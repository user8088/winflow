using WinFlow.Core.Models;

namespace WinFlow.Core.Abstractions;

/// <summary>
/// Captures microphone audio and delivers it as 16-bit mono 24 kHz PCM —
/// both incrementally (for streaming transcription and HUD metering) and
/// as a complete take (for the batch fallback and WAV persistence).
/// </summary>
public interface IAudioProvider : IDisposable
{
    /// <summary>Raised on the capture thread for each converted chunk.</summary>
    event Action<AudioChunk>? ChunkAvailable;

    /// <summary>Opens the default capture device and starts recording.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops recording and returns everything captured since <see cref="StartAsync"/>.</summary>
    Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default);
}
