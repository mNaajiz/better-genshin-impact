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

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out RECT value, int size);
}
