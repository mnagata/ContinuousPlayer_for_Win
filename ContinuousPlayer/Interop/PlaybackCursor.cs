using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.UI.Input;

namespace ContinuousPlayer.Interop;

internal static partial class PlaybackCursor
{
    public static void RefreshPointer(nint window)
    {
        // WinUI may retain the system arrow after keyboard input until a real
        // pointer update arrives. Queue a one-pixel update and return atomically
        // to the original position; physical-position polling ignores this pair.
        if (GetForegroundWindow() != window || !GetPhysicalCursorPos(out var point) || !IsOver(window, point)) return;
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref info) && (info.Flags & 1) == 0) return;
        foreach (var button in new[] { 1, 2, 4, 5, 6 })
            if ((GetAsyncKeyState(button) & 0x8000) != 0) return;
        var next = point;
        next.X++;
        if (!IsOver(window, next)) { next.X = point.X - 1; if (!IsOver(window, next)) return; }
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1) return;
        int Normalize(int value, int start, int extent) => Math.Clamp((int)Math.Round((value - start) * 65535.0 / (extent - 1)), 0, 65535);
        NativeInput Move(CursorPoint position) => new()
        {
            Mouse = new MouseInput { X = Normalize(position.X, left, width), Y = Normalize(position.Y, top, height), Flags = 0xE001 }
        }; // MOVE | MOVE_NOCOALESCE | VIRTUALDESK | ABSOLUTE
        SendInput(2, [Move(next), Move(point)], Marshal.SizeOf<NativeInput>());
        // Correct absolute-coordinate rounding without overriding new user motion.
        if (GetPhysicalCursorPos(out var after) && (after.X != point.X || after.Y != point.Y) &&
            Math.Abs(after.X - point.X) <= 1 && Math.Abs(after.Y - point.Y) <= 1)
            SetPhysicalCursorPos(point.X, point.Y);
    }

    private static bool IsOver(nint window, CursorPoint point)
    {
        var target = WindowFromPoint(point);
        return target == window || IsChild(window, target);
    }

    public static InputCursor CreateTransparent()
    {
        // Monochrome AND=1 / XOR=0 preserves every underlying pixel.
        var andMask = new byte[128];
        Array.Fill(andMask, (byte)255);
        var handle = CreateCursor(0, 0, 0, 32, 32, andMask, new byte[128]);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var factory = InputCursor.As<IInputCursorStaticsInterop>();
            Marshal.ThrowExceptionForHR(factory.CreateFromHCursor(handle, out var result));
            try { return WinRT.MarshalInterface<InputCursor>.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        // The WinUI interop API copies custom cursor data.
        finally { DestroyCursor(handle); }
    }

    [GeneratedComInterface]
    [Guid("ac6f5065-90c4-46ce-beb7-05e138e54117")]
    internal partial interface IInputCursorStaticsInterop
    {
        // IInspectable slots after IUnknown; only CreateFromHCursor is called.
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int CreateFromHCursor(nint cursor, out nint result);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int xHotSpot, int yHotSpot, int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyCursor(nint cursor);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public CursorPoint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
