using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>Runtime correction mode, switchable from the tray without restarting.</summary>
public sealed class CorrectionModeController
{
    private CorrectionMode _mode;

    public CorrectionModeController(CorrectionMode mode) => _mode = mode;

    public CorrectionMode Mode
    {
        get => _mode;
        set => _mode = value;
    }
}
