using System.ComponentModel;
using System.Runtime.InteropServices;
using ContinuousPlayer.Core;

namespace ContinuousPlayer.Interop;

/// <summary>Only tracks media-key press lifetimes; never consumes or records other keyboard input.</summary>
internal sealed class PlaybackInputMonitor : IDisposable
{
    private readonly MediaKeyPressLatch mediaKeys = new();
    private readonly KeyboardProc keyboardCallback;
    private readonly WindowProc windowCallback;
    private readonly nint hwnd;
    private nint hook;
    private bool subclassed;
    private const nuint SubclassId = 0x4350494E;

    public PlaybackInputMonitor(nint hwnd)
    {
        this.hwnd = hwnd;
        keyboardCallback = KeyboardMessage;
        windowCallback = WindowMessage;
        try
        {
            if (!SetWindowSubclass(hwnd, windowCallback, SubclassId, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            subclassed = true;
            hook = SetWindowsHookExW(13, keyboardCallback, GetModuleHandleW(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    public bool TryAcceptMediaKey(int virtualKey) => mediaKeys.TryAccept(virtualKey, virtualKey == 0xB3 ? 0xFA : 0);
    public static int DoubleClickMilliseconds => (int)GetDoubleClickTime();
    public static int DoubleClickWidth => GetSystemMetrics(36);

    private nint KeyboardMessage(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            // vkCode is the first DWORD of KBDLLHOOKSTRUCT. No other key data is retained.
            var key = Marshal.ReadInt32(data);
            if (key is >= 0xB0 and <= 0xB3 or 0xFA)
            {
                if (message is 0x100 or 0x104) mediaKeys.KeyDown(key);
                else if (message is 0x101 or 0x105) mediaKeys.KeyUp(key);
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private nint WindowMessage(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // WM_MOUSEACTIVATE / HTCLIENT: the first real mouse click activates only.
        // Promoted touch/pen messages carry MI_WP_SIGNATURE and keep their touch semantics.
        if (message == 0x21 && (lParam.ToInt64() & 0xFFFF) == 1 && GetForegroundWindow() != hwnd &&
            (GetMessageExtraInfo().ToInt64() & 0xFFFFFF00L) != 0xFF515700L)
            return 2; // MA_ACTIVATEANDEAT
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
        if (subclassed) { RemoveWindowSubclass(hwnd, windowCallback, SubclassId); subclassed = false; }
    }

    private delegate nint KeyboardProc(int code, nuint message, nint data);
    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int type, KeyboardProc callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, WindowProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, WindowProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
