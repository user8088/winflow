namespace WinFlow.Core.Models;

/// <summary>
/// The complete audio of one dictation, in the pipeline's canonical format
/// (16-bit signed PCM, mono, <see cref="SampleRate"/> Hz).
/// </summary>
public sealed record CapturedAudio(byte[] Pcm16, int SampleRate, TimeSpan Duration, float PeakRms)
{
    public bool IsEmpty => Pcm16.Length == 0;
}
