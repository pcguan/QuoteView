using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StockClient.App.ViewModels;
using StockClient.Core.Groups;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

internal static class Native
{
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>System-wide time since the last keyboard/mouse input.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>(),
        };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Unsigned math survives the 49-day TickCount wrap.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    public const int WhMouseLl = 14;
    public const int WhKeyboardLl = 13;
    public const int WmMouseWheelMsg = 0x020A;
    public const int WmLButtonDownMsg = 0x0201;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();
    public const int WmKeyDown = 0x0100;
    public const int WmSysKeyDown = 0x0104;

    public const long WsExTransparent = 0x20;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkMenu = 0x12;

    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>Payload of a low-level keyboard hook callback (KBDLLHOOKSTRUCT).</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct KeyboardLowLevel
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>Payload of a low-level mouse hook callback (MSLLHOOKSTRUCT).</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MouseLowLevel
    {
        public int X;
        public int Y;

        /// <summary>For the wheel, the notch delta is the HIGH word.</summary>
        public int MouseData;

        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    public const int GwlExStyle = -20;
    public const int WsExTopmost = 0x8;

    /// <summary>Previous in Z-order means nearer the front.</summary>
    public const uint GwHwndPrev = 3;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNoTopmost = new(-2);

    public const uint SwpNoSize = 0x1;
    public const uint SwpNoMove = 0x2;
    public const uint SwpNoActivate = 0x10;


    /// <summary>
    /// What Windows thinks, not what WPF thinks.
    ///
    /// A panel that is Opacity=0, or that quietly lost WS_EX_TOPMOST and slid
    /// behind Chrome, is invisible to the user while every WPF property still
    /// reads perfectly healthy. Both have to be sampled from the OS side.
    /// </summary>
    public static string Describe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "hwnd=0";

        var ex = GetWindowLong(hWnd, GwlExStyle);
        return $"hwnd=0x{hWnd:X} win32visible={IsWindowVisible(hWnd)} " +
               $"topmost={((ex & WsExTopmost) != 0)}";
    }
}
