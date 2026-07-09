using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>
/// Thread-safe state machine for the dictation session lifecycle.
///
/// Allowed transitions:
///   Idle → Recording            (hotkey pressed)
///   Recording → Processing      (hotkey released)
///   Recording → Idle            (cancelled / capture failed)
///   Processing → Idle           (completed or failed)
/// </summary>
public sealed class RecordingCoordinator
{
    private static readonly (RecordingState From, RecordingState To)[] AllowedTransitions =
    [
        (RecordingState.Idle, RecordingState.Recording),
        (RecordingState.Recording, RecordingState.Processing),
        (RecordingState.Recording, RecordingState.Idle),
        (RecordingState.Processing, RecordingState.Idle),
    ];

    private readonly Lock _gate = new();
    private RecordingState _state = RecordingState.Idle;

    public event Action<RecordingState>? StateChanged;

    public RecordingState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Moves from <paramref name="from"/> to <paramref name="to"/> if and only
    /// if the machine is currently in <paramref name="from"/> and the pair is
    /// an allowed transition. Returns false otherwise (no state change).
    /// </summary>
    public bool TryTransition(RecordingState from, RecordingState to)
    {
        lock (_gate)
        {
            if (_state != from || !AllowedTransitions.Contains((from, to)))
            {
                return false;
            }

            _state = to;
        }

        StateChanged?.Invoke(to);
        return true;
    }

    /// <summary>Forces the machine back to Idle from any state (error recovery).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_state == RecordingState.Idle)
            {
                return;
            }

            _state = RecordingState.Idle;
        }

        StateChanged?.Invoke(RecordingState.Idle);
    }
}
