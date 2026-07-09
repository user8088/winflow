using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by typing each character as a Unicode keystroke via SendInput
/// (KEYEVENTF_UNICODE). This bypasses the clipboard entirely, so it works in
/// terminals and Electron apps (Cursor, VS Code integrated terminal) where a
/// synthetic Ctrl+V paste is dropped.
///
/// Newlines are collapsed to spaces so a multi-line transcript never submits
/// a terminal prompt or chat box mid-text.
/// </summary>
public sealed class KeystrokeTextInjector : ITextInjector
{
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyup = 0x0002;

    public Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string safe = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (safe.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (ElevatedTargetDetector.IsInjectionBlockedByUipi())
        {
            // UIPI would silently drop every synthesized keystroke. Leave the
            // transcript on the clipboard so nothing is lost.
            ClipboardHelper.SetText(text);
            throw new InvalidOperationException(ElevatedTargetDetector.BlockedMessage);
        }

        Input[] inputs = BuildInputs(safe);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput failed (error {Marshal.GetLastWin32Error()}).");
        }

        return Task.CompletedTask;
    }

    private static Input[] BuildInputs(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (char.IsSurrogatePair(text, i) && i + 1 < text.Length)
            {
                // Non-BMP code point: send both surrogates as a paired sequence.
                char low = text[i + 1];
                AddKey(inputs, c, down: true);
                AddKey(inputs, low, down: true);
                AddKey(inputs, low, down: false);
                AddKey(inputs, c, down: false);
                i += 2;
            }
            else
            {
                AddKey(inputs, c, down: true);
                AddKey(inputs, c, down: false);
                i += 1;
            }
        }

        return inputs.ToArray();
    }

    private static void AddKey(List<Input> inputs, char codeUnit, bool down) => inputs.Add(new Input
    {
        Type = 1, // INPUT_KEYBOARD
        VirtualKey = 0,
        ScanCode = codeUnit,
        Flags = KeyeventfUnicode | (down ? 0 : KeyeventfKeyup),
    });

    // INPUT is a 40-byte tagged union on x64; explicit layout places the
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
