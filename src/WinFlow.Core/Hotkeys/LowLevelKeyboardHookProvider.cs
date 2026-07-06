using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Hotkeys;

/// <summary>
/// Global push-to-talk key watcher built on a WH_KEYBOARD_LL hook.
///
/// The hook callback must return within the system's LowLevelHooksTimeout
/// (~300ms) or Windows silently removes the hook, so the callback only
/// classifies the event, writes it to an unbounded channel, and returns.
/// A consumer task delivers <see cref="HotkeyChanged"/> sequentially.
///
/// The watched key is swallowed (the callback returns 1) so holding it
/// doesn't leak modifier state into the focused application. Synthetic
/// events (LLKHF_INJECTED) are passed through untouched by default so the
/// app's own SendInput-based text injection can never retrigger recording;
/// set <paramref name="allowInjected"/> for automated end-to-end tests.
/// </summary>
public sealed class LowLevelKeyboardHookProvider : IHotkeyProvider
{
    public const uint VkRightControl = 0xA3;

    private const int WhKeyboardLl = 13;
    private const nuint WmKeydown = 0x0100;
    private const nuint WmKeyup = 0x0101;
    private const nuint WmSyskeydown = 0x0104;
    private const nuint WmSyskeyup = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private const uint WmQuit = 0x0012;

    private readonly uint _virtualKey;
    private readonly bool _allowInjected;
    private readonly Channel<HotkeyEvent> _events = Channel.CreateUnbounded<HotkeyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Keeps the delegate alive for the lifetime of the hook; without this
    // reference the GC may collect it and the callback becomes a dangling pointer.
    private readonly HookProc _hookProc;

    private Thread? _hookThread;
    private Task? _dispatchTask;
    private nint _hookHandle;
    private uint _hookThreadId;
    private volatile bool _keyIsDown;
    private volatile bool _running;

    public event Action<HotkeyEvent>? HotkeyChanged;

    public LowLevelKeyboardHookProvider(uint virtualKey = VkRightControl, bool allowInjected = false)
    {
        _virtualKey = virtualKey;
        _allowInjected = allowInjected;
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;

        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hookThread = new Thread(() => HookThreadMain(ready))
        {
            Name = "WinFlow.KeyboardHook",
            IsBackground = true,
        };
        _hookThread.Start();

        if (ready.Task.GetAwaiter().GetResult() is { } startupError)
        {
            _running = false;
            throw startupError;
        }

        _dispatchTask = Task.Run(DispatchLoopAsync);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WmQuit, 0, 0);
        }

        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
        _events.Writer.TryComplete();
        _dispatchTask?.Wait(TimeSpan.FromSeconds(2));
        _dispatchTask = null;
        _keyIsDown = false;
    }

    public void Dispose() => Stop();

    private void HookThreadMain(TaskCompletionSource<Exception?> ready)
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);

        if (_hookHandle == 0)
        {
            ready.TrySetResult(new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed"));
            return;
        }

        ready.TrySetResult(null);

        while (GetMessage(out _, 0, 0, 0) > 0)
        {
            // WH_KEYBOARD_LL requires a message pump on the installing thread;
            // no messages are expected other than the WM_QUIT that ends the loop.
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
        _hookThreadId = 0;
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KbdllHookStruct>(lParam);

        bool isInjected = (info.Flags & LlkhfInjected) != 0;
        if (info.VkCode != _virtualKey || (isInjected && !_allowInjected))
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        switch (wParam)
        {
            case WmKeydown or WmSyskeydown:
                if (!_keyIsDown) // filter auto-repeat
                {
                    _keyIsDown = true;
                    _events.Writer.TryWrite(HotkeyEvent.Pressed());
                }
                return 1; // swallow

            case WmKeyup or WmSyskeyup:
                if (_keyIsDown)
                {
                    _keyIsDown = false;
                    _events.Writer.TryWrite(HotkeyEvent.Released());
                }
                return 1; // swallow

            default:
                return CallNextHookEx(0, code, wParam, lParam);
        }
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (HotkeyEvent hotkeyEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                HotkeyChanged?.Invoke(hotkeyEvent);
            }
            catch
            {
                // A throwing subscriber must not kill hotkey delivery.
            }
        }
    }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }
}
