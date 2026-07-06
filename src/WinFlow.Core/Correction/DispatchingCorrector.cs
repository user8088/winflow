using WinFlow.Core.Abstractions;
using WinFlow.Core.Local;

namespace WinFlow.Core.Correction;

/// <summary>
/// Routes correction to cloud or local backend, mirroring <see cref="DispatchingSttProvider"/>.
/// </summary>
public sealed class DispatchingCorrector : ITranscriptCorrector
{
    private readonly SttModeController _controller;
    private readonly ITranscriptCorrector _cloud;
    private readonly ITranscriptCorrector? _local;

    public DispatchingCorrector(
        SttModeController controller,
        ITranscriptCorrector cloud,
        ITranscriptCorrector? local)
    {
        _controller = controller;
        _cloud = cloud;
        _local = local;
    }

    public Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default)
    {
        if (_controller.ResolvedBackend == SttBackend.Local && _local is not null)
        {
            return _local.CorrectAsync(transcript, intensity, cancellationToken);
        }

        return _cloud.CorrectAsync(transcript, intensity, cancellationToken);
    }
}
