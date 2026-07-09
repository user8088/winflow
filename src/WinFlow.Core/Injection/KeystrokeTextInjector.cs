using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by typing Unicode keystrokes via SendInput (KEYEVENTF_UNICODE).
/// Batches characters into fewer SendInput calls so injection finishes before
/// the user can interleave real keypresses, and attaches to the foreground
/// window's input thread so Electron apps (Cursor, VS Code) receive events.
/// </summary>
public sealed class KeystrokeTextInjector : ITextInjector
{
    private const ushort VkReturn = 0x0D;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyup = 0x0002;
    private const int MaxInputsPerSend = 128;

    public Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        if (ElevatedTargetDetector.IsInjectionBlockedByUipi())
        {
            ClipboardHelper.SetText(text);
            throw new InvalidOperationException(ElevatedTargetDetector.BlockedMessage);
        }

        var pending = new List<NativeInput.Input>(Math.Min(text.Length * 2, MaxInputsPerSend));
        var unitsInPending = 0;
        var unitsInjected = 0;

        for (int i = 0; i < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                pending.AddRange(ReturnInputs());
                unitsInPending++;
                i += 2;
            }
            else if (c is '\r' or '\n')
            {
                pending.AddRange(ReturnInputs());
                unitsInPending++;
                i++;
            }
            else if (char.IsSurrogatePair(text, i) && i + 1 < text.Length)
            {
                char low = text[i + 1];
                pending.Add(MakeUnicodeKey(c, down: true));
                pending.Add(MakeUnicodeKey(low, down: true));
                pending.Add(MakeUnicodeKey(low, down: false));
                pending.Add(MakeUnicodeKey(c, down: false));
                unitsInPending++;
                i += 2;
            }
            else
            {
                pending.Add(MakeUnicodeKey(c, down: true));
                pending.Add(MakeUnicodeKey(c, down: false));
                unitsInPending++;
                i++;
            }

            if (pending.Count >= MaxInputsPerSend)
            {
                unitsInjected += Flush(pending, ref unitsInPending, unitsInjected);
            }
        }

        if (pending.Count > 0)
        {
            unitsInjected += Flush(pending, ref unitsInPending, unitsInjected);
        }

        return Task.CompletedTask;
    }

    private static int Flush(
        List<NativeInput.Input> pending,
        ref int unitsInPending,
        int unitsInjectedSoFar)
    {
        int unitCount = unitsInPending;
        var batch = pending.ToArray();
        pending.Clear();
        unitsInPending = 0;

        uint sent = NativeInput.Send(batch);
        if (sent != batch.Length)
        {
            int error = Marshal.GetLastWin32Error();
            int partialUnits = unitsInjectedSoFar > 0 || sent > 0
                ? unitsInjectedSoFar + Math.Max(1, (int)(sent / 2))
                : 0;

            if (partialUnits > 0)
            {
                throw new PartialTextInjectionException(partialUnits, error);
            }

            throw new InvalidOperationException($"SendInput failed (error {error}).");
        }

        return unitCount;
    }

    private static IEnumerable<NativeInput.Input> ReturnInputs() =>
    [
        MakeReturnKey(down: true),
        MakeReturnKey(down: false),
    ];

    private static NativeInput.Input MakeUnicodeKey(char codeUnit, bool down) => new()
    {
        Type = 1,
        VirtualKey = 0,
        ScanCode = codeUnit,
        Flags = KeyeventfUnicode | (down ? 0 : KeyeventfKeyup),
    };

    private static NativeInput.Input MakeReturnKey(bool down) => new()
    {
        Type = 1,
        VirtualKey = VkReturn,
        Flags = down ? 0 : KeyeventfKeyup,
    };
}
