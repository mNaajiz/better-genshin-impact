// Movement rules follow BetterGI's PathExecutor.MoveTo / MoveCloseTo and CameraRotateTask (GPL-3.0),
// simplified: no character skills, no gadgets, no anomaly handling beyond "stuck → jump and side-step".
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine.Nav;

/// <summary>One point of a community route file (BetterGI pathing JSON).</summary>
public sealed record RoutePoint(string Type, string MoveMode, string Action, string ActionParams, float X, float Y)
{
    public Point2f Pos => new(X, Y);
    public bool IsTeleport => Type == "teleport";
    public bool IsTarget => Type == "target";
    public bool IsOrientation => Type == "orientation";
}

/// <summary>Pure steering maths, kept apart for tests.</summary>
public static class RouteSteering
{
    /// <summary>
    /// Heading from <paramref name="from"/> to <paramref name="to"/> in the minimap camera's convention
    /// (BetterGI Navigation.GetTargetOrientation), 0..359.
    /// </summary>
    public static int OrientationTo(Point2f from, Point2f to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0)
        {
            return 0;
        }

        var a = Math.Acos(dx / len);
        if (dy < 0)
        {
            a = 2 * Math.PI - a;
        }

        return (int)(a * 180 / Math.PI);
    }

    /// <summary>Camera minus target, wrapped to -180..180.</summary>
    public static float AngleDiff(float camera, float target)
    {
        var diff = (camera - target + 180) % 360 - 180;
        return diff < -180 ? diff + 360 : diff;
    }

    /// <summary>Mouse movement for a heading error, as BetterGI's RotateToApproach (bigger steps for bigger errors).</summary>
    public static int TurnDx(float diff, double mouseScale = 1)
    {
        var a = Math.Abs(diff);
        double ratio = a > 90 ? 4 : a > 30 ? 3 : a > 5 ? 2 : 1;
        return (int)Math.Round(-ratio * diff * mouseScale);
    }

    /// <summary>Close enough to move on: 4 units for path points, 2 for targets (BetterGI's thresholds).</summary>
    public static double ArriveDistance(RoutePoint p) => p.IsTarget ? 2 : 4;
}

/// <summary>
/// Walks a community route with the game in front: minimap position each frame, turn the camera toward the
/// next point, hold W; sprint (right click) for run/dash points, jump/glide for fly points.
/// </summary>
public sealed class RouteWalker(MiniMapLocator locator, Func<Mat?> grab, Func<bool> keepGoing, Action<string> log) : IDisposable
{
    private readonly MiniMapPreprocessor _camera = new();
    private readonly Template _spaceKey = new("key_space.png", new Rect(1920 - 350, 1080 - 70, 200, 70));
    private readonly Template _xKey = new("key_x.png", new Rect(1920 - 350, 1080 - 70, 200, 70));

    /// <summary>Mouse movement per degree of camera turn (BetterGI uses 1 × DPI scale). Settings can tune it.</summary>
    public double MouseScale { get; set; } = 1;

    public enum Result
    {
        Arrived,
        Lost,
        Stuck,
        TimedOut,
        Cancelled,
    }

    private Point2f? _last;

    public Point2f? LastPosition => _last;

    /// <summary>Walks every point after the teleport. <paramref name="start"/> is the expected first position (the teleport point).</summary>
    public Result Walk(IReadOnlyList<RoutePoint> route, Point2f? start)
    {
        if (start is { } s)
        {
            locator.SetPrevious(s);
        }

        try
        {
            foreach (var p in route)
            {
                if (p.IsTeleport)
                {
                    continue;
                }

                var r = p.IsOrientation ? Face(p) : MoveTo(p);
                if (r != Result.Arrived)
                {
                    return r;
                }

                if (p.IsTarget)
                {
                    var c = MoveCloseTo(p);
                    if (c == Result.Cancelled)
                    {
                        return c;
                    }
                }

                if (p.Action == "stop_flying")
                {
                    StopFlying(p);
                }
            }

            return Result.Arrived;
        }
        finally
        {
            ReleaseAll();
        }
    }

    private (Point2f? Pos, float Camera) Read()
    {
        using var frame = grab();
        if (frame == null)
        {
            return (null, 0);
        }

        using var mini = new Mat(frame, MiniMapLocator.MiniMapRect);
        var pos = locator.LocateMiniMap(mini);
        var cam = _camera.PredictRotation(mini);
        if (pos != null)
        {
            _last = pos;
        }

        return (pos, cam);
    }

    private bool Flying()
    {
        using var frame = grab();
        if (frame == null)
        {
            return false;
        }

        using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        return Vision.Find(grey, _spaceKey) != null && Vision.Find(grey, _xKey) == null;
    }

    private Result MoveTo(RoutePoint p)
    {
        var started = DateTime.Now;
        var lost = 0;
        var history = new List<Point2f>();
        var lastRecord = DateTime.MinValue;
        var traps = 0;
        var sprintAt = DateTime.MinValue;
        var sprintHeld = false;
        var w = false;

        while (true)
        {
            if (!keepGoing())
            {
                return Result.Cancelled;
            }

            if ((DateTime.Now - started).TotalSeconds > 120)
            {
                log("Route step took over 2 minutes, giving up");
                return Result.TimedOut;
            }

            var (pos, cam) = Read();
            if (pos is not { } here)
            {
                if (++lost > 40)
                {
                    log("Lost track of where we are on the minimap");
                    return Result.Lost;
                }

                Thread.Sleep(50);
                continue;
            }

            lost = 0;
            var distance = here.DistanceTo(p.Pos);
            if (distance < RouteSteering.ArriveDistance(p) || (!p.IsTarget && distance < 4))
            {
                return Result.Arrived;
            }

            if (distance > 500)
            {
                log($"Position ({here.X:F0}, {here.Y:F0}) is {distance:F0} away from the route, giving up");
                return Result.Lost;
            }

            // Stuck: hardly moved over the last ~8 s of walking (not while climbing).
            if (p.MoveMode != "climb" && (DateTime.Now - lastRecord).TotalMilliseconds > 1000)
            {
                lastRecord = DateTime.Now;
                history.Add(here);
                if (history.Count > 8)
                {
                    var d = history[^1] - history[^8];
                    if (Math.Abs(d.X) + Math.Abs(d.Y) < 3)
                    {
                        if (++traps > 2)
                        {
                            log("Stuck three times on this route step");
                            return Result.Stuck;
                        }

                        log("Looks stuck, jumping and side-stepping");
                        Unstick(traps);
                        history.Clear();
                        continue;
                    }
                }
            }

            var diff = RouteSteering.AngleDiff(cam, RouteSteering.OrientationTo(here, p.Pos));
            var dx = RouteSteering.TurnDx(diff, MouseScale);
            if (dx != 0)
            {
                Native.MoveMouse(dx, 0);
            }

            // Way off: stop and turn on the spot first.
            var walk = Math.Abs(diff) < 60;
            if (walk != w)
            {
                Native.SendKey(Native.VK_W, !walk);
                w = walk;
            }

            switch (p.MoveMode)
            {
                case "fly":
                    if (walk && !Flying())
                    {
                        Tap(Native.VK_SPACE);
                        Thread.Sleep(200);
                    }

                    break;
                case "jump":
                    Tap(Native.VK_SPACE);
                    Thread.Sleep(200);
                    break;
                case "run":
                    // Hold sprint while far (BetterGI: right mouse held over 20 units).
                    var wantRun = walk && distance > 20;
                    if (wantRun != sprintHeld)
                    {
                        Native.SendKey(Native.VK_SHIFT, !wantRun);
                        sprintHeld = wantRun;
                    }

                    break;
                case "dash":
                    if (walk && distance > 20 && (DateTime.Now - sprintAt).TotalMilliseconds > 1000)
                    {
                        sprintAt = DateTime.Now;
                        Native.SendRightClick();
                    }

                    break;
                case "climb":
                    break;
                default:
                    if (walk && distance > 20 && (DateTime.Now - sprintAt).TotalMilliseconds > 2500)
                    {
                        sprintAt = DateTime.Now;
                        Native.SendRightClick();
                    }

                    break;
            }

            Thread.Sleep(60);
        }
    }

    /// <summary>Short steps until within 2 units (BetterGI MoveCloseTo).</summary>
    private Result MoveCloseTo(RoutePoint p)
    {
        ReleaseAll();
        for (var i = 0; i < 25; i++)
        {
            if (!keepGoing())
            {
                return Result.Cancelled;
            }

            var (pos, _) = Read();
            if (pos is not { } here)
            {
                continue;
            }

            if (here.DistanceTo(p.Pos) < 2)
            {
                break;
            }

            if (TurnTo(RouteSteering.OrientationTo(here, p.Pos), 2) == Result.Cancelled)
            {
                return Result.Cancelled;
            }

            Native.SendKey(Native.VK_W, false);
            Thread.Sleep(60);
            Native.SendKey(Native.VK_W, true);
            Thread.Sleep(20);
        }

        Thread.Sleep(1000);
        return Result.Arrived;
    }

    /// <summary>"orientation" points: face them without moving.</summary>
    private Result Face(RoutePoint p)
    {
        ReleaseAll();
        var (pos, _) = Read();
        var from = pos ?? _last;
        if (from is not { } here)
        {
            return Result.Arrived; // can't tell; facing is a nicety
        }

        return TurnTo(RouteSteering.OrientationTo(here, p.Pos), 5) == Result.Cancelled ? Result.Cancelled : Result.Arrived;
    }

    private Result TurnTo(int target, int maxDiff)
    {
        for (var i = 0; i < 50; i++)
        {
            if (!keepGoing())
            {
                return Result.Cancelled;
            }

            using var frame = grab();
            if (frame == null)
            {
                continue;
            }

            using var mini = new Mat(frame, MiniMapLocator.MiniMapRect);
            var diff = RouteSteering.AngleDiff(_camera.PredictRotation(mini), target);
            if (Math.Abs(diff) < maxDiff)
            {
                return Result.Arrived;
            }

            Native.MoveMouse(RouteSteering.TurnDx(diff, MouseScale), 0);
            Thread.Sleep(50);
        }

        return Result.Arrived;
    }

    /// <summary>Plunge out of the glider (BetterGI StopFlyingHandler: optional free fall, then a normal attack).</summary>
    private void StopFlying(RoutePoint p)
    {
        if (int.TryParse(p.ActionParams, out var fallMs))
        {
            Tap(Native.VK_SPACE);
            Thread.Sleep(fallMs);
            Tap(Native.VK_SPACE);
            Thread.Sleep(300);
        }

        Native.SendLeftClick();
        for (var i = 0; i < 50 && keepGoing() && Flying(); i++)
        {
            Thread.Sleep(300);
        }
    }

    private void Unstick(int attempt)
    {
        Tap(Native.VK_SPACE);
        var side = attempt % 2 == 1 ? Native.VK_A : Native.VK_D;
        Native.SendKey(side, false);
        Thread.Sleep(500);
        Native.SendKey(side, true);
    }

    private static void Tap(int vk)
    {
        Native.SendKey(vk, false);
        Thread.Sleep(40);
        Native.SendKey(vk, true);
    }

    private static void ReleaseAll()
    {
        Native.SendKey(Native.VK_W, true);
        Native.SendKey(Native.VK_SHIFT, true);
    }

    public void Dispose()
    {
        _camera.Dispose();
        _spaceKey.Dispose();
        _xKey.Dispose();
    }
}
