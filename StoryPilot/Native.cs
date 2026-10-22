using System.Runtime.InteropServices;

namespace StoryPilot;

internal static class Native
{
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_HOTKEY = 0x0312;

    public const int VK_SPACE = 0x20;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_F = 0x46;
    public const int VK_S = 0x53;
    public const int VK_W = 0x57;
    public const int VK_A = 0x41;
    public const int VK_D = 0x44;
    public const int VK_V = 0x56;
    public const int VK_SHIFT = 0xA0; // left Shift

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    public const uint WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Tick count of the last real or injected keyboard/mouse input anywhere on the desktop.</summary>
    public static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    /// <summary>True while any of the keys is physically held down.</summary>
    public static bool AnyKeyDown(params int[] vks) => vks.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi; // the union starts at 8 on x64; MOUSEINPUT is its largest member
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>Real key press or release (by scan code, which the game reads), foreground only.</summary>
    public static void SendKey(int vk, bool up)
    {
        var ki = new KEYBDINPUT
        {
            wVk = (ushort)vk,
            wScan = (ushort)MapVirtualKey((uint)vk, 0),
            dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0),
        };
        SendInput(1, [new INPUT { type = 1, ki = ki }], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Relative mouse movement (turns the game camera), foreground only.</summary>
    public static void MoveMouse(int dx, int dy)
    {
        var mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
        SendInput(1, [new INPUT { type = 0, mi = mi }], Marshal.SizeOf<INPUT>());
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>Real left click at the current cursor position (foreground only).</summary>
    public static void SendLeftClick()
    {
        var down = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out RECT value, int size);
}
