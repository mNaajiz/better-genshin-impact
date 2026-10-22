using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

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

public readonly record struct MarkerHit(Point Centre, QuestColour Colour, int Pixels);

/// <summary>
/// Finds the tracked quest's marker in the game world. Its icon changes (a small diamond, a door for
/// "go inside", …) but it is always drawn in the quest's own vivid colour, the same colour as the quest
/// icon next to the quest name. So: read that colour from the tracker icon, then look for a compact
/// patch of exactly that vivid colour outside the HUD. Lamps, sunlight and the V-key path sparkles are
/// paler (saturation ~120..175) and are ignored.
/// </summary>
public static class QuestMarker
{
    /// <summary>The quest icon left of the quest name, under the minimap.</summary>
    public static readonly Rect TrackerIcon = new(30, 235, 60, 60);

    // HUD parts (1080p): minimap + quest tracker, top-right menu icons, party list, bottom bar / skills.
    private static readonly Rect[] UiAreas =
    [
        new(0, 0, 560, 330),
        new(1260, 0, 660, 130),
        new(1560, 220, 360, 400),
        new(1640, 760, 280, 320),
        new(0, 950, 1920, 130),
    ];

    private const int MinSaturation = 200;
    private const int MinValue = 185;

    private static readonly (int Lo, int Hi)[] AllQuestHues = [(14, 32), (125, 160), (95, 120)];

    public static MarkerHit? Find(Mat bgr)
    {
        using var hsv = bgr.CvtColor(ColorConversionCodes.BGR2HSV);
        var hue = TrackedQuestHue(hsv);
        (int Lo, int Hi)[] ranges = hue is { } h ? [(h - 6, h + 6)] : AllQuestHues;

        MarkerHit? best = null;
        foreach (var (lo, hi) in ranges)
        {
            using var mask = new Mat();
            Cv2.InRange(hsv, new Scalar(Math.Max(0, lo), MinSaturation, MinValue), new Scalar(Math.Min(180, hi), 255, 255), mask);
            foreach (var ui in UiAreas)
            {
                mask[ui & new Rect(0, 0, mask.Width, mask.Height)].SetTo(Scalar.All(0));
            }

            // Join the outline pieces of one icon into one blob.
            using var joined = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Dilate(mask, joined, kernel);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var n = Cv2.ConnectedComponentsWithStats(joined, labels, stats, centroids, PixelConnectivity.Connectivity8);
            for (var i = 1; i < n; i++)
            {
                var box = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
                if (box.Width is < 12 or > 90 || box.Height is < 12 or > 90 || box.Width > box.Height * 2.5 || box.Height > box.Width * 2.5)
                {
                    continue;
                }

                using var inBox = new Mat(mask, box);
                var pixels = Cv2.CountNonZero(inBox);
                if (pixels < 30 || (best is { } b && b.Pixels >= pixels))
                {
                    continue;
                }

                var centre = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
                best = new MarkerHit(centre, Classify((lo + hi) / 2), pixels);
            }
        }

        return best;
    }

    /// <summary>Hue of the tracked quest's icon, or null if no quest is tracked (or it can't be read).</summary>
    public static int? TrackedQuestHue(Mat hsv)
    {
        using var icon = new Mat(hsv, TrackerIcon);
        using var mask = new Mat();
        Cv2.InRange(icon, new Scalar(0, MinSaturation, MinValue), new Scalar(180, 255, 255), mask);
        var hues = new List<int>();
        for (var y = 0; y < icon.Rows; y++)
        {
            for (var x = 0; x < icon.Cols; x++)
            {
                if (mask.At<byte>(y, x) != 0)
                {
                    hues.Add(icon.At<Vec3b>(y, x).Item0);
                }
            }
        }

        if (hues.Count < 40)
        {
            return null;
        }

        hues.Sort();
        return hues[hues.Count / 2];
    }

    /// <summary>OpenCV hue (0..180) to quest colour.</summary>
    public static QuestColour Classify(int hue) => hue switch
    {
        >= 12 and <= 35 => QuestColour.Gold,
        >= 90 and < 125 => QuestColour.Blue,
        >= 125 and <= 165 => QuestColour.Purple,
        _ => QuestColour.Other,
    };
}
