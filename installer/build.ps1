# Builds the WinFlow release installer.
#
# Prereqs: .NET 10 SDK, Inno Setup 6, and both models present in .\models
# (download them once through the app, or copy the folders in).
#
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0

param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# --- 1. Verify the models are present and the expected size -----------------
$models = @(
    @{ Dir = "parakeet-tdt-0.6b-v2-int8"; Files = @{
        "encoder.int8.onnx" = 652184296
        "decoder.int8.onnx" = 7257753
        "joiner.int8.onnx"  = 1739080
        "tokens.txt"        = 9384 } },
    @{ Dir = "qwen2.5-0.5b-instruct-q4-k-m"; Files = @{
        "qwen2.5-0.5b-instruct-q4_k_m.gguf" = 491400032 } }
)

foreach ($model in $models) {
    $dir = Join-Path $root "models\$($model.Dir)"
    foreach ($name in $model.Files.Keys) {
        $path = Join-Path $dir $name
        if (-not (Test-Path $path)) {
            throw "Missing model file: $path. Download the models through the app first (they land in %LOCALAPPDATA%\WinFlow\models), then copy the folders into .\models."
        }
        $actual = (Get-Item $path).Length
        $expected = $model.Files[$name]
        if ($actual -ne $expected) {
            throw "Size mismatch for ${path}: expected $expected bytes, found $actual. Re-download the model."
        }
    }
    # The app treats a model as installed only when .verified exists.
    $verified = Join-Path $dir ".verified"
    if (-not (Test-Path $verified)) {
        Set-Content -Path $verified -Value $model.Dir -NoNewline -Encoding ascii
    }
}
Write-Host "Models verified." -ForegroundColor Green

# --- 2. Publish the app (self-contained: users don't need .NET) -------------
# LLamaSharp.Backend.Cpu ships four CPU variants of the same DLLs
# (avx/avx2/avx512/noavx), which collide when publish flattens native files
# (NETSDK1152). Allow the duplicates, then restore the runtimes\<variant>
# layout below so LLamaSharp picks the right variant on the customer's CPU.
$publishDir = Join-Path $PSScriptRoot "publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish "$root\src\WinFlow.App" -c Release -r win-x64 --self-contained true `
    -p:ErrorOnDuplicatePublishOutputFiles=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$nugetRoot = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$llamaNative = Get-ChildItem "$nugetRoot\llamasharp.backend.cpu\*\runtimes\win-x64\native" |
    Sort-Object FullName | Select-Object -Last 1
if (-not $llamaNative) { throw "LLamaSharp.Backend.Cpu natives not found under $nugetRoot." }

# Drop the arbitrarily-picked flattened copies from the publish root...
$flattened = Get-ChildItem $llamaNative.FullName -Recurse -File | Select-Object -ExpandProperty Name -Unique
foreach ($name in $flattened) {
    $stray = Join-Path $publishDir $name
    if (Test-Path $stray) { Remove-Item $stray -Force }
}
# ...and ship the full per-variant tree where LLamaSharp's loader looks.
$nativeDest = Join-Path $publishDir "runtimes\win-x64\native"
New-Item -ItemType Directory -Force $nativeDest | Out-Null
Copy-Item "$($llamaNative.FullName)\*" $nativeDest -Recurse -Force
Write-Host "Publish done." -ForegroundColor Green

# --- 3. Compile both installer flavors --------------------------------------
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

$iss = Join-Path $PSScriptRoot "WinFlow.iss"

& $iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (Lite)." }

& $iscc "/DMyAppVersion=$Version" "/DBundleModels" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (Full)." }

foreach ($name in "WinFlow-Setup-$Version.exe", "WinFlow-Setup-Full-$Version.exe") {
    $output = Join-Path $PSScriptRoot "Output\$name"
    Write-Host "Installer ready: $output ($([math]::Round((Get-Item $output).Length / 1MB)) MB)" -ForegroundColor Green
}
