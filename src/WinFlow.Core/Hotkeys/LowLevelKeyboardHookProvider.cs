using System.ComponentModel;
using System.Diagnostics;
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
    private const uint WmTimer = 0x0113;

    // The hook only sees events on the interactive desktop. If the key is
    // released while a secure desktop is foreground (UAC, Ctrl+Alt+Del, Win+L,
    // fast user switching) the key-up never arrives and _keyIsDown would stay
    // true forever: recording never stops and every future press is dropped as
    // auto-repeat. A timer on the hook thread periodically resyncs
    // _keyIsDown against reality and synthesizes the missing Released event.
    private const uint ResyncIntervalMs = 250;

    // Because the hook swallows the key (returns 1), the suppressed events
    // never reach the async key-state table, so GetAsyncKeyState alone can
    // report "up" while the key is physically held. While the key is held on
    // the interactive desktop, however, the keyboard's typematic auto-repeat
    // keeps invoking the hook callback. A resync is therefore only trusted
    // once no key-down callback has arrived for longer than the largest
    // standard typematic repeat delay (1s) plus margin.
    private const long ResyncGraceMs = 1500;

    // Windows silently removes a WH_KEYBOARD_LL hook whose callback ever
    // exceeds LowLevelHooksTimeout (GC pause, debugger break, first-JIT) and
    // never calls it again; there is no notification and no "is my hook
    // alive" query. The watchdog timer therefore re-arms the hook
    // unconditionally every few seconds: install a fresh hook first, then
    // remove the old (possibly defunct) one. Cheap and idempotent.
    private const uint RearmIntervalMs = 5000;

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

    // Tick of the most recent key-down callback (initial press or typematic
    // repeat). Only ever touched on the hook thread.
    private long _lastKeyDownTick;

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

        // Thread timers (hWnd = 0) deliver WM_TIMER through this thread's
        // queue, so both maintenance checks run on the hook thread even when
        // no keyboard callbacks arrive — which is exactly the failure mode
        // they exist to repair.
        nuint resyncTimerId = SetTimer(0, 0, ResyncIntervalMs, 0);
        nuint rearmTimerId = SetTimer(0, 0, RearmIntervalMs, 0);

        // WH_KEYBOARD_LL requires a message pump on the installing thread;
        // the loop ends when Stop() posts WM_QUIT.
        while (GetMessage(out Msg msg, 0, 0, 0) > 0)
        {
            if (msg.Hwnd == 0 && msg.Message == WmTimer)
            {
                if (msg.WParam == resyncTimerId)
                {
                    ResyncKeyState();
                }
                else if (msg.WParam == rearmTimerId)
                {
                    RearmHook();
                }
            }
        }

        KillTimer(0, resyncTimerId);
        KillTimer(0, rearmTimerId);
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
        _hookThreadId = 0;
    }

    /// <summary>
    /// Repairs <see cref="_keyIsDown"/> when the key-up was delivered to a
    /// desktop this hook cannot observe (finding: stuck recording + all
    /// future presses misclassified as auto-repeat). Runs on the hook thread.
    /// </summary>
    private void ResyncKeyState()
    {
        if (!_keyIsDown)
        {
            return;
        }

        // While the key is genuinely held on the interactive desktop the
        // typematic auto-repeat keeps refreshing _lastKeyDownTick, so a
        // fresh tick means the key is provably still down.
        if (Environment.TickCount64 - _lastKeyDownTick < ResyncGraceMs)
        {
            return;
        }

        // High bit set = key physically down right now.
        if ((GetAsyncKeyState((int)_virtualKey) & 0x8000) != 0)
        {
            return;
        }

        _keyIsDown = false;
        _events.Writer.TryWrite(HotkeyEvent.Released());
    }

    /// <summary>
    /// Unconditionally re-arms the hook (finding: Windows silently removes a
    /// hook whose callback once exceeded LowLevelHooksTimeout, with no error
    /// and no way to query hook health). Installing the replacement before
    /// removing the old handle avoids a window with no hook at all; the new
    /// hook sits in front of the chain and swallows the watched key, so no
    /// duplicate events are produced. Runs on the hook thread, which also
    /// owns disposal, so it cannot race Stop().
    /// </summary>
    private void RearmHook()
    {
        nint newHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (newHandle == 0)
        {
            // Keep the existing (possibly still healthy) hook; retry on the
            // next watchdog tick.
            return;
        }

        if (_hookHandle != 0)
        {
            UnhookWindowsHookEx(_hookHandle);
        }

        _hookHandle = newHandle;
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        try
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
                    _lastKeyDownTick = Environment.TickCount64;
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
        catch (Exception exception)
        {
            // A managed exception in a native hook callback crashes the process
            // and breaks the global hook chain; pass through and keep running.
            Debug.WriteLine($"Keyboard hook callback failed: {exception}");
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hWnd, nuint nIdEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint hWnd, nuint uIdEvent);

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
