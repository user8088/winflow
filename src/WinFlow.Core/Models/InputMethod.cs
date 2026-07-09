namespace WinFlow.Core.Models;

/// <summary>How the finished transcript is delivered into the focused app.</summary>
public enum InputMethod
{
    /// <summary>Copy to clipboard and send Ctrl+V. Fast; best for editors and browsers.</summary>
    Paste,

    /// <summary>Synthesize per-character keystrokes. Best for terminals and apps that filter paste.</summary>
    Type,

    /// <summary>Detect terminal-like targets and type into them; paste everywhere else.</summary>
    Auto,
}
