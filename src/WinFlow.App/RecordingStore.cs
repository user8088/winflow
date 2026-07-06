using System.IO;
using WinFlow.Core.Audio;
using WinFlow.Core.Models;

namespace WinFlow.App;

/// <summary>
/// Persists completed takes as WAV files under %APPDATA%\WinFlow\recordings.
/// </summary>
public sealed class RecordingStore
{
    public string RecordingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinFlow",
        "recordings");

    public string Save(CapturedAudio audio)
    {
        Directory.CreateDirectory(RecordingsDirectory);

        string path = Path.Combine(
            RecordingsDirectory,
            $"winflow-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

        File.WriteAllBytes(path, WavEncoder.Encode(audio.Pcm16, audio.SampleRate));
        return path;
    }
}
