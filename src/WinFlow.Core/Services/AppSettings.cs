using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>User preferences, persisted as JSON under %APPDATA%\WinFlow.</summary>
public sealed record AppSettings
{
    /// <summary>The settings schema version written by this build of the app.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// On-disk schema version, for future migrations. Files written before
    /// versioning existed have no field and deserialize as version 1.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public SttMode SttMode { get; init; } = SttMode.Auto;

    /// <summary>True for headsets/near-field mics (near_field noise reduction); false for laptop built-ins.</summary>
    public bool NearFieldMic { get; init; } = true;

    public string? Language { get; init; }

    /// <summary>
    /// Where the on-device model lives. Null = default (%LOCALAPPDATA%\WinFlow\models).
    /// Let the user choose via the UI rather than relying on an env var.
    /// </summary>
    public string? ModelDirectory { get; init; }

    /// <summary>How text is delivered to the focused app (Auto detects terminals).</summary>
    public InputMethod InputMethod { get; init; } = InputMethod.Auto;

    /// <summary>How dictated transcripts are cleaned up before injection.</summary>
    public CorrectionMode CorrectionMode { get; init; } = CorrectionMode.AutoCorrect;
}
