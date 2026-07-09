using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Local;

/// <summary>
/// Resolves the configured <see cref="SttMode"/> to a concrete provider at
/// call time, so the user can switch Cloud ⇄ Local from the tray without
/// rebuilding the pipeline.
/// </summary>
public sealed class SttModeController
{
    private SttMode _mode;
    private readonly Func<bool> _cloudAvailable;
    private readonly Func<bool> _localAvailable;

    public SttModeController(SttMode mode, Func<bool> cloudAvailable, Func<bool> localAvailable)
    {
        _mode = mode;
        _cloudAvailable = cloudAvailable;
        _localAvailable = localAvailable;
    }

    /// <summary>
    /// The mode the user chose. Mutations go through <see cref="Apply"/> so
    /// that <see cref="BackendChanged"/> fires when the resolution flips.
    /// </summary>
    public SttMode ConfiguredMode => _mode;

    /// <summary>The effective provider backend after resolving Auto.</summary>
    public SttBackend ResolvedBackend => _mode switch
    {
        SttMode.Cloud => SttBackend.Cloud,
        SttMode.Local => SttBackend.Local,
        _ => ResolveAuto(),
    };

    private SttBackend ResolveAuto()
    {
        if (_localAvailable())
        {
            return SttBackend.Local;
        }

        if (_cloudAvailable())
        {
            return SttBackend.Cloud;
        }

        // Neither backend is usable (no model, no API key). SttBackend has no
        // 'none' value, so keep the historical Cloud fallback: the dictation
        // attempt fails with a visible toast instead of routing to a missing
        // local model.
        return SttBackend.Cloud;
    }

    public event Action<SttBackend>? BackendChanged;

    public void Apply(SttMode mode)
    {
        SttBackend before = ResolvedBackend;
        _mode = mode;
        SttBackend after = ResolvedBackend;
        if (before != after)
        {
            BackendChanged?.Invoke(after);
        }
    }

    public void NotifyLocalAvailabilityChanged()
    {
        // Re-resolve in case Auto should flip now that the model is/isn't present.
        BackendChanged?.Invoke(ResolvedBackend);
    }
}

public enum SttBackend { Cloud, Local }

/// <summary>
/// Presents a single <see cref="IStreamingSttProvider"/> + <see cref="IBatchSttProvider"/>
/// pair to the pipeline while routing each call to the active backend.
///
/// In Local mode there is no streaming path; the streaming session is a
/// no-op that returns an empty transcript, so the pipeline's race lets the
/// batch (on-device) result win. This mirrors how the pipeline already
/// handles a batch-only configuration.
/// </summary>
public sealed class DispatchingSttProvider : IStreamingSttProvider, IBatchSttProvider
{
    private readonly SttModeController _controller;
    private readonly IStreamingSttProvider _cloudStreaming;
    private readonly IBatchSttProvider _cloudBatch;
    private readonly IBatchSttProvider? _localBatch;

    public DispatchingSttProvider(
        SttModeController controller,
        IStreamingSttProvider cloudStreaming,
        IBatchSttProvider cloudBatch,
        IBatchSttProvider? localBatch)
    {
        _controller = controller;
        _cloudStreaming = cloudStreaming;
        _cloudBatch = cloudBatch;
        _localBatch = localBatch;
    }

    public Task<IStreamingSttSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        return _controller.ResolvedBackend == SttBackend.Local
            ? Task.FromResult<IStreamingSttSession>(NullStreamingSession.Instance)
            : _cloudStreaming.OpenSessionAsync(cancellationToken);
    }

    public Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        return _controller.ResolvedBackend == SttBackend.Local && _localBatch is not null
            ? _localBatch.TranscribeAsync(audio, cancellationToken)
            : _cloudBatch.TranscribeAsync(audio, cancellationToken);
    }

    /// <summary>
    /// Streaming stand-in for local mode: accepts audio (discarded), returns
    /// an empty transcript on finish so the batch racer wins.
    /// </summary>
    private sealed class NullStreamingSession : IStreamingSttSession
    {
        public static readonly NullStreamingSession Instance = new();
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<string> FinishAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
