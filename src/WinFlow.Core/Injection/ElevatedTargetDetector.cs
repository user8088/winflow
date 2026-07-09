using System.Runtime.InteropServices;

namespace WinFlow.Core.Injection;

/// <summary>
/// Detects the UIPI trap: when the foreground window belongs to an elevated
/// process and WinFlow is not elevated, Windows silently drops synthesized
/// input — SendInput still reports success, so without this check the
/// transcript would be lost without any error. Shared by
/// <see cref="ClipboardTextInjector"/> and <see cref="KeystrokeTextInjector"/>.
/// </summary>
public static class ElevatedTargetDetector
{
    /// <summary>
    /// User-facing explanation raised when injection would be UIPI-blocked.
    /// </summary>
    public const string BlockedMessage =
        "The focused window is elevated (run as administrator); Windows blocks "
        + "synthesized input. The transcript was left on the clipboard — press "
        + "Ctrl+V to paste it.";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20; // TOKEN_INFORMATION_CLASS.TokenElevation
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// True when the foreground window belongs to an elevated process while
    /// this process is not elevated, meaning UIPI will silently discard any
    /// SendInput we synthesize. Fails open (returns false) on any lookup
    /// error other than access-denied, which itself signals a
    /// higher-integrity target.
    /// </summary>
    public static bool IsInjectionBlockedByUipi()
    {
        try
        {
            if (IsCurrentProcessElevated())
            {
                // Elevated WinFlow can inject into anything.
                return false;
            }

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

            nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == 0)
            {
                // A non-elevated process cannot open a higher-integrity one.
                return Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }

            try
            {
                return IsProcessElevated(process);
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch
        {
            // Never let the detector itself break injection.
            return false;
        }
    }

    private static bool IsCurrentProcessElevated() => IsProcessElevated(GetCurrentProcess());

    private static bool IsProcessElevated(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            uint elevation = 0;
            if (!GetTokenInformation(
                    token, TokenElevationClass, ref elevation, sizeof(uint), out _))
            {
                return false;
            }

            return elevation != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token, int informationClass, ref uint information, uint length, out uint returnLength);
}
