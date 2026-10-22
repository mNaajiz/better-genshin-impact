using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// What StoryPilot needs to see on the big map (all on a 1920x1080 frame, English client):
/// teleport waypoints, whether each is unlocked, the player arrow, and the Teleport button.
/// </summary>
public sealed class MapVision : IDisposable
{
    public static readonly Point Centre = new(Vision.Width / 2, Vision.Height / 2);

    // Parts of the map screen covered by UI, where a "waypoint" match is not a real map icon.
    private static readonly Rect[] UiAreas =
    [
        new(0, 0, Vision.Width, 90),          // top bar (resin, abyss, close)
        new(0, 90, 380, 340),                 // reward list on the left
        new(0, 420, 95, 260),                 // zoom slider
        new(0, 980, 120, 100),                // settings button
        new(720, 985, 480, 95),               // bottom centre button
        new(1470, 975, 450, 105),             // region name / exploration progress
    ];

    private readonly Template _waypoint = new("waypoint_on_map.png", new Rect(0, 0, Vision.Width, Vision.Height), threshold: 0.9, useAlpha: true);
    private readonly Template _teleportButton = new("teleport_button.png", new Rect(1440, 960, 100, 120));
    private readonly Template _waypointInList = new("waypoint_in_list.png", new Rect(1270, 100, 50, 880), threshold: 0.75);
    private readonly Template _mapScale = new("map_scale_button.png", new Rect(30, 440, 40, 200), blackIsTransparent: true);
    private readonly Template _mapSettings = new("map_settings_button.png", new Rect(25, 990, 58, 62));
    private readonly Template _paimonMenu = new("paimon_menu.png", new Rect(0, 0, 480, 270));

    /// <summary>The normal game view (Paimon icon top-left).</summary>
    public bool IsMainUi(Mat grey) => Vision.Find(grey, _paimonMenu) != null;

    public bool IsMapOpen(Mat grey) => Vision.Find(grey, _mapScale) != null || Vision.Find(grey, _mapSettings) != null;

    /// <summary>Centre of the Teleport button in the side panel, if the panel is open.</summary>
    public Point? FindTeleportButton(Mat grey) => Vision.Find(grey, _teleportButton) is { } r ? Centre_(r) : null;

    /// <summary>When several icons overlap, the game shows a list; this finds the waypoint row in it.</summary>
    public Point? FindWaypointInList(Mat grey) => Vision.Find(grey, _waypointInList) is { } r ? Centre_(r) : null;

    /// <summary>Unlocked teleport waypoints (grey pins are locked and are skipped).</summary>
    public List<Point> FindUnlockedWaypoints(Mat bgr, Mat grey)
    {
        var list = new List<Point>();
        foreach (var r in Vision.FindAll(grey, _waypoint, maxCount: 60))
        {
            var c = Centre_(r);
            if (UiAreas.Any(a => a.Contains(c)))
            {
                continue;
            }

            if (BluePixels(bgr, r, _waypoint.Mask) >= 12)
            {
                list.Add(c);
            }
        }

        return list;
    }

    /// <summary>The bright cyan player arrow, if it is on screen.</summary>
    public static Point? FindPlayer(Mat bgr)
    {
        using var mask = new Mat();
        // BGR: blue high, green high, red low (arrow is about RGB 0,214,255).
        Cv2.InRange(bgr, new Scalar(230, 200, 0), new Scalar(255, 255, 60), mask);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        Point? best = null;
        var bestArea = 0;
        for (var i = 1; i < n; i++)
        {
            var w = stats.At<int>(i, 2);
            var h = stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);
            var c = new Point((int)centroids.At<double>(i, 0), (int)centroids.At<double>(i, 1));
            if (area is < 250 or > 2000 || w is < 18 or > 70 || h is < 18 or > 70 || UiAreas.Any(a => a.Contains(c)))
            {
                continue;
            }

            if (area > bestArea)
            {
                bestArea = area;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// The waypoint to use for a quest at <paramref name="quest"/>: the one nearest to it, but only if it is
    /// meaningfully closer to the quest than the player already is (less than half the player's distance).
    /// </summary>
    public static Point? ChooseWaypoint(IReadOnlyList<Point> waypoints, Point quest, Point? player)
    {
        if (waypoints.Count == 0)
        {
            return null;
        }

        var nearest = waypoints.MinBy(w => Distance(w, quest));
        // Player off screen means farther than anything visible.
        var playerDistance = player is { } p ? Distance(p, quest) : double.MaxValue;
        return Distance(nearest, quest) < playerDistance * 0.5 ? nearest : null;
    }

    public static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>Blue-diamond pixels inside the pin's shape (the map around it is ignored).</summary>
    private static int BluePixels(Mat bgr, Rect r, Mat? shape)
    {
        var box = new Rect(r.X, r.Y, r.Width, r.Height) & new Rect(0, 0, bgr.Width, bgr.Height);
        using var crop = new Mat(bgr, box);
        using var mask = new Mat();
        // The diamond inside an unlocked waypoint: strong blue, little red (RGB ~ 40,140,220).
        Cv2.InRange(crop, new Scalar(171, 0, 0), new Scalar(255, 255, 109), mask);
        var count = 0;
        for (var y = 0; y < crop.Rows; y++)
        {
            for (var x = 0; x < crop.Cols; x++)
            {
                if (mask.At<byte>(y, x) == 0 || (shape != null && (y >= shape.Rows || x >= shape.Cols || shape.At<byte>(y, x) == 0)))
                {
                    continue;
                }

                var px = crop.At<Vec3b>(y, x);
                if (px.Item0 - px.Item2 > 90)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Point Centre_(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public void Dispose()
    {
        _waypoint.Dispose();
        _teleportButton.Dispose();
        _waypointInList.Dispose();
        _mapScale.Dispose();
        _mapSettings.Dispose();
        _paimonMenu.Dispose();
    }
}
