namespace WinFlow.Core.Models;

/// <summary>How finished text is delivered to the focused application.</summary>
public enum InputMethod
{
    /// <summary>Pick per target: type into terminals/Electron, paste elsewhere.</summary>
    Auto,

    /// <summary>Clipboard + synthetic Ctrl+V. Fast, preserves formatting, but
    /// unreliable in Electron terminals (Cursor, VS Code) and some consoles.</summary>
    Paste,

    /// <summary>Type each character via SendInput Unicode keystrokes. Slower
    /// but works in terminals/Electron where synthetic paste fails.</summary>
    Type,
}
