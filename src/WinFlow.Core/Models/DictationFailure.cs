namespace WinFlow.Core.Models;

public enum DictationFailureKind
{
    /// <summary>Audio capture could not start or stop.</summary>
    CaptureFailed,

    /// <summary>The take was too short or silent; nothing was sent anywhere.</summary>
    NoSpeech,

    /// <summary>Both the streaming and batch transcription paths failed.</summary>
    TranscriptionFailed,

    /// <summary>A transcript exists but couldn't be pasted; it remains on the clipboard.</summary>
    InjectionFailed,
}

public sealed record DictationFailure(
    DictationFailureKind Kind,
    string Message,
    string? Transcript = null);
