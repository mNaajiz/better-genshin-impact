using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// A grey-scale template with the region of the 1920x1080 frame it is searched in.
/// Templates and regions come from BetterGI's AutoSkip / AutoPick / Element / QuickTeleport assets.
/// </summary>
public sealed class Template : IDisposable
{
    public string Name { get; }
    public Mat Image { get; }
    public Mat? Mask { get; }
    public Rect Roi { get; }
    public double Threshold { get; }

    public Template(string file, Rect roi, double threshold = 0.8, bool blackIsTransparent = false)
    {
        Name = Path.GetFileNameWithoutExtension(file);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        using var color = Cv2.ImRead(path, ImreadModes.Color);
        if (color.Empty())
        {
            throw new FileNotFoundException("Missing template image", path);
        }

        Image = color.CvtColor(ColorConversionCodes.BGR2GRAY);
        if (blackIsTransparent)
        {
            using var black = new Mat();
            Cv2.InRange(color, new Scalar(0, 0, 0), new Scalar(0, 0, 0), black);
            Mask = ~black;
        }

        Roi = roi;
        Threshold = threshold;
    }

    public void Dispose()
    {
        Image.Dispose();
        Mask?.Dispose();
    }
}

public static class Vision
{
    public const int Width = 1920;
    public const int Height = 1080;

    /// <summary>
    /// Best match location (in frame coordinates) if it beats the template's threshold.
    /// </summary>
    public static Rect? Find(Mat grey, Template t)
    {
        var roi = Clamp(t.Roi, grey);
        if (roi.Width < t.Image.Width || roi.Height < t.Image.Height)
        {
            return null;
        }

        using var area = new Mat(grey, roi);
        using var result = new Mat();
        if (t.Mask != null)
        {
            Cv2.MatchTemplate(area, t.Image, result, TemplateMatchModes.CCoeffNormed, t.Mask);
        }
        else
        {
            Cv2.MatchTemplate(area, t.Image, result, TemplateMatchModes.CCoeffNormed);
        }

        Cv2.MinMaxLoc(result, out _, out var max, out _, out var loc);
        if (max < t.Threshold || double.IsNaN(max) || double.IsInfinity(max))
        {
            return null;
        }

        return new Rect(roi.X + loc.X, roi.Y + loc.Y, t.Image.Width, t.Image.Height);
    }

    /// <summary>
    /// All non-overlapping matches above the threshold, up to maxCount.
    /// </summary>
    public static List<Rect> FindAll(Mat grey, Template t, int maxCount = 8)
    {
        var found = new List<Rect>();
        var roi = Clamp(t.Roi, grey);
        if (roi.Width < t.Image.Width || roi.Height < t.Image.Height)
        {
            return found;
        }

        using var area = new Mat(grey, roi);
        using var result = new Mat();
        Cv2.MatchTemplate(area, t.Image, result, TemplateMatchModes.CCoeffNormed);
        for (var i = 0; i < maxCount; i++)
        {
            Cv2.MinMaxLoc(result, out _, out var max, out _, out var loc);
            if (max < t.Threshold || double.IsNaN(max))
            {
                break;
            }

            found.Add(new Rect(roi.X + loc.X, roi.Y + loc.Y, t.Image.Width, t.Image.Height));
            // Blank out this match so the next MinMaxLoc finds a different one.
            var x0 = Math.Max(0, loc.X - t.Image.Width / 2);
            var y0 = Math.Max(0, loc.Y - t.Image.Height / 2);
            var x1 = Math.Min(result.Width, loc.X + t.Image.Width / 2 + 1);
            var y1 = Math.Min(result.Height, loc.Y + t.Image.Height / 2 + 1);
            result[new Rect(x0, y0, x1 - x0, y1 - y0)].SetTo(new Scalar(-1));
        }

        return found;
    }

    /// <summary>
    /// Share of pixels in the BGR crop that are the orange quest-option text colour
    /// (RGB 243..255, 195..205, 48..55 — same range BetterGI uses).
    /// </summary>
    public static double OrangeRatio(Mat bgr, Rect rect)
    {
        rect = Clamp(rect, bgr);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return 0;
        }

        using var crop = new Mat(bgr, rect);
        using var mask = new Mat();
        Cv2.InRange(crop, new Scalar(48, 195, 243), new Scalar(55, 205, 255), mask);
        return Cv2.CountNonZero(mask) / (double)(rect.Width * rect.Height);
    }

    /// <summary>
    /// Share of pure-black pixels in the middle third of the frame (black-screen narration check).
    /// </summary>
    public static double MiddleBlackRatio(Mat grey)
    {
        using var band = new Mat(grey, new Rect(0, grey.Height / 3, grey.Width, grey.Height / 3));
        using var mask = new Mat();
        Cv2.InRange(band, new Scalar(0), new Scalar(0), mask);
        return Cv2.CountNonZero(mask) / (double)(band.Width * band.Height);
    }

    /// <summary>
    /// Finds the small yellow or blue triangle at the bottom centre that means "click to continue"
    /// on item / tutorial pop-ups during quests. Returns its centre in frame coordinates.
    /// </summary>
    public static Point? FindBottomTriangle(Mat bgr)
    {
        var rect = new Rect(945, 980, 30, 80);
        using var crop = new Mat(bgr, rect);
        using var hsv = crop.CvtColor(ColorConversionCodes.BGR2HSV);
        using var yellow = new Mat();
        using var blue = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 240, 229), new Scalar(25, 255, 255), yellow);
        Cv2.InRange(hsv, new Scalar(90, 156, 145), new Scalar(99, 208, 253), blue);
        Cv2.FindContours(yellow, out var yc, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Cv2.FindContours(blue, out var bc, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in yc.Concat(bc))
        {
            var area = Cv2.ContourArea(contour);
            var approx = Cv2.ApproxPolyDP(contour, 0.04 * Cv2.ArcLength(contour, true), true);
            if (area < 10 || area > 50 || approx.Length != 3)
            {
                continue;
            }

            var b = Cv2.BoundingRect(approx);
            return new Point(rect.X + b.X + b.Width / 2, rect.Y + b.Y + b.Height / 2);
        }

        return null;
    }

    private static Rect Clamp(Rect r, Mat m)
    {
        var x = Math.Clamp(r.X, 0, m.Width);
        var y = Math.Clamp(r.Y, 0, m.Height);
        var w = Math.Clamp(r.Width, 0, m.Width - x);
        var h = Math.Clamp(r.Height, 0, m.Height - y);
        return new Rect(x, y, w, h);
    }
}
