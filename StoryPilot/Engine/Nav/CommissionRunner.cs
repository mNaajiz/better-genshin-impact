using System.Text.Json;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine.Nav;

/// <summary>
/// Daily commissions, game in front. Mirrors the community AutoCommissionNova flow without reading text:
/// F1 → Commissions → for each of the 4 entries, its locate button opens the big map centred on the marker;
/// the map position (SIFT) picks the community route that ends there. Combat ones are then done:
/// teleport to the route's waypoint, walk the route with minimap tracking, fight with your rotation.
/// Saves a frame of every screen it relies on to Pictures\StoryPilot\commission (to make exact templates later).
/// </summary>
public sealed class CommissionRunner(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log, Action<string> status) : IDisposable
{
    // Commission page of the Adventurer Handbook at 1920x1080 (AutoCommissionNova game-constants.js).
    private static readonly Point CommissionTab = new(300, 350);
    private static readonly Point[] LocateButtons = [new(1550, 320), new(1550, 440), new(1550, 530), new(1550, 560)];
    private static readonly Rect[] StatusAreas = [new(1510, 270, 80, 100), new(1510, 370, 80, 100), new(1510, 470, 80, 100), new(1510, 520, 80, 100)];

    private const int VK_F1 = 0x70;
    private const int VK_M = 0x4D;

    private readonly MapVision _map = new();
    private readonly Template _track = new("commission_track.png", new Rect(1428, 965, 87, 86), 0.8);
    private readonly Template _paimon = new("paimon_menu.png", new Rect(0, 0, 500, 500), 0.8);
    private readonly List<Template> _done = [];
    private readonly List<Template> _notDone = [];

    private MiniMapLocator? _mini;
    private BigMapLocator? _big;
    private CommissionCatalog? _catalog;
    private nint _hWnd;
    private GameInput? _input;
    private int _clientW = Vision.Width, _clientH = Vision.Height;

    public sealed record Found(int Index, bool? Completed, Point2f? Marker, CommissionEntry? Entry);

    public static string MapDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Map", "Teyvat");

    public static string CatalogFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Commissions", "mondstadt.json");

    /// <summary>Loads the map data (a few seconds, about 1 GB) the first time.</summary>
    private bool EnsureLoaded()
    {
        if (_catalog != null)
        {
            return true;
        }

        if (!Directory.Exists(MapDir))
        {
            log($"Commissions need BetterGI's map pieces in {MapDir} (see README)");
            return false;
        }

        status("Commissions: loading map data…");
        _big = BigMapLocator.Load(MapDir);
        _mini = MiniMapLocator.Load(MapDir);
        _catalog = CommissionCatalog.Load(CatalogFile);
        for (var i = 0; i < 4; i++)
        {
            _done.Add(new Template("commission_completed.png", StatusAreas[i], 0.8));
            _notDone.Add(new Template("commission_uncompleted.png", StatusAreas[i], 0.8));
        }

        log($"Commissions: {_mini.LayerCount} map pieces, {_catalog.Entries.Count} known routes");
        return _big != null && _mini.LayerCount > 0;
    }

    public void Run(nint hWnd, GameInput input, string rotationText, int clientW, int clientH)
    {
        _hWnd = hWnd;
        _input = input;
        _clientW = clientW;
        _clientH = clientH;
        if (!input.IsGameInFront)
        {
            log("Commissions: bring the game to the front first (they need real mouse and keyboard)");
            return;
        }

        List<CombatStep> rotation;
        try
        {
            rotation = DomainCombat.Parse(rotationText);
        }
        catch (FormatException e)
        {
            log("Commissions: " + e.Message);
            return;
        }

        if (!EnsureLoaded())
        {
            return;
        }

        status("Commissions: reading today's list");
        var found = Scan();
        foreach (var f in found)
        {
            var what = f.Entry != null ? $"{f.Entry} ({(f.Entry.IsCombat ? "combat" : "NPC")})" : "unknown route";
            var where = f.Marker is { } m ? $"({m.X:F0}, {m.Y:F0})" : "not located";
            log($"Commission {f.Index + 1}: {(f.Completed == true ? "done" : "to do")}, marker {where}, {what}");
        }

        foreach (var f in found.Where(f => f.Completed != true && f.Entry is { IsCombat: true }))
        {
            if (!keepGoing())
            {
                return;
            }

            status($"Commissions: {f.Index + 1} of 4 ({f.Entry!.Variant})");
            DoCombat(f, rotation);
        }

        var skipped = found.Count(f => f.Completed != true && f.Entry is not { IsCombat: true });
        status(skipped > 0
            ? $"Commissions: done what I can; {skipped} need you (NPC or unknown)"
            : "Commissions: all combat commissions attempted");
    }

    // ---------- handbook ----------

    private bool OpenCommissionPage()
    {
        ToMainUi();
        Key(VK_F1);
        if (!Sleep(1800))
        {
            return false;
        }

        Click(CommissionTab);
        if (!Sleep(1500))
        {
            return false;
        }

        Save("handbook");
        return true;
    }

    public List<Found> Scan()
    {
        var found = new List<Found>();
        if (!OpenCommissionPage())
        {
            return found;
        }

        for (var i = 0; i < 4 && keepGoing(); i++)
        {
            if (i == 3)
            {
                ScrollList();
            }

            bool? completed = null;
            using (var frame = Grab())
            {
                if (frame != null)
                {
                    using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
                    completed = Vision.Find(grey, _done[i]) != null ? true : Vision.Find(grey, _notDone[i]) != null ? false : null;
                }
            }

            var marker = LocateFromHandbook(i);
            var entry = marker is { } m ? _catalog!.Nearest(m) : null;
            found.Add(new Found(i, completed, marker, entry));

            // Back from the map to the commission page.
            Key(Native.VK_ESCAPE);
            if (!Sleep(1500))
            {
                break;
            }

            if (MapOpen())
            {
                // Landed back in the map instead of the page: reopen the page.
                if (!OpenCommissionPage())
                {
                    break;
                }
            }
        }

        ToMainUi();
        return found;
    }

    /// <summary>Clicks entry <paramref name="index"/>'s locate button and reads the map position at the screen centre.</summary>
    private Point2f? LocateFromHandbook(int index)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Click(LocateButtons[index]);
            if (!Sleep(1500))
            {
                return null;
            }

            using var frame = Grab();
            if (frame == null)
            {
                continue;
            }

            using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            var trackShown = Vision.Find(grey, _track) != null;
            if (!trackShown && !_map.IsMapOpen(grey))
            {
                continue;
            }

            Save($"map_{index + 1}");
            using var view = _big!.Locate(frame);
            if (view == null)
            {
                log($"Commission {index + 1}: map opened but couldn't tell where");
                return null;
            }

            return view.Centre;
        }

        log($"Commission {index + 1}: its locate button didn't open the map");
        return null;
    }

    /// <summary>The 4th commission needs the list dragged up (AutoCommissionNova pageScroll).</summary>
    private void ScrollList()
    {
        var p = ToScreen(new Point(950, 600));
        Native.SetCursorPos(p.X, p.Y);
        Thread.Sleep(100);
        Native.SendLeftButton(true);
        for (var j = 0; j < 20; j++)
        {
            Native.MoveMouse(0, -10);
            Thread.Sleep(10);
        }

        Thread.Sleep(100);
        Native.SendLeftButton(false);
        Thread.Sleep(500);
    }

    // ---------- one combat commission ----------

    private void DoCombat(Found f, IReadOnlyList<CombatStep> rotation)
    {
        var entry = f.Entry!;
        if (entry.Teleport is not { } tp)
        {
            log($"{entry}: route has no teleport point");
            return;
        }

        if (!TeleportTo(f.Index, tp.Pos))
        {
            log($"{entry}: couldn't teleport to the route's waypoint, skipping");
            return;
        }

        using var walker = new RouteWalker(_mini!, Grab, keepGoing, log);
        _mini!.Reset();
        var fighter = new CommissionFighter(Grab, keepGoing, log);
        var first = true;
        foreach (var step in entry.Steps.EnumerateArray())
        {
            if (!keepGoing())
            {
                return;
            }

            var type = step.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (type)
            {
                case "地图追踪": // follow a route file
                {
                    var file = step.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "_path.json";
                    if (!entry.Routes.TryGetValue(file, out var route))
                    {
                        log($"{entry}: missing route {file}");
                        return;
                    }

                    status($"Commissions: walking to {entry.Variant}");
                    var r = walker.Walk(route, first ? tp.Pos : walker.LastPosition);
                    first = false;
                    if (r != RouteWalker.Result.Arrived)
                    {
                        log($"{entry}: walking ended with {r}");
                        if (r == RouteWalker.Result.Cancelled)
                        {
                            return;
                        }
                    }

                    break;
                }

                case "自动战斗": // fight
                case "摧毁哨塔": // destroy watchtowers (attacks hit them too)
                case "摧毁史莱姆气球": // slime balloons
                    status($"Commissions: fighting at {entry.Variant}");
                    fighter.Fight(rotation);
                    break;
                case "在附近交互": // interact nearby (e.g. start the ley line guard)
                    Key(Native.VK_F);
                    Sleep(1500);
                    break;
                default:
                    log($"{entry}: step \"{type}\" not supported yet, skipped");
                    break;
            }
        }

        log($"{entry}: finished its steps");
    }

    /// <summary>
    /// Opens the big map on the commission (handbook locate button), finds the route's waypoint on screen
    /// from the map position, clicks it and presses Teleport.
    /// </summary>
    private bool TeleportTo(int index, Point2f waypoint)
    {
        if (!OpenCommissionPage())
        {
            return false;
        }

        if (index == 3)
        {
            ScrollList();
        }

        Click(LocateButtons[index]);
        if (!Sleep(1500))
        {
            return false;
        }

        for (var attempt = 0; attempt < 4 && keepGoing(); attempt++)
        {
            Point target;
            using (var frame = Grab())
            {
                if (frame == null)
                {
                    continue;
                }

                using var view = _big!.Locate(frame);
                if (view == null)
                {
                    log("Teleport: couldn't place the map");
                    break;
                }

                target = view.WorldToScreenPoint(waypoint);
            }

            // Off screen (or under the side panel): drag the map so the waypoint comes to the middle.
            if (target.X < 150 || target.X > 1400 || target.Y < 120 || target.Y > 960)
            {
                DragMap(new Point(target.X - 960, target.Y - 540));
                Sleep(600);
                continue;
            }

            Click(target);
            if (!Sleep(1000))
            {
                return false;
            }

            using (var frame = Grab())
            using (var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                if (grey != null && _map.FindWaypointInList(grey) is { } row)
                {
                    Click(row); // several icons overlapped: pick the waypoint row
                    Sleep(800);
                }
            }

            using (var frame = Grab())
            using (var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                if (grey != null && _map.FindTeleportButton(grey) is { } button)
                {
                    Click(button);
                    Sleep(1500);
                    return WaitForMainUi(20);
                }
            }

            Save("teleport_no_button");
            Key(Native.VK_ESCAPE);
            Sleep(600);
        }

        ToMainUi();
        return false;
    }

    /// <summary>Drags the map by <paramref name="delta"/> screen pixels the other way, so that point moves to the centre.</summary>
    private void DragMap(Point delta)
    {
        var start = ToScreen(new Point(960, 540));
        Native.SetCursorPos(start.X, start.Y);
        Thread.Sleep(80);
        Native.SendLeftButton(true);
        const int steps = 20;
        var sx = _clientW / (double)Vision.Width;
        var sy = _clientH / (double)Vision.Height;
        for (var i = 0; i < steps; i++)
        {
            Native.MoveMouse((int)Math.Round(-delta.X * sx / steps), (int)Math.Round(-delta.Y * sy / steps));
            Thread.Sleep(15);
        }

        Thread.Sleep(100);
        Native.SendLeftButton(false);
    }

    // ---------- helpers ----------

    private bool WaitForMainUi(int seconds)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until)
        {
            if (!Sleep(500))
            {
                return false;
            }

            if (InMainUi())
            {
                Sleep(800);
                return true;
            }
        }

        return false;
    }

    private bool InMainUi()
    {
        using var frame = Grab();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null && Vision.Find(grey, _paimon) != null;
    }

    private bool MapOpen()
    {
        using var frame = Grab();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null && _map.IsMapOpen(grey);
    }

    private void ToMainUi()
    {
        for (var i = 0; i < 4 && keepGoing() && !InMainUi(); i++)
        {
            Key(Native.VK_ESCAPE);
            Sleep(900);
        }
    }

    private Mat? Grab()
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

        var r = raw.Resize(new OpenCvSharp.Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        raw.Dispose();
        return r;
    }

    private Point ToClient(Point p) =>
        new((int)Math.Round(p.X * _clientW / (double)Vision.Width), (int)Math.Round(p.Y * _clientH / (double)Vision.Height));

    private Point ToScreen(Point p)
    {
        var c = ToClient(p);
        var pt = new Native.POINT { X = c.X, Y = c.Y };
        Native.ClientToScreen(_hWnd, ref pt);
        return new Point(pt.X, pt.Y);
    }

    private void Click(Point p1080)
    {
        var c = ToClient(p1080);
        _input!.ForegroundClick(c.X, c.Y);
    }

    private static void Key(int vk)
    {
        Native.SendKey(vk, false);
        Thread.Sleep(50);
        Native.SendKey(vk, true);
    }

    private bool Sleep(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (!keepGoing())
            {
                return false;
            }

            Thread.Sleep(30);
        }

        return true;
    }

    private void Save(string name)
    {
        try
        {
            using var frame = Grab();
            if (frame == null)
            {
                return;
            }

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "StoryPilot", "commission");
            Directory.CreateDirectory(dir);
            Cv2.ImWrite(Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png"), frame);
        }
        catch (Exception e)
        {
            log("Couldn't save a frame: " + e.Message);
        }
    }

    public void Dispose()
    {
        _map.Dispose();
        _track.Dispose();
        _paimon.Dispose();
        _done.ForEach(t => t.Dispose());
        _notDone.ForEach(t => t.Dispose());
        _mini?.Dispose();
        _big?.Dispose();
    }
}
