using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>Tracked-quest colour, which depends on the quest type.</summary>
public enum QuestColour
{
    /// <summary>Archon (main story) quests.</summary>
    Gold,

    /// <summary>Story (character) quests.</summary>
    Purple,

    /// <summary>World quests.</summary>
    Blue,

    /// <summary>Commissions, events and anything else.</summary>
    Other,
}

public readonly record struct MarkerHit(Point Centre, QuestColour Colour, double Score);

/// <summary>
/// Finds the tracked quest's on-screen marker (a diamond inside a diamond, as BetterGI's blue_track_point).
/// The shape is matched on the HSV brightness channel, so a gold, purple or blue marker matches the same way;
/// the colour is then read from the marker's own pixels.
/// </summary>
public sealed class QuestMarker : IDisposable
{
    // Minimap + quest tracker (they show the quest icon too) and the top-right buttons.
    private static readonly Rect[] UiAreas =
    [
        new(0, 0, 480, 420),
        new(1660, 0, 260, 120),
    ];

    private readonly Mat _templateV;
    private readonly Mat _mask;
    private readonly Mat _templateHalf;
    private readonly Mat _maskHalf;

    public double Threshold { get; init; } = 0.72;

    public QuestMarker()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "quest_marker.png");
        using var bgr = Cv2.ImRead(path, ImreadModes.Color);
        if (bgr.Empty())
        {
            throw new FileNotFoundException("Missing template image", path);
        }

        _templateV = Brightness(bgr);
        _mask = DiamondMask(bgr.Width, bgr.Height);
        _templateHalf = _templateV.Resize(new OpenCvSharp.Size(bgr.Width / 2, bgr.Height / 2), 0, 0, InterpolationFlags.Area);
        _maskHalf = DiamondMask(bgr.Width / 2, bgr.Height / 2);
    }

    /// <summary>The diamond the template image is cut to (its corners are not part of the marker).</summary>
    public static Mat DiamondMask(int w, int h)
    {
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        var cx = (w - 1) / 2.0;
        var cy = (h - 1) / 2.0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (Math.Abs(x - cx) / (w / 2.0) + Math.Abs(y - cy) / (h / 2.0) <= 1.0)
                {
                    mask.Set(y, x, (byte)255);
                }
            }
        }

        return mask;
    }

    /// <summary>HSV "value" (the brightest of B, G, R): the same for a gold, purple or blue marker.</summary>
    private static Mat Brightness(Mat bgr)
    {
        var channels = bgr.Split();
        var v = new Mat();
        Cv2.Max(channels[0], channels[1], v);
        Cv2.Max(v, channels[2], v);
        foreach (var c in channels)
        {
            c.Dispose();
        }

        return v;
    }

    /// <summary>
    /// Coarse search at half size (a full-size masked match takes ~170 ms), then each candidate is
    /// confirmed at full size in a small window around it.
    /// </summary>
    public MarkerHit? Find(Mat bgr)
    {
        using var v = Brightness(bgr);
        using var half = v.Resize(new OpenCvSharp.Size(v.Width / 2, v.Height / 2), 0, 0, InterpolationFlags.Area);
        using var coarse = Match(half, _templateHalf, _maskHalf);

        for (var i = 0; i < 8; i++)
        {
            Cv2.MinMaxLoc(coarse, out _, out var coarseMax, out _, out var loc);
            if (coarseMax < Threshold - 0.15)
            {
                break;
            }

            Blank(coarse, loc, _templateHalf.Width, _templateHalf.Height);
            var window = new Rect(loc.X * 2 - 6, loc.Y * 2 - 6, _templateV.Width + 12, _templateV.Height + 12)
                         & new Rect(0, 0, v.Width, v.Height);
            if (window.Width < _templateV.Width || window.Height < _templateV.Height)
            {
                continue;
            }

            using var area = new Mat(v, window);
            using var fine = Match(area, _templateV, _mask);
            Cv2.MinMaxLoc(fine, out _, out var max, out _, out var fineLoc);
            if (max < Threshold)
            {
                continue;
            }

            var rect = new Rect(window.X + fineLoc.X, window.Y + fineLoc.Y, _templateV.Width, _templateV.Height);
            var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            if (UiAreas.Any(a => a.Contains(centre)))
            {
                continue;
            }

            // A white or grey diamond (no colour) is not a quest marker.
            if (ColourOf(bgr, rect) is { } colour)
            {
                return new MarkerHit(centre, colour, max);
            }
        }

        return null;
    }

    private static Mat Match(Mat image, Mat template, Mat mask)
    {
        var result = new Mat();
        Cv2.MatchTemplate(image, template, result, TemplateMatchModes.CCoeffNormed, mask);
        // Masked matching yields NaN/inf on flat areas; treat those as no match.
        Cv2.PatchNaNs(result, -1);
        using var inf = new Mat();
        Cv2.InRange(result, new Scalar(1.01), new Scalar(double.MaxValue), inf);
        result.SetTo(new Scalar(-1), inf);
        return result;
    }

    private static void Blank(Mat result, Point loc, int w, int h)
    {
        var x0 = Math.Max(0, loc.X - w / 2);
        var y0 = Math.Max(0, loc.Y - h / 2);
        var x1 = Math.Min(result.Width, loc.X + w / 2 + 1);
        var y1 = Math.Min(result.Height, loc.Y + h / 2 + 1);
        result[new Rect(x0, y0, x1 - x0, y1 - y0)].SetTo(new Scalar(-1));
    }

    /// <summary>Quest colour from the saturated pixels inside the diamond, or null if it has hardly any colour.</summary>
    private QuestColour? ColourOf(Mat bgr, Rect rect)
    {
        using var hsv = new Mat(bgr, rect).CvtColor(ColorConversionCodes.BGR2HSV);
        var hues = new List<int>();
        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                if (_mask.At<byte>(y, x) == 0)
                {
                    continue;
                }

                var px = hsv.At<Vec3b>(y, x);
                if (px.Item1 >= 80 && px.Item2 >= 120)
                {
                    hues.Add(px.Item0);
                }
            }
        }

        if (hues.Count < 60)
        {
            return null;
        }

        hues.Sort();
        return Classify(hues[hues.Count / 2]);
    }

    /// <summary>OpenCV hue (0..180) to quest colour.</summary>
    public static QuestColour Classify(int hue) => hue switch
    {
        >= 12 and <= 35 => QuestColour.Gold,
        >= 90 and < 125 => QuestColour.Blue,
        >= 125 and <= 165 => QuestColour.Purple,
        _ => QuestColour.Other,
    };

    public void Dispose()
    {
        _templateV.Dispose();
        _mask.Dispose();
        _templateHalf.Dispose();
        _maskHalf.Dispose();
    }
}
