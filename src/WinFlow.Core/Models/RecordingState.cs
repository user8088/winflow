namespace WinFlow.Core.Models;

/// <summary>
/// Phases of a dictation session, driven by <see cref="Services.RecordingCoordinator"/>.
/// </summary>
public enum RecordingState
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>Hotkey held; audio is being captured.</summary>
    Recording,

    /// <summary>Hotkey released; audio is being finalized/transcribed.</summary>
    Processing,
}
