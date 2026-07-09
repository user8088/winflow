# Optional WinFlow uninstall helper — removes the API key from Credential Manager.
# Settings/recordings under %APPDATA%\WinFlow are removed by the installer itself.
param([switch]$RemoveCredential)

if (-not $RemoveCredential) {
    exit 0
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinFlowCredDelete {
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredDelete(string target, int type, int flags);
}
"@

# CRED_TYPE_GENERIC = 1; ignore if the credential is already gone.
[void][WinFlowCredDelete]::CredDelete('WinFlow/OpenAI', 1, 0)
exit 0
