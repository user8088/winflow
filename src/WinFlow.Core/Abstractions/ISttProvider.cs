using WinFlow.Core.Models;

namespace WinFlow.Core.Abstractions;

/// <summary>
/// One live streaming transcription session: audio goes in while the user
/// speaks, a transcript comes out after <see cref="FinishAsync"/> commits.
/// </summary>
public interface IStreamingSttSession : IAsyncDisposable
{
    /// <summary>Forwards a chunk of 16-bit mono 24 kHz PCM.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);

    /// <summary>Commits the audio buffer and waits for the final transcript.</summary>
    Task<string> FinishAsync(CancellationToken cancellationToken = default);
}

/// <summary>Opens streaming transcription sessions (e.g. OpenAI Realtime API).</summary>
public interface IStreamingSttProvider
{
    Task<IStreamingSttSession> OpenSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Whole-take transcription (e.g. POST /v1/audio/transcriptions). Used as
/// the fallback racer when streaming stalls or fails.
/// </summary>
public interface IBatchSttProvider
{
    Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default);
}
