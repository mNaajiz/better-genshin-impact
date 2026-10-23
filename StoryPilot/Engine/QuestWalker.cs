using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// Camera steering toward the quest marker at the normal camera angle: turn until the marker is
/// near the middle of the screen, walk while it is roughly ahead.
/// </summary>
public static class WalkSteering
{
    /// <summary>Within this many pixels of the middle, no turning is needed.</summary>
    public const int DeadZone = 25;

    /// <summary>Walk forward while the marker is within this many pixels of the middle.</summary>
    public const int WalkZone = 220;

    /// <summary>
    /// Horizontal mouse movement to turn by, and whether to walk forward now. <paramref name="previousDx"/> is
    /// the last turn: when the direction flips (overshoot) the turn is halved, so it settles instead of swinging.
    /// </summary>
    public static (int MouseDx, bool Walk) Steer(Point marker, int previousDx)
    {
        var offset = marker.X - Vision.Width / 2;
        if (marker.Y > 820)
        {
            // Pinned to the bottom of the screen: it's behind. Turn toward its side, don't walk away from it.
            return (offset >= 0 ? 300 : -300, false);
        }

        var dx = Math.Abs(offset) <= DeadZone ? 0 : Math.Clamp(offset / 5, -250, 250);
        if (dx != 0 && Math.Sign(dx) == -Math.Sign(previousDx))
        {
            dx /= 2;
        }

        return (dx, Math.Abs(offset) < WalkZone);
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
/// "Walk to quest marker": follows the glowing quest trail (pressing V to show it) or, where there is no
/// trail, turns the camera toward the tracked quest's marker, and holds W.
/// Real input, so only while the game is the window in front. Stops on arrival, dialogue or an F prompt,
/// when StoryPilot is stopped, or as soon as you touch the keyboard or mouse. W / Shift are always released.
/// </summary>
public sealed class QuestWalker(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log) : IDisposable
{
    public const int ArriveMetres = 5;
    private const int MaxRecoveries = 3;

    /// <summary>Search turns (300 px each) without seeing the marker before giving up: about one full turn.</summary>
    private const int MaxSearchTurns = 10;

    /// <summary>No sprinting this close to the quest.</summary>
    public const int SprintStopMetres = 15;

    /// <summary>Sprint only while turning less than this per step (mouse px).</summary>
    public const int SprintMaxTurn = 30;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Press V again when the trail has been gone this long (it fades after a while)…</summary>
    private static readonly TimeSpan TrailGoneBeforeV = TimeSpan.FromSeconds(1.5);

    /// <summary>…but not more often than this.</summary>
    private static readonly TimeSpan MinVInterval = TimeSpan.FromSeconds(6);

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

    /// <summary>
    /// <paramref name="sprintAlways"/>: hold Shift whenever walking straight; otherwise <paramref name="sprintBursts"/>:
    /// sprint in random bursts. Never on tight turns, sharp trail bends or near the quest.
    /// </summary>
    public Result Run(nint hWnd, GameInput input, bool sprintAlways, bool sprintBursts)
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
            return Walk(sprintAlways, sprintBursts);
        }
        finally
        {
            ReleaseAll();
        }
    }

    private Result Walk(bool sprintAlways, bool sprintBursts)
    {
        // Started from Ctrl+Alt+W: let go of the hotkey first, or releasing it would count as "you took over".
        var released = DateTime.Now.AddSeconds(3);
        while (Native.AnyKeyDown(0x11, 0x12, Native.VK_W) && DateTime.Now < released)
        {
            Thread.Sleep(30);
        }

        Thread.Sleep(150);
        Rebaseline();

        var started = DateTime.Now;
        int? lastMetres = null;
        var stuck = new StuckDetector(TimeSpan.FromSeconds(2));
        var recoveries = 0;
        var searchTurns = 0;
        var previousDx = 0;
        var strafeLeft = false;
        var bursts = new SprintBursts(Random.Shared);
        var lastV = DateTime.MinValue;
        var trailSeen = DateTime.MinValue;
        var outOfView = 0;
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

            // A menu opened (V with nothing to navigate to opens the quest list, for one): stop, don't walk blind.
            outOfView = Vision.Find(grey, _paimonMenu) == null ? outOfView + 1 : 0;
            if (outOfView >= 3)
            {
                return Finish(Result.GaveUp, "left the normal game view");
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

            // Show the trail (again) when it isn't there: at the start, and after it fades.
            var trail = QuestTrail.Find(frame);
            if (trail != null)
            {
                trailSeen = DateTime.Now;
            }
            else if (DateTime.Now - trailSeen >= TrailGoneBeforeV && DateTime.Now - lastV >= MinVInterval)
            {
                lastV = DateTime.Now;
                Tap(Native.VK_V);
            }

            var aim = trail?.Aim ?? QuestMarker.Find(frame)?.Centre;
            if (aim == null)
            {
                Hold(Native.VK_W, false);
                Hold(Native.VK_SHIFT, false);
                if (++searchTurns > MaxSearchTurns)
                {
                    return metres <= 10
                        ? Finish(Result.Arrived, $"arrived ({metres} m)")
                        : Finish(Result.GaveUp, "can't see the quest trail or marker (turned all the way round)");
                }

                Native.MoveMouse(300, 0); // look around for it
                previousDx = 0;
                stuck.Restart(DateTime.Now);
                Rebaseline();
                if (!Wait(250))
                {
                    return Cancelled();
                }

                continue;
            }

            searchTurns = 0;
            var (dx, walk) = WalkSteering.Steer(aim.Value, previousDx);
            if (dx != 0)
            {
                Native.MoveMouse(dx, 0);
            }

            previousDx = dx;
            var mayRun = walk && SprintAllowed(dx, trail?.SharpCurve ?? false, metres);
            var burst = bursts.Update(DateTime.Now, mayRun);
            Hold(Native.VK_W, walk);
            Hold(Native.VK_SHIFT, mayRun && (sprintAlways || (sprintBursts && burst)));

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

            Rebaseline();
            if (!Wait(150))
            {
                return Cancelled();
            }
        }

        return Finish(Result.GaveUp, "took too long");
    }

    /// <summary>Sprinting is fine when going (nearly) straight, the trail doesn't bend sharply and the quest isn't close.</summary>
    public static bool SprintAllowed(int turnDx, bool sharpCurve, int? metres) =>
        Math.Abs(turnDx) < SprintMaxTurn && !sharpCurve && metres is null or > SprintStopMetres;

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
        _talkUi.Dispose();
        _pickF.Dispose();
        _paimonMenu.Dispose();
    }
}
