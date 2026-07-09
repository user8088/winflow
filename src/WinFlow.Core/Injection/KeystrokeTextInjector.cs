using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by typing each character as a Unicode keystroke via SendInput
/// (KEYEVENTF_UNICODE). This bypasses the clipboard entirely, so it works in
/// terminals and Electron apps (Cursor, VS Code integrated terminal) where a
/// synthetic Ctrl+V paste is dropped.
///
/// Line breaks (\r\n, \r, \n) are sent as Enter (VK_RETURN) keystrokes so
/// multi-line transcripts preserve their structure in the target field.
/// </summary>
public sealed class KeystrokeTextInjector : ITextInjector
{
    private const ushort VkReturn = 0x0D;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyup = 0x0002;

    public Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(text))
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

        int unitsInjected = 0;
        for (int i = 0; i < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NativeInput.Input[] inputs;
            int step;

            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                inputs = ReturnInputs();
                step = 2;
            }
            else if (c is '\r' or '\n')
            {
                inputs = ReturnInputs();
                step = 1;
            }
            else if (char.IsSurrogatePair(text, i) && i + 1 < text.Length)
            {
                char low = text[i + 1];
                inputs =
                [
                    MakeUnicodeKey(c, down: true),
                    MakeUnicodeKey(low, down: true),
                    MakeUnicodeKey(low, down: false),
                    MakeUnicodeKey(c, down: false),
                ];
                step = 2;
            }
            else
            {
                inputs =
                [
                    MakeUnicodeKey(c, down: true),
                    MakeUnicodeKey(c, down: false),
                ];
                step = 1;
            }

            if (NativeInput.Send(inputs) != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                if (unitsInjected > 0)
                {
                    throw new PartialTextInjectionException(unitsInjected, error);
                }

                throw new InvalidOperationException($"SendInput failed (error {error}).");
            }

            unitsInjected++;
            i += step;
        }

        return Task.CompletedTask;
    }

    private static NativeInput.Input[] ReturnInputs() =>
    [
        MakeReturnKey(down: true),
        MakeReturnKey(down: false),
    ];

    private static NativeInput.Input MakeUnicodeKey(char codeUnit, bool down) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        VirtualKey = 0,
        ScanCode = codeUnit,
        Flags = KeyeventfUnicode | (down ? 0 : KeyeventfKeyup),
    };

    private static NativeInput.Input MakeReturnKey(bool down) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        VirtualKey = VkReturn,
        Flags = down ? 0 : KeyeventfKeyup,
    };
}
