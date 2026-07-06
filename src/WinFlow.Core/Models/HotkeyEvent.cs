using System.Diagnostics;

namespace WinFlow.Core.Models;

public enum HotkeyEventKind
{
    Pressed,
    Released,
}

/// <summary>
/// A push-to-talk hotkey transition. <see cref="Timestamp"/> is a
/// <see cref="Stopwatch.GetTimestamp"/> value captured inside the keyboard
/// hook callback, so latency can be measured from the actual key event.
/// </summary>
public readonly record struct HotkeyEvent(HotkeyEventKind Kind, long Timestamp)
{
    public static HotkeyEvent Pressed() => new(HotkeyEventKind.Pressed, Stopwatch.GetTimestamp());

    public static HotkeyEvent Released() => new(HotkeyEventKind.Released, Stopwatch.GetTimestamp());
}
