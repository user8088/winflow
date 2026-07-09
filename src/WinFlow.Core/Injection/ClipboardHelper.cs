using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinFlow.Core.Injection;

/// <summary>
/// Raw Win32 clipboard access. Used instead of the WPF Clipboard class so
/// injection can run on any thread without STA requirements. OpenClipboard
/// contention (clipboard managers, the target app itself) is handled with
/// bounded retries. Besides plain-text get/set, supports snapshotting and
/// restoring every duplicable format so paste injection doesn't destroy
/// images, file lists, or rich-text content the user had copied.
/// </summary>
public static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int OpenRetries = 10;

    // Handle-based formats whose HGLOBAL bytes cannot be duplicated safely
    // (HBITMAP/HPALETTE/HENHMETAFILE handles, owner-rendered data), plus
    // formats Windows re-synthesizes from CF_UNICODETEXT on demand.
    private const uint CfOemText = 7;          // synthesized from text
    private const uint CfBitmap = 2;           // HBITMAP handle
    private const uint CfMetafilePict = 3;     // HGLOBAL wrapping an HMETAFILE
    private const uint CfPalette = 9;          // HPALETTE handle
    private const uint CfEnhMetafile = 14;     // HENHMETAFILE handle
    private const uint CfLocale = 16;          // synthesized alongside text
    private const uint CfOwnerDisplay = 0x0080;
    private const uint CfDspBitmap = 0x0082;
    private const uint CfDspMetafilePict = 0x0083;
    private const uint CfDspEnhMetafile = 0x008E;
    private const uint CfPrivateFirst = 0x0200; // owner-managed, arbitrary handles
    private const uint CfPrivateLast = 0x02FF;
    private const uint CfGdiObjFirst = 0x0300;  // GDI object handles, not HGLOBAL
    private const uint CfGdiObjLast = 0x03FF;

    // Registered formats that keep a transcript out of Clipboard History
    // (Win+V) and Cloud Clipboard sync: the monitor format's mere presence
    // suppresses third-party monitors, and the other two are DWORD-0 flags
    // read by the Windows history/cloud services. A value of 0 means
    // registration failed and that format is skipped (best-effort).
    private static readonly uint ExcludeFromMonitorFormat =
        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistoryFormat =
        RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloudFormat =
        RegisterClipboardFormat("CanUploadToCloudClipboard");

    /// <summary>
    /// Copies every duplicable clipboard format into process memory so it can
    /// be restored after paste injection. Returns null when the clipboard
    /// could not be opened; an empty snapshot when it held nothing copyable.
    /// </summary>
    public static ClipboardSnapshot? TrySnapshot()
    {
        if (!OpenWithRetry())
        {
            return null;
        }

        try
        {
            var entries = new List<ClipboardSnapshot.Entry>();
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (!IsDuplicableFormat(format))
                {
                    continue;
                }

                nint handle = GetClipboardData(format);
                if (handle == 0)
                {
                    continue;
                }

                nuint size = GlobalSize(handle);
                if (size == 0 || size > int.MaxValue)
                {
                    continue;
                }

                nint pointer = GlobalLock(handle);
                if (pointer == 0)
                {
                    continue;
                }

                try
                {
                    var data = new byte[(int)size];
                    Marshal.Copy(pointer, data, 0, data.Length);
                    entries.Add(new ClipboardSnapshot.Entry(format, data));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }

            return new ClipboardSnapshot(entries);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Replaces the clipboard contents with a previously taken snapshot.
    /// Best-effort: returns false if the clipboard could not be opened or
    /// emptied; individual formats that fail to set are skipped.
    /// </summary>
    public static bool TryRestore(ClipboardSnapshot snapshot)
    {
        if (!OpenWithRetry())
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            foreach (ClipboardSnapshot.Entry entry in snapshot.Entries)
            {
                nint handle = GlobalAlloc(GmemMoveable, (nuint)entry.Data.Length);
                if (handle == 0)
                {
                    continue;
                }

                nint pointer = GlobalLock(handle);
                if (pointer == 0)
                {
                    GlobalFree(handle);
                    continue;
                }

                try
                {
                    Marshal.Copy(entry.Data, 0, pointer, entry.Data.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(entry.Format, handle) == 0)
                {
                    // Ownership only transfers to the system on success.
                    GlobalFree(handle);
                }
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool IsDuplicableFormat(uint format) => format switch
    {
        CfBitmap or CfMetafilePict or CfPalette or CfEnhMetafile => false,
        CfOwnerDisplay or CfDspBitmap or CfDspMetafilePict or CfDspEnhMetafile => false,
        CfOemText or CfLocale => false,
        >= CfPrivateFirst and <= CfPrivateLast => false,
        >= CfGdiObjFirst and <= CfGdiObjLast => false,
        _ => true,
    };

    public static string? TryGetText()
    {
        if (!OpenWithRetry())
        {
            return null;
        }

        try
        {
            nint handle = GetClipboardData(CfUnicodeText);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Places transcript text on the clipboard, flagged so it never enters
    /// Clipboard History (Win+V) or Cloud Clipboard sync. The write is
    /// transactional: every HGLOBAL is allocated and populated before
    /// EmptyClipboard runs, so a failure up to that point leaves the
    /// clipboard untouched. Returns the clipboard sequence number observed
    /// after the write (while the clipboard is still held open, so it is
    /// race-free) for later staleness checks before restoring.
    /// </summary>
    public static uint SetText(string text)
    {
        if (!OpenWithRetry())
        {
            throw new InvalidOperationException("Could not open the clipboard (another app is holding it).");
        }

        try
        {
            // CF_UNICODETEXT first so a SetClipboardData failure on it aborts
            // before any exclusion flag is written.
            var pending = new List<(uint Format, nint Handle)>
            {
                (CfUnicodeText, AllocTextHandle(text)),
            };

            try
            {
                foreach (uint format in new[]
                         {
                             ExcludeFromMonitorFormat,
                             CanIncludeInHistoryFormat,
                             CanUploadToCloudFormat,
                         })
                {
                    if (format != 0)
                    {
                        pending.Add((format, AllocDwordHandle(0)));
                    }
                }

                if (!EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed");
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    (uint format, nint handle) = pending[i];
                    if (SetClipboardData(format, handle) != 0)
                    {
                        // Ownership only transfers to the system on success.
                        pending[i] = (format, 0);
                    }
                    else if (format == CfUnicodeText)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed");
                    }
                    // Exclusion flags are best-effort; a failed one is freed below.
                }
            }
            finally
            {
                foreach ((_, nint handle) in pending)
                {
                    if (handle != 0)
                    {
                        GlobalFree(handle);
                    }
                }
            }

            return GetClipboardSequenceNumber();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Current clipboard sequence number; Windows bumps it on every clipboard
    /// write, so an unchanged value means nobody has touched the clipboard.
    /// </summary>
    public static uint GetSequenceNumber() => GetClipboardSequenceNumber();

    /// <summary>
    /// Best-effort clear, used to return the clipboard to its pre-injection
    /// empty state instead of leaving a transcript behind.
    /// </summary>
    public static bool TryClear()
    {
        if (!OpenWithRetry())
        {
            return false;
        }

        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static nint AllocTextHandle(string text)
    {
        int bytes = (text.Length + 1) * 2;
        nint handle = AllocLockedCopy((nuint)bytes, pointer =>
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0); // null terminator
        });
        return handle;
    }

    private static nint AllocDwordHandle(uint value) =>
        AllocLockedCopy(sizeof(uint), pointer => Marshal.WriteInt32(pointer, (int)value));

    private static nint AllocLockedCopy(nuint bytes, Action<nint> populate)
    {
        nint handle = GlobalAlloc(GmemMoveable, bytes);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed");
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            int error = Marshal.GetLastWin32Error();
            GlobalFree(handle);
            throw new Win32Exception(error, "GlobalLock failed");
        }

        try
        {
            populate(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static bool OpenWithRetry()
    {
        var spinWait = new SpinWait();
        for (int attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            if (attempt < OpenRetries - 1)
            {
                // SpinWait yields quickly on early failures and only backs
                // off to Thread.Sleep(1) after brief spinning — avoids the
                // fixed 15 ms Thread.Sleep block per failed attempt.
                spinWait.SpinOnce();
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string formatName);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint handle);
}
