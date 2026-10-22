using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// Camera steering toward the quest marker, with the camera looking down at the character (as BetterGI's
/// MakeBlueTrackPointDirectlyAbove): straight ahead is "above the screen centre".
/// </summary>
public static class WalkSteering
{
    /// <summary>Horizontal mouse movement to turn by, and whether to walk forward now.</summary>
    public static (int MouseDx, bool Walk) Steer(Point marker)
    {
        var offset = marker.X - Vision.Width / 2;
        if (marker.Y > Vision.Height / 2 + 40)
        {
            // Behind the character: turn quickly toward its side, don't walk away from it.
            return (offset >= 0 ? 150 : -150, false);
        }

        var dx = Math.Clamp(offset / 8, -200, 200);
        if (dx is > 0 and < 10)
        {
            dx = 10;
        }
        else if (dx is < 0 and > -10)
        {
            dx = -10;
        }

        return (dx, Math.Abs(offset) < 160);
    }
}

/// <summary>
/// "Stuck": the quest distance hasn't gone down for <paramref name="window"/> while walking.
/// </summary>
public sealed class StuckDetector(TimeSpan window)
{
    private int? _best;
    private DateTime _since;

    public bool Update(int? metres, DateTime now)
    {
        // Unreadable distance (or the first reading): can't tell, so don't call it stuck.
        if (metres == null || _best == null || metres < _best)
        {
            _best = metres ?? _best;
            _since = now;
            return false;
        }

        return now - _since >= window;
    }

    /// <summary>After a recovery move, give it a fresh window.</summary>
    public void Restart(DateTime now) => _since = now;
}

/// <summary>
/// "Walk to quest marker": turns the camera toward the tracked quest's on-screen marker and holds W.
/// Real input, so only while the game is the window in front. Stops on arrival, dialogue or an F prompt,
/// when StoryPilot is stopped, or as soon as you touch the keyboard or mouse. W / Shift are always released.
/// </summary>
public sealed class QuestWalker(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log) : IDisposable
{
    public const int ArriveMetres = 5;
    private const int MaxRecoveries = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(4);

    private readonly QuestMarker _marker = new();
    private readonly Template _talkUi = new("disabled_ui.png", new Rect(0, 0, 640, 135));
    private readonly Template _pickF = new("chat_pick_f.png", new Rect(1090, 330, 60, 420));
    private readonly Template _paimonMenu = new("paimon_menu.png", new Rect(0, 0, 480, 270));

    private readonly HashSet<int> _held = [];
    private nint _hWnd;
    private uint _inputBaseline;

    public enum Result
    {
        Arrived,
        HandedOver,
        GaveUp,
        Cancelled,
    }

    public Result Run(nint hWnd, GameInput input, bool sprint)
    {
        _hWnd = hWnd;
        if (!input.IsGameInFront)
        {
            log("Walk: the game must be the window in front");
            return Result.GaveUp;
        }

        using (var first = Grab1080())
        using (var grey = first?.CvtColor(ColorConversionCodes.BGR2GRAY))
        {
            if (grey == null || Vision.Find(grey, _paimonMenu) == null)
            {
                log("Walk: not in the normal game view");
                return Result.GaveUp;
            }
        }

        try
        {
            return Walk(sprint);
        }
        finally
        {
            ReleaseAll();
            if (Native.GetForegroundWindow() == _hWnd)
            {
                Native.MoveMouse(0, -300); // tilt the camera back up from the top-down view
            }
        }
    }

    private Result Walk(bool sprint)
    {
        // Started from Ctrl+Alt+W: let go of the hotkey first, or releasing it would count as "you took over".
        var released = DateTime.Now.AddSeconds(3);
        while (Native.AnyKeyDown(0x11, 0x12, Native.VK_W) && DateTime.Now < released)
        {
            Thread.Sleep(30);
        }

        Thread.Sleep(150);
        // V shows the tracked quest's navigation marker; then look down at the character.
        Native.SendKey(Native.VK_V, false);
        Native.SendKey(Native.VK_V, true);
        Native.MoveMouse(0, 600);
        Rebaseline();
        if (!Wait(1200))
        {
            return Cancelled();
        }

        var started = DateTime.Now;
        var lastSeen = DateTime.Now;
        int? lastMetres = null;
        var stuck = new StuckDetector(TimeSpan.FromSeconds(2));
        var recoveries = 0;
        var strafeLeft = false;
        log("Walking to the quest marker…");

        while (DateTime.Now - started < Timeout)
        {
            if (!keepGoing() || UserTookOver())
            {
                return Cancelled();
            }

            using var frame = Grab1080();
            if (frame == null)
            {
                return Finish(Result.GaveUp, "lost the game picture");
            }

            using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            if (Vision.Find(grey, _talkUi) != null)
            {
                return Finish(Result.HandedOver, "dialogue started");
            }

            var read = DistanceReader.Read(frame);
            var metres = read ?? lastMetres;
            if (Vision.Find(grey, _pickF) != null)
            {
                ReleaseAll();
                if (metres is null or <= 10)
                {
                    Tap(Native.VK_F);
                    return Finish(Result.HandedOver, "pressed F at the quest");
                }

                return Finish(Result.HandedOver, "something to interact with is in the way");
            }

            lastMetres = metres;
            if (metres <= ArriveMetres)
            {
                return Finish(Result.Arrived, $"arrived ({metres} m)");
            }

            var marker = _marker.Find(frame);
            if (marker == null)
            {
                Hold(Native.VK_W, false);
                Hold(Native.VK_SHIFT, false);
                if (DateTime.Now - lastSeen > LostAfter)
                {
                    return metres <= 10
                        ? Finish(Result.Arrived, $"arrived ({metres} m)")
                        : Finish(Result.GaveUp, "can't see the quest marker");
                }

                Native.MoveMouse(250, 0); // look around for it
                stuck.Restart(DateTime.Now);
            }
            else
            {
                lastSeen = DateTime.Now;
                var (dx, walk) = WalkSteering.Steer(marker.Value.Centre);
                if (dx != 0)
                {
                    Native.MoveMouse(dx, 30); // the small downward nudge keeps the top-down view
                }

                Hold(Native.VK_W, walk);
                Hold(Native.VK_SHIFT, walk && sprint);
                if (!walk)
                {
                    stuck.Restart(DateTime.Now);
                }
                else if (stuck.Update(read, DateTime.Now))
                {
                    if (++recoveries > MaxRecoveries)
                    {
                        return Finish(Result.GaveUp, "stuck");
                    }

                    // Jump, then side-step left or right (alternating) while still walking.
                    Tap(Native.VK_SPACE);
                    var side = strafeLeft ? Native.VK_A : Native.VK_D;
                    strafeLeft = !strafeLeft;
                    Hold(side, true);
                    Thread.Sleep(450);
                    Hold(side, false);
                    stuck.Restart(DateTime.Now);
                    log($"Walk: stuck, trying to get around it ({recoveries}/{MaxRecoveries})");
                }
            }

            Rebaseline();
            if (!Wait(150))
            {
                return Cancelled();
            }
        }

        return Finish(Result.GaveUp, "took too long");
    }

    private Result Finish(Result result, string why)
    {
        ReleaseAll();
        log($"Walk: {why}");
        return result;
    }

    private Result Cancelled()
    {
        ReleaseAll();
        log("Walk cancelled");
        return Result.Cancelled;
    }

    private void Hold(int vk, bool down)
    {
        if (down == _held.Contains(vk))
        {
            return;
        }

        Native.SendKey(vk, !down);
        if (down)
        {
            _held.Add(vk);
        }
        else
        {
            _held.Remove(vk);
        }
    }

    private void Tap(int vk)
    {
        Native.SendKey(vk, false);
        Thread.Sleep(40);
        Native.SendKey(vk, true);
        Rebaseline();
    }

    private void ReleaseAll()
    {
        foreach (var vk in _held.ToList())
        {
            Native.SendKey(vk, true);
        }

        _held.Clear();
        Rebaseline();
    }

    /// <summary>Our own SendInput also counts as "last input"; only input after this point is the player's.</summary>
    private void Rebaseline() => _inputBaseline = Native.LastInputTick();

    /// <summary>The player used the keyboard/mouse, or switched away from the game.</summary>
    private bool UserTookOver() => Native.GetForegroundWindow() != _hWnd || Native.LastInputTick() != _inputBaseline;

    private bool Wait(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (!keepGoing() || UserTookOver())
            {
                return false;
            }

            Thread.Sleep(30);
        }

        return true;
    }

    private Mat? Grab1080()
    {
        var raw = grab();
        if (raw == null || raw.Empty())
        {
            raw?.Dispose();
            return null;
        }

        if (raw.Width == Vision.Width && raw.Height == Vision.Height)
        {
            return raw;
        }

        var resized = raw.Resize(new OpenCvSharp.Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        raw.Dispose();
        return resized;
    }

    public void Dispose()
    {
        _marker.Dispose();
        _talkUi.Dispose();
        _pickF.Dispose();
        _paimonMenu.Dispose();
    }
}
