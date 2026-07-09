using System.Runtime.InteropServices;

namespace WinFlow.Core.Injection;

/// <summary>
/// Win32 <c>INPUT</c> keyboard events and <c>SendInput</c> P/Invoke shared by
/// text injectors.
/// </summary>
internal static class NativeInput
{
    // INPUT is a 40-byte tagged union on x64; explicit layout keeps the
    // keyboard fields at the union offset without defining MOUSEINPUT.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    internal static uint Send(Input[] inputs)
    {
        if (inputs.Length == 0)
        {
            return 0;
        }

        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground != 0
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, fAttach: true);

        try
        {
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, fAttach: false);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
