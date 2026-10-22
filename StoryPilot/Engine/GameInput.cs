namespace StoryPilot.Engine;

/// <summary>
/// Sends input to the game window with PostMessage. This never moves the real mouse
/// cursor and never brings the game to the front, so you can keep working in other windows.
/// Same message sequence as BetterGI's PostMessageSimulator (WM_ACTIVATE first when the game is in the background).
/// </summary>
public sealed class GameInput(nint hWnd)
{
    private void ActivateIfBackground()
    {
        if (Native.GetForegroundWindow() != hWnd)
        {
            Native.PostMessage(hWnd, Native.WM_ACTIVATE, 1, 0);
        }
    }

    private static nint KeyLParam(int vk, bool up)
    {
        var scan = Native.MapVirtualKey((uint)vk, 0) & 0xFFu;
        var l = 1u | (scan << 16);
        if (up)
        {
            l |= (1u << 30) | (1u << 31);
        }

        return unchecked((nint)l);
    }

    public void Key(int vk)
    {
        ActivateIfBackground();
        Native.PostMessage(hWnd, Native.WM_KEYDOWN, vk, KeyLParam(vk, false));
        Native.PostMessage(hWnd, Native.WM_CHAR, vk, KeyLParam(vk, false));
        Native.PostMessage(hWnd, Native.WM_KEYUP, vk, KeyLParam(vk, true));
    }

    /// <summary>
    /// Left click at a point in the game's client area (physical pixels).
    /// </summary>
    public void Click(int clientX, int clientY)
    {
        ActivateIfBackground();
        var p = (nint)((clientY << 16) | (clientX & 0xFFFF));
        Native.PostMessage(hWnd, Native.WM_MOUSEMOVE, 0, p);
        Native.PostMessage(hWnd, Native.WM_LBUTTONDOWN, 1, p);
        Thread.Sleep(60);
        Native.PostMessage(hWnd, Native.WM_LBUTTONUP, 0, p);
    }

    public bool IsGameInFront => Native.GetForegroundWindow() == hWnd;

    /// <summary>
    /// Real mouse click (moves the cursor, then puts it back). Only used when the game is
    /// already the window in front, for screens that ignore posted clicks.
    /// </summary>
    public bool ForegroundClick(int clientX, int clientY)
    {
        if (!IsGameInFront)
        {
            return false;
        }

        var pt = new Native.POINT { X = clientX, Y = clientY };
        Native.ClientToScreen(hWnd, ref pt);
        Native.GetCursorPos(out var old);
        Native.SetCursorPos(pt.X, pt.Y);
        Thread.Sleep(30);
        Native.SendLeftClick();
        Thread.Sleep(30);
        Native.SetCursorPos(old.X, old.Y);
        return true;
    }
}
