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

    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            ITextInjector injector = Resolve() == InputMethod.Type ? _typeInjector : _pasteInjector;
            await injector.InjectAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Let the target app finish handling paste/keystrokes before releasing
            // modifiers — an immediate ReleaseAll can cancel Ctrl before V is processed.
            await Task.Delay(50).ConfigureAwait(false);
            ModifierRelease.ReleaseAll();
        }
    }

    private InputMethod Resolve() => Method switch
    {
        InputMethod.Paste => InputMethod.Paste,
        InputMethod.Type => InputMethod.Type,
        _ => PrefersKeystrokeInjection() ? InputMethod.Type : InputMethod.Paste,
    };

    /// <summary>
    /// True when synthetic Ctrl+V is unreliable (terminals, Electron, browsers).
    /// Those apps often swallow the Ctrl modifier but still accept the V key,
    /// which produces a lone "v" instead of a paste — or block SendInput entirely.
    /// </summary>
    private static bool PrefersKeystrokeInjection()
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
            return KeystrokeInjectionProcessNames.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    // Terminals, Electron apps, and browsers where synthetic Ctrl+V often drops the
    // Ctrl modifier (user sees a lone "v") or is blocked entirely. Unicode typing
    // via KEYEVENTF_UNICODE works in their text fields.
    private static readonly HashSet<string> KeystrokeInjectionProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Terminals & shells
        "Cursor", "Code", "Code - Insiders", "VSCodium", "Claude",
        "WindowsTerminal", "OpenConsole", "conhost",
        "cmd", "powershell", "pwsh", "pwsh-preview",
        "mintty", "alacritty", "wezterm-gui", "hyper", "Tabby",
        "FluentTerminal", "Terminus", "kitty", "foot", "warp",
        // Chat & collaboration (Electron)
        "Slack", "Discord", "Teams", "ms-teams", "Zoom", "Telegram",
        // Browsers
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "Arc",
        // Other Electron / web wrappers
        "Notion", "WhatsApp", "Signal", "Postman", "figma", "Linear",
    };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
