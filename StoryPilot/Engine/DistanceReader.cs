using System.Drawing;
using System.Drawing.Text;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine;

/// <summary>
/// Reads the "274m" distance under the tracked quest's name (top-left, under the minimap).
/// The text has the quest's colour: gold (Archon), purple (Story) or blue (World), so each is tried.
/// No OCR model: the digits are split into blobs and each blob is compared with digits drawn
/// from a few bold Windows fonts, which are close enough to the game's font at this size.
/// </summary>
public static class DistanceReader
{
    // Under the minimap; tall enough for a quest name that wraps onto two lines.
    public static readonly Rect TrackerArea = new(75, 240, 255, 120);

    private const int GlyphW = 10;
    private const int GlyphH = 14;

    private static readonly Lazy<List<(int Digit, float[] Pixels, double Aspect)>> Digits = new(BuildDigitTemplates);

    /// <summary>
    /// The distance in metres, or null if there is no tracked quest distance or a digit is unclear.
    /// </summary>
    public static int? Read(Mat bgr)
    {
        using var area = new Mat(bgr, TrackerArea);
        foreach (var colour in new[] { QuestColour.Gold, QuestColour.Purple, QuestColour.Blue })
        {
            using var mask = colour == QuestColour.Gold ? YellowMask(area) : HueMask(area, colour);
            using var loose = colour == QuestColour.Gold ? LooseYellowMask(area) : HueMask(area, colour, loose: true);
            if (ReadMask(mask, loose) is { } metres)
            {
                return metres;
            }
        }

        return null;
    }

    /// <summary>
    /// Bright coloured text pixels of a purple or blue quest (the text is pale: saturation ~90..180).
    /// Blue sky can pass this too, but it forms big blobs that are dropped; it only hurts if it touches the digits.
    /// </summary>
    public static Mat HueMask(Mat bgr, QuestColour colour, bool loose = false)
    {
        var (lo, hi) = colour == QuestColour.Purple ? (125, 165) : (92, 124);
        using var hsv = bgr.CvtColor(ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(lo, loose ? 40 : 70, loose ? 140 : 190), new Scalar(hi, 255, 255), mask);
        return mask;
    }

    /// <summary>
    /// Gold text including its soft anti-aliased edge. Too blurry to read digits from, but it keeps the
    /// thin top and bottom of an 8 closed, so counting loops works (the tight mask leaves them open).
    /// </summary>
    public static Mat LooseYellowMask(Mat bgr)
    {
        var channels = bgr.Split();
        try
        {
            using var rb = new Mat();
            Cv2.Subtract(channels[2], channels[0], rb);
            using var rHigh = channels[2].GreaterThan(150);
            using var rbHigh = rb.GreaterThan(60);
            var mask = new Mat();
            Cv2.BitwiseAnd(rHigh, rbHigh, mask);
            return mask;
        }
        finally
        {
            foreach (var c in channels)
            {
                c.Dispose();
            }
        }
    }

    private static int? ReadMask(Mat mask, Mat loose)
    {
        var blobs = Blobs(mask);
        if (blobs.Count == 0)
        {
            return null;
        }

        // Group blobs into text lines and look for "<digits>m": tall digits followed by a shorter "m".
        foreach (var line in Lines(blobs))
        {
            var tallest = line.Max(b => b.Height);
            if (tallest is < 8 or > 24)
            {
                continue;
            }

            var digits = new List<Rect>();
            var sawM = false;
            foreach (var b in line)
            {
                if (b.Height >= tallest * 0.85)
                {
                    if (sawM)
                    {
                        digits.Clear();
                        break;
                    }

                    digits.Add(b);
                }
                else if (b.Height >= tallest * 0.5 && b.Bottom >= line.Max(x => x.Bottom) - 2)
                {
                    sawM = true; // lower-case m sits on the baseline (may be split into two blobs)
                }
            }

            if (!sawM || digits.Count is 0 or > 5)
            {
                continue;
            }

            var value = 0;
            foreach (var d in digits)
            {
                using var glyph = new Mat(mask, d);
                using var looseGlyph = new Mat(loose, new Rect(d.X - 1, d.Y - 1, d.Width + 2, d.Height + 2) & new Rect(0, 0, loose.Width, loose.Height));
                var digit = Classify(glyph, Holes(looseGlyph));
                if (digit == null)
                {
                    return null;
                }

                value = value * 10 + digit.Value;
            }

            return value;
        }

        return null;
    }

    /// <summary>Yellow text pixels (about RGB 248,224,72), not the paler underline or the blue sky.</summary>
    public static Mat YellowMask(Mat bgr)
    {
        var channels = bgr.Split();
        try
        {
            using var b = channels[0];
            using var g = channels[1];
            using var r = channels[2];
            using var rHigh = r.GreaterThan(200);
            using var gHigh = g.GreaterThan(170);
            using var bLow = b.LessThan(150);
            using var rb = new Mat();
            Cv2.Subtract(r, b, rb);
            using var rbHigh = rb.GreaterThan(90);
            var mask = new Mat();
            Cv2.BitwiseAnd(rHigh, gHigh, mask);
            Cv2.BitwiseAnd(mask, bLow, mask);
            Cv2.BitwiseAnd(mask, rbHigh, mask);
            return mask;
        }
        finally
        {
            foreach (var c in channels)
            {
                c.Dispose();
            }
        }
    }

    private static List<Rect> Blobs(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var list = new List<Rect>();
        for (var i = 1; i < n; i++)
        {
            var r = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            var area = stats.At<int>(i, 4);
            if (r.Height >= 4 && r.Width >= 2 && area >= 6 && r.Width < 40)
            {
                list.Add(r);
            }
        }

        return list;
    }

    private static IEnumerable<List<Rect>> Lines(List<Rect> blobs)
    {
        var lines = new List<List<Rect>>();
        foreach (var b in blobs.OrderBy(b => b.Bottom))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Bottom - b.Bottom) <= 3);
            if (line == null)
            {
                lines.Add(line = []);
            }

            line.Add(b);
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.X.CompareTo(b.X));
            // A distance line is compact: drop lines with big gaps (e.g. stray pixels in the quest name).
            var compact = line.Zip(line.Skip(1), (a, b) => b.X - a.Right).All(gap => gap <= 6);
            if (compact)
            {
                yield return line;
            }
        }
    }

    // Closed loops per digit (4 is closed in some fonts, open in others).
    private static readonly int[][] HolesByDigit = [[1], [0], [0], [0], [0, 1], [0], [1], [0], [2], [1]];

    private static int? Classify(Mat glyph, int holes)
    {
        var (pixels, aspect) = Normalize(glyph);
        var best = new double[10];
        Array.Fill(best, double.MinValue);
        foreach (var (digit, t, tAspect) in Digits.Value)
        {
            double dot = 0, a = 0, b = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                dot += pixels[i] * t[i];
                a += pixels[i] * pixels[i];
                b += t[i] * t[i];
            }

            var score = dot / Math.Sqrt(a * b + 1e-9) - 0.3 * Math.Abs(aspect - tAspect);
            // Shape alone mixes up 3 / 8 (and 6 / 8 / 9) at this size; the number of loops tells them apart.
            if (!HolesByDigit[digit].Contains(holes))
            {
                score -= 0.15;
            }

            best[digit] = Math.Max(best[digit], score);
        }

        var ranked = best.Select((s, d) => (s, d)).OrderByDescending(x => x.s).ToArray();
        // Unsure (low score or two digits look alike): don't guess.
        if (ranked[0].s < 0.7 || ranked[0].s - ranked[1].s < 0.05)
        {
            return null;
        }

        return ranked[0].d;
    }

    /// <summary>Number of enclosed background regions (loops) in a binary glyph.</summary>
    public static int Holes(Mat binary)
    {
        using var padded = new Mat();
        Cv2.CopyMakeBorder(binary, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(0));
        using var inverted = new Mat();
        Cv2.BitwiseNot(padded, inverted);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(inverted, labels, stats, centroids, PixelConnectivity.Connectivity4);
        // Label 0 is the glyph itself; the outside background touches the padding at (0,0).
        var outside = labels.At<int>(0, 0);
        var holes = 0;
        for (var i = 1; i < n; i++)
        {
            if (i != outside && stats.At<int>(i, 4) >= 2)
            {
                holes++;
            }
        }

        return holes;
    }

    private static (float[] Pixels, double Aspect) Normalize(Mat binary)
    {
        using var nonZero = new Mat();
        Cv2.FindNonZero(binary, nonZero);
        var box = Cv2.BoundingRect(nonZero);
        using var crop = new Mat(binary, box);
        using var small = crop.Resize(new Size(GlyphW, GlyphH), 0, 0, InterpolationFlags.Linear);
        var pixels = new float[GlyphW * GlyphH];
        for (var y = 0; y < GlyphH; y++)
        {
            for (var x = 0; x < GlyphW; x++)
            {
                pixels[y * GlyphW + x] = small.At<byte>(y, x) / 255f;
            }
        }

        return (pixels, box.Width / (double)box.Height);
    }

    private static List<(int, float[], double)> BuildDigitTemplates()
    {
        var list = new List<(int, float[], double)>();
        string[] fonts = ["Segoe UI", "Segoe UI Semibold", "Arial", "Tahoma", "Verdana", "Calibri"];
        foreach (var name in fonts)
        {
            using var font = new Font(name, 30, name.Contains("Semibold") ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
            if (!string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue; // font not installed
            }

            for (var d = 0; d <= 9; d++)
            {
                using var bmp = new Bitmap(48, 48);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(d.ToString(), font, Brushes.White, 4, 2);
                }

                using var mat = new Mat(48, 48, MatType.CV_8UC1);
                for (var y = 0; y < 48; y++)
                {
                    for (var x = 0; x < 48; x++)
                    {
                        mat.Set(y, x, (byte)(bmp.GetPixel(x, y).R > 128 ? 255 : 0));
                    }
                }

                var (pixels, aspect) = Normalize(mat);
                list.Add((d, pixels, aspect));
            }
        }

        return list;
    }
}
