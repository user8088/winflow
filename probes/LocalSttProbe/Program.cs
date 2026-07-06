// Dev-only probe: downloads the on-device Parakeet model and transcribes a
// WAV with the local engine, proving the sherpa-onnx config, audio format
// handoff, and threading are correct end-to-end. Not shipped.
//
//   dotnet run --project probes/LocalSttProbe -- "<path-to.wav>"
using System.Diagnostics;
using System.IO;
using WinFlow.Core.Local;
using WinFlow.Core.Local.Models;
using WinFlow.Core.Models;

if (args.Length == 0)
{
    string recDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinFlow", "recordings");
    args = Directory.Exists(recDir)
        ? new[] { Directory.GetFiles(recDir, "*.wav").OrderByDescending(File.GetLastWriteTime).First() }
        : throw new InvalidOperationException("Pass a WAV path, or record one first.");
}

string wavPath = args[0];
Console.WriteLine($"Transcribing: {wavPath}");

byte[] wav = File.ReadAllBytes(wavPath);
// Strip the 44-byte canonical RIFF header written by WavEncoder.
byte[] pcm16 = wav[44..];

var model = LocalModelCatalog.Default;
using var manager = new LocalModelManager();
Console.WriteLine($"Model dir: {manager.ModelDirectory(model)}");

if (!manager.IsInstalled(model))
{
    Console.WriteLine($"Downloading {model.DisplayName} (~{model.TotalBytes / (1024 * 1024)} MB)…");
    var sw = Stopwatch.StartNew();
    manager.ProgressChanged += (fraction, detail) =>
        Console.WriteLine($"  {fraction * 100,5:F1}%  {detail}");
    await manager.EnsureInstalledAsync(model);
    Console.WriteLine($"Downloaded in {sw.Elapsed.TotalSeconds:F1}s");
}
else
{
    Console.WriteLine("Model already installed.");
}

var engine = new SherpaOnnxSttEngine(manager, model);

Console.WriteLine("Warming up model (first load)…");
var warm = Stopwatch.StartNew();
// First transcription loads the encoder; discard the result timing.
_ = await engine.TranscribeAsync(new CapturedAudio(pcm16, 24000, TimeSpan.Zero, 0.05f));
Console.WriteLine($"Model warm in {warm.Elapsed.TotalSeconds:F1}s");

var t = Stopwatch.StartNew();
string transcript = await engine.TranscribeAsync(new CapturedAudio(pcm16, 24000, TimeSpan.Zero, 0.05f));
Console.WriteLine($"Transcribed in {t.Elapsed.TotalSeconds:F2}s");
Console.WriteLine();
Console.WriteLine($"TRANSCRIPT: \"{transcript}\"");
