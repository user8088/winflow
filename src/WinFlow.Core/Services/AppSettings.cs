using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>User preferences, persisted as JSON under %APPDATA%\WinFlow.</summary>
public sealed record AppSettings
{
    public SttMode SttMode { get; init; } = SttMode.Auto;

    /// <summary>True for headsets/near-field mics (near_field noise reduction); false for laptop built-ins.</summary>
    public bool NearFieldMic { get; init; } = true;

    public string? Language { get; init; }
}
