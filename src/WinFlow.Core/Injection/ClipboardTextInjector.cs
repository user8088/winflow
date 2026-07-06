using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by placing it on the clipboard, synthesizing Ctrl+V, and
/// restoring the previous clipboard text afterwards. The broadest-compat
/// strategy — works in terminals, browsers, and Electron apps where
/// programmatic text APIs don't.
///
/// M1 limitation: only CF_UNICODETEXT is saved/restored, so a copied
/// image or file list is lost. The strategy table in M2 narrows how often
/// this path runs at all.
/// </summary>
public sealed class ClipboardTextInjector : ITextInjector
{
    /// <summary>
    /// How long the target app gets to service the paste before the
    /// previous clipboard text is restored.
    /// </summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(300);

    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        string? previous = ClipboardHelper.TryGetText();

        ClipboardHelper.SetText(text);
        SendCtrlV();

        await Task.Delay(RestoreDelay, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(previous))
        {
            try
            {
                ClipboardHelper.SetText(previous);
            }
            catch
            {
                // Restoring is best-effort; the injected text stays on the
                // clipboard, which is a tolerable failure mode.
            }
        }
    }

    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint KeyeventfKeyup = 0x0002;

    private static void SendCtrlV()
    {
        Input[] sequence =
        [
            MakeKey(VkControl, down: true),
            MakeKey(VkV, down: true),
            MakeKey(VkV, down: false),
            MakeKey(VkControl, down: false),
        ];

        if (SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<Input>()) != sequence.Length)
        {
            throw new InvalidOperationException(
                $"SendInput failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static Input MakeKey(ushort virtualKey, bool down) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        VirtualKey = virtualKey,
        Flags = down ? 0 : KeyeventfKeyup,
    };

    // INPUT is a 40-byte tagged union on x64; explicit layout keeps the
    // keyboard fields at the union offset without defining MOUSEINPUT.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
