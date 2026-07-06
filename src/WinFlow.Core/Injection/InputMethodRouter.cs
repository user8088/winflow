using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Injection;

/// <summary>
/// Routes injection to <see cref="InputMethod.Paste"/> or <see cref="InputMethod.Type"/>
/// based on a user setting, with an <see cref="InputMethod.Auto"/> mode that detects
/// terminals / Electron editors and types into them (synthetic Ctrl+V is unreliable
/// in xterm.js-based terminals like Cursor and VS Code's integrated terminal).
/// </summary>
public sealed class InputMethodRouter : ITextInjector
{
    private readonly ITextInjector _pasteInjector;
    private readonly ITextInjector _typeInjector;

    /// <summary>Current selection. <see cref="InputMethod.Auto"/> detects per-target.</summary>
    public InputMethod Method { get; set; }

    public InputMethodRouter(ITextInjector pasteInjector, ITextInjector typeInjector, InputMethod method)
    {
        _pasteInjector = pasteInjector;
        _typeInjector = typeInjector;
        Method = method;
    }

    public Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        ITextInjector injector = Resolve() == InputMethod.Type ? _typeInjector : _pasteInjector;
        return injector.InjectAsync(text, cancellationToken);
    }

    private InputMethod Resolve() => Method switch
    {
        InputMethod.Paste => InputMethod.Paste,
        InputMethod.Type => InputMethod.Type,
        _ => IsTerminalLikeTarget() ? InputMethod.Type : InputMethod.Paste,
    };

    /// <summary>
    /// True when the focused window belongs to a terminal or Electron-based editor,
    /// where synthetic paste is unreliable and keystroke typing should be used instead.
    /// </summary>
    private static bool IsTerminalLikeTarget()
    {
        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == 0)
            {
                return false;
            }

            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == 0)
            {
                return false;
            }

            string name = Process.GetProcessById((int)pid).ProcessName;
            return TerminalProcessNames.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    // Electron editors/terminals and classic consoles. The Cursor/Code entries
    // cover their integrated terminals (typing in the editor itself also works
    // fine with keystrokes, so this is a safe over-approximation).
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cursor", "Code", "Code - Insiders", "VSCodium",
        "WindowsTerminal", "OpenConsole", "conhost",
        "cmd", "powershell", "pwsh", "pwsh-preview",
        "mintty", "alacritty", "wezterm-gui", "hyper", "Tabby",
        "FluentTerminal", "Terminus", "kitty", "foot", "warp",
    };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
