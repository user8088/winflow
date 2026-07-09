using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by placing it on the clipboard and pasting via WM_PASTE or
/// Shift+Insert. Avoids synthetic Ctrl+V, which many Electron apps and browsers
/// mishandle (Ctrl is dropped and only the letter "v" is typed).
/// </summary>
public sealed class ClipboardTextInjector : ITextInjector
{
    /// <summary>
    /// How long the target app gets to service the paste before the
    /// previous clipboard contents are restored.
    /// </summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(300);

    private const ushort VkShift = 0x10;
    private const ushort VkInsert = 0x2D;
    private const ushort VkLControl = 0xA2;
    private const ushort VkV = 0x56;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfScancode = 0x0008;

    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (ElevatedTargetDetector.IsInjectionBlockedByUipi())
        {
            ClipboardHelper.SetText(text);
            throw new InvalidOperationException(ElevatedTargetDetector.BlockedMessage);
        }

        ClipboardSnapshot? snapshot = ClipboardHelper.TrySnapshot();

        uint sequenceAfterWrite;
        try
        {
            sequenceAfterWrite = ClipboardHelper.SetText(text);
            PasteFromClipboard();
        }
        catch when (TryRestoreSnapshot(snapshot))
        {
            throw;
        }

        await Task.Delay(RestoreDelay, cancellationToken).ConfigureAwait(false);

        if (ClipboardHelper.GetSequenceNumber() != sequenceAfterWrite)
        {
            return;
        }

        if (snapshot is { IsEmpty: false })
        {
            ClipboardHelper.TryRestore(snapshot);
        }
        else if (snapshot is { IsEmpty: true })
        {
            ClipboardHelper.TryClear();
        }
    }

    private static bool TryRestoreSnapshot(ClipboardSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            try
            {
                ClipboardHelper.TryRestore(snapshot);
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Pastes via Shift+Insert (standard Windows paste, no "v" key). Falls back to
    /// left-Ctrl+V only if SendInput fails entirely.
    /// </summary>
    private static void PasteFromClipboard()
    {
        SendShiftInsert();
    }

    private static void SendShiftInsert()
    {
        NativeInput.Input[] sequence =
        [
            MakeKey(VkShift, down: true),
            MakeKey(VkInsert, down: true),
            MakeKey(VkInsert, down: false),
            MakeKey(VkShift, down: false),
        ];

        if (NativeInput.Send(sequence) == sequence.Length)
        {
            return;
        }

        SendLeftCtrlV();
    }

    private static void SendLeftCtrlV()
    {
        NativeInput.Input[] sequence =
        [
            MakeKey(VkLControl, down: true),
            MakeKey(VkV, down: true),
            MakeKey(VkV, down: false),
            MakeKey(VkLControl, down: false),
        ];

        uint injected = NativeInput.Send(sequence);
        if (injected != sequence.Length)
        {
            int error = Marshal.GetLastWin32Error();
            if (injected >= 1)
            {
                NativeInput.Input[] release = [MakeKey(VkLControl, down: false)];
                NativeInput.Send(release);
            }

            throw new InvalidOperationException($"SendInput failed (error {error}).");
        }
    }

    private static NativeInput.Input MakeKey(ushort virtualKey, bool down)
    {
        ushort scan = (ushort)MapVirtualKey(virtualKey, 0);
        return new NativeInput.Input
        {
            Type = 1,
            VirtualKey = virtualKey,
            ScanCode = scan,
            Flags = (down ? 0 : KeyeventfKeyup) | (scan != 0 ? KeyeventfScancode : 0),
        };
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
