using System.Runtime.InteropServices;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Injection;

/// <summary>
/// Injects text by placing it on the clipboard, synthesizing Ctrl+V, and
/// restoring the previous clipboard contents afterwards. The broadest-compat
/// strategy — works in terminals, browsers, and Electron apps where
/// programmatic text APIs don't. All duplicable clipboard formats (images,
/// file lists, HTML/RTF, registered formats) are snapshotted and restored,
/// not just plain text.
/// </summary>
public sealed class ClipboardTextInjector : ITextInjector
{
    /// <summary>
    /// How long the target app gets to service the paste before the
    /// previous clipboard contents are restored.
    /// </summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(300);

    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (ElevatedTargetDetector.IsInjectionBlockedByUipi())
        {
            // UIPI would silently drop the synthesized Ctrl+V. Leave the
            // transcript on the clipboard (no restore) so nothing is lost.
            ClipboardHelper.SetText(text);
            throw new InvalidOperationException(ElevatedTargetDetector.BlockedMessage);
        }

        ClipboardSnapshot? snapshot = ClipboardHelper.TrySnapshot();

        uint sequenceAfterWrite;
        try
        {
            sequenceAfterWrite = ClipboardHelper.SetText(text);
            SendCtrlV();
        }
        catch when (TryRestoreSnapshot(snapshot))
        {
            // TryRestoreSnapshot always returns false: restore the user's
            // clipboard (best effort) and let the original exception fly.
            throw;
        }

        await Task.Delay(RestoreDelay, cancellationToken).ConfigureAwait(false);

        if (ClipboardHelper.GetSequenceNumber() != sequenceAfterWrite)
        {
            // Someone else wrote to the clipboard during the paste window;
            // they own it now, so restoring would clobber their content.
            return;
        }

        if (snapshot is { IsEmpty: false })
        {
            // Restoring is best-effort; the injected text stays on the
            // clipboard, which is a tolerable failure mode.
            ClipboardHelper.TryRestore(snapshot);
        }
        else if (snapshot is { IsEmpty: true })
        {
            // The clipboard was empty before injection; clear it again so
            // the transcript doesn't linger for other processes to read.
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
                // Best effort only; the original failure matters more.
            }
        }

        return false; // Exception filter: never actually catch.
    }

    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint KeyeventfKeyup = 0x0002;

    private static void SendCtrlV()
    {
        NativeInput.Input[] sequence =
        [
            MakeKey(VkControl, down: true),
            MakeKey(VkV, down: true),
            MakeKey(VkV, down: false),
            MakeKey(VkControl, down: false),
        ];

        uint injected = NativeInput.Send(sequence);
        if (injected != sequence.Length)
        {
            int error = Marshal.GetLastWin32Error();

            // SendInput inserts a prefix of the sequence before failing. If
            // Ctrl-down went in but Ctrl-up (the last event) did not, release
            // Ctrl so the user's keyboard isn't left with a stuck modifier.
            // Best effort: its own failure is ignored.
            if (injected >= 1)
            {
                NativeInput.Input[] release = [MakeKey(VkControl, down: false)];
                NativeInput.Send(release);
            }

            throw new InvalidOperationException($"SendInput failed (error {error}).");
        }
    }

    private static NativeInput.Input MakeKey(ushort virtualKey, bool down) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        VirtualKey = virtualKey,
        Flags = down ? 0 : KeyeventfKeyup,
    };
}
