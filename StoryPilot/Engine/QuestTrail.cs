using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>Where the quest trail leads: the point to aim at (on a line fitted through it), and whether it bends sharply further on.</summary>
public readonly record struct TrailHit(Point Aim, bool SharpCurve, int Sparkles);

/// <summary>
/// Finds the glowing navigation trail (shown for a while after pressing V): a dotted line of small golden
/// sparkles on the ground leading toward the quest. Their cores are pure yellow (hue ~30) at full
/// brightness with medium saturation, which sets them apart from lamps (more orange, hue ~17), the
/// vivid quest marker and the scenery. Only the part ahead of the character counts.
/// </summary>
public static class QuestTrail
{
    /// <summary>Trail below this line is beside or behind the character's feet.</summary>
    public const int FeetY = 820;

    /// <summary>Aim at the trail about this high on screen: some way ahead, not right under the feet.</summary>
    public const int LookAheadY = 460;

    private const int TopY = 150;
    private const int BandHalfHeight = 70;
    private const int MinSparkles = 4;

    /// <summary>Bend between the near and the far half of the visible trail that counts as sharp (degrees).</summary>
    public const double SharpCurveDegrees = 35;

    // Same HUD parts as the quest marker search.
    private static readonly Rect[] UiAreas =
    [
        new(0, 0, 560, 330),
        new(1260, 0, 660, 130),
        new(1560, 220, 360, 400),
        new(1640, 760, 280, 320),
        new(0, 950, 1920, 130),
    ];

    public static TrailHit? Find(Mat bgr)
    {
        var sparkles = Sparkles(bgr);
        return sparkles.Count < MinSparkles ? null : Analyse(sparkles);
    }

    /// <summary>Centres (and pixel counts) of the trail sparkles ahead of the character.</summary>
    public static List<(Point Centre, int Pixels)> Sparkles(Mat bgr)
    {
        using var hsv = bgr.CvtColor(ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(26, 70, 235), new Scalar(34, 235, 255), mask);
        var frame = new Rect(0, 0, mask.Width, mask.Height);
        foreach (var ui in UiAreas)
        {
            mask[ui & frame].SetTo(Scalar.All(0));
        }

        mask[new Rect(0, 0, mask.Width, TopY) & frame].SetTo(Scalar.All(0));
        mask[new Rect(0, FeetY, mask.Width, Math.Max(0, mask.Height - FeetY)) & frame].SetTo(Scalar.All(0));

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var found = new List<(Point, int)>();
        for (var i = 1; i < n; i++)
        {
            var area = stats.At<int>(i, 4);
            var w = stats.At<int>(i, 2);
            var h = stats.At<int>(i, 3);

            // Sparkles are small; a big patch is a lamp, a window or the marker.
            if (area is < 3 or > 400 || w > 45 || h > 45)
            {
                continue;
            }

            found.Add((new Point((int)centroids.At<double>(i, 0), (int)centroids.At<double>(i, 1)), area));
        }

        return found;
    }

    /// <summary>Aim point and bend from sparkle centres (screen coordinates, 1080p).</summary>
    public static TrailHit? Analyse(IReadOnlyList<(Point Centre, int Pixels)> sparkles)
    {
        if (sparkles.Count < MinSparkles)
        {
            return null;
        }

        var minY = sparkles.Min(s => s.Centre.Y);
        var maxY = sparkles.Max(s => s.Centre.Y);

        // A few sparkles at the same height (a lit window sill, a row of lamps) are not a trail leading somewhere.
        if (maxY - minY < BandHalfHeight)
        {
            return null;
        }

        // Aim at the look-ahead height on a straight line fitted through the whole visible trail, not at the
        // sparkles there: the game sometimes draws the trail zigzagging, and the fit averages that out.
        // If the visible trail is all nearer (or all further), use its far (or near) end.
        var bandY = Math.Clamp(LookAheadY, minY + BandHalfHeight / 2, maxY - BandHalfHeight / 2);
        var aim = new Point((int)Math.Round(FitX(sparkles, bandY)), bandY);

        // Bend: direction of the near third against the far third of the trail.
        var third = (maxY - minY) / 3;
        var near = Centre(sparkles, maxY - third, maxY);
        var mid = Centre(sparkles, minY + third, maxY - third);
        var far = Centre(sparkles, minY, minY + third);
        var sharp = false;
        if (near is { } a && mid is { } b && far is { } c)
        {
            var bend = Math.Abs(AngleDegrees(a, b) - AngleDegrees(b, c));
            sharp = Math.Min(bend, 360 - bend) > SharpCurveDegrees;
        }

        return new TrailHit(aim, sharp, sparkles.Count);
    }

    /// <summary>
    /// x at height <paramref name="y"/> on the line x = a + b·y fitted (pixel-weighted) through the sparkles.
    /// Fitted twice: the second time without sparkles far off the first line (a stray spark in the scenery).
    /// </summary>
    public static double FitX(IReadOnlyList<(Point Centre, int Pixels)> sparkles, int y)
    {
        var (a, b) = FitLine(sparkles);
        var residuals = sparkles.Select(s => Math.Abs(s.Centre.X - (a + b * s.Centre.Y))).OrderBy(r => r).ToList();
        var limit = Math.Max(40, 3 * residuals[residuals.Count / 2]);
        var kept = sparkles.Where(s => Math.Abs(s.Centre.X - (a + b * s.Centre.Y)) <= limit).ToList();
        if (kept.Count >= MinSparkles)
        {
            (a, b) = FitLine(kept);
        }

        return a + b * y;
    }

    private static (double A, double B) FitLine(IReadOnlyList<(Point Centre, int Pixels)> sparkles)
    {
        double sw = 0, sx = 0, sy = 0, syy = 0, sxy = 0;
        foreach (var (p, px) in sparkles)
        {
            sw += px;
            sx += px * p.X;
            sy += px * p.Y;
            syy += (double)px * p.Y * p.Y;
            sxy += (double)px * p.X * p.Y;
        }

        var spread = sw * syy - sy * sy;
        if (spread <= 0)
        {
            return (sx / sw, 0);
        }

        var b = (sw * sxy - sx * sy) / spread;
        return ((sx - b * sy) / sw, b);
    }

    /// <summary>Pixel-weighted centre of the sparkles between two heights.</summary>
    private static Point? Centre(IReadOnlyList<(Point Centre, int Pixels)> sparkles, int fromY, int toY)
    {
        double sx = 0, sy = 0, sw = 0;
        foreach (var (p, px) in sparkles)
        {
            if (p.Y >= fromY && p.Y <= toY)
            {
                sx += p.X * px;
                sy += p.Y * px;
                sw += px;
            }
        }

        return sw == 0 ? null : new Point((int)(sx / sw), (int)(sy / sw));
    }

    /// <summary>Direction from <paramref name="from"/> to <paramref name="to"/>, 0 = straight up the screen.</summary>
    private static double AngleDegrees(Point from, Point to) =>
        Math.Atan2(to.X - from.X, from.Y - to.Y) * 180 / Math.PI;
}

/// <summary>
/// Sprinting in random bursts (as a person would): Shift held for 1-4 s, then released for 2-6 s.
/// Only while sprinting is allowed (walking straight, not near the end); a disallowed moment ends a burst.
/// </summary>
public sealed class SprintBursts(Random random)
{
    private bool _on;
    private DateTime _until = DateTime.MinValue;

    public bool Update(DateTime now, bool allowed)
    {
        if (_on && !allowed)
        {
            // End this burst; rest before the next one.
            _on = false;
            _until = now.AddSeconds(2 + random.NextDouble() * 4);
            return false;
        }

        if (now >= _until)
        {
            _on = !_on && allowed;
            _until = now.AddSeconds(_on ? 1 + random.NextDouble() * 3 : 2 + random.NextDouble() * 4);
        }

        return _on && allowed;
    }
}
