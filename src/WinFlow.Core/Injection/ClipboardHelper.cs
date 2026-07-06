using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinFlow.Core.Injection;

/// <summary>
/// Raw Win32 clipboard access (CF_UNICODETEXT only). Used instead of the
/// WPF Clipboard class so injection can run on any thread without STA
/// requirements. OpenClipboard contention (clipboard managers, the target
/// app itself) is handled with bounded retries.
/// </summary>
public static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int OpenRetries = 10;
    private const int OpenRetryDelayMs = 15;

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

    public static void SetText(string text)
    {
        if (!OpenWithRetry())
        {
            throw new InvalidOperationException("Could not open the clipboard (another app is holding it).");
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed");
            }

            int bytes = (text.Length + 1) * 2;
            nint handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed");
            }

            nint pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                GlobalFree(handle);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed");
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * 2, 0); // null terminator
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == 0)
            {
                // Ownership only transfers to the system on success.
                GlobalFree(handle);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool OpenWithRetry()
    {
        for (int attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
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
    private static extern nint SetClipboardData(uint format, nint data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint handle);
}
