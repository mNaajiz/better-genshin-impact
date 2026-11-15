using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Tests;

/// <summary>
/// Uses two real English-client screenshots (UID blanked): the quest tracker showing "274m",
/// and the Snezhnaya map opened with M.
/// </summary>
public class TeleportTests
{
    private static Mat Data(string file) => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Data", file), ImreadModes.Color);

    /// <summary>The tracker crop (x 0..480, y 200..360) placed back into a 1080p frame.</summary>
    private static Mat TrackerFrame(Action<Mat>? edit = null)
    {
        var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3, new Scalar(200, 180, 160));
        using var crop = Data("tracker_274m.png");
        edit?.Invoke(crop);
        crop.CopyTo(new Mat(frame, new Rect(0, 200, crop.Width, crop.Height)));
        return frame;
    }

    [Fact]
    public void ReadsQuestDistanceFromTracker()
    {
        using var frame = TrackerFrame();
        Assert.Equal(274, DistanceReader.Read(frame));
    }

    [Fact]
    public void ReadsRearrangedDigits()
    {
        // Glyphs in the crop (crop coordinates): "2" x84..91, "7" x93..101, "4" x103..112, rows 83..95.
        using var frame = TrackerFrame(crop =>
        {
            using var original = crop.Clone();
            var two = new Rect(84, 83, 9, 13);
            var seven = new Rect(93, 83, 9, 13);
            var four = new Rect(103, 83, 10, 13);
            // Write "472": 4 at 84, 7 at 95, 2 at 105 (blank the area first).
            crop[new Rect(84, 83, 31, 13)].SetTo(new Scalar(248, 216, 184));
            new Mat(original, four).CopyTo(new Mat(crop, new Rect(84, 83, 10, 13)));
            new Mat(original, seven).CopyTo(new Mat(crop, new Rect(95, 83, 9, 13)));
            new Mat(original, two).CopyTo(new Mat(crop, new Rect(105, 83, 9, 13)));
        });
        Assert.Equal(472, DistanceReader.Read(frame));
    }

    [Fact]
    public void NoTrackedQuestMeansNoDistance()
    {
        using var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3, new Scalar(200, 180, 160));
        Assert.Null(DistanceReader.Read(frame));
    }

    [Fact]
    public void FindsUnlockedWaypointsAndPlayerOnRealMap()
    {
        using var map = Data("map_snezhnaya.png");
        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var vision = new MapVision();
        Assert.True(vision.IsMapOpen(grey));

        var waypoints = vision.FindUnlockedWaypoints(map, grey);
        // Unlocked (blue diamond) waypoints visible in the screenshot.
        foreach (var expected in new Point[] { new(983, 372), new(849, 438), new(922, 667), new(1392, 781) })
        {
            Assert.Contains(waypoints, w => MapVision.Distance(w, expected) < 6);
        }

        // Grey (locked) waypoints must be ignored.
        foreach (var locked in new Point[] { new(693, 330), new(267, 904), new(786, 376) })
        {
            Assert.DoesNotContain(waypoints, w => MapVision.Distance(w, locked) < 6);
        }

        var player = MapVision.FindPlayer(map);
        Assert.NotNull(player);
        Assert.True(MapVision.Distance(player!.Value, new Point(961, 538)) < 6);
    }

    [Fact]
    public void ChoosesWaypointNearestQuestOnRealMap()
    {
        using var map = Data("map_snezhnaya.png");
        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var vision = new MapVision();
        var waypoints = vision.FindUnlockedWaypoints(map, grey);
        var player = MapVision.FindPlayer(map);

        // The gold quest marker in this screenshot is at about (831, 411); the waypoint beside it is (849, 438).
        var choice = MapVision.ChooseWaypoint(waypoints, new Point(831, 411), player);
        Assert.NotNull(choice);
        Assert.True(MapVision.Distance(choice!.Value, new Point(849, 438)) < 6);

        // A "quest" right next to the player: no waypoint is meaningfully closer, so don't teleport.
        Assert.Null(MapVision.ChooseWaypoint(waypoints, new Point(965, 545), player));
    }

    [Fact]
    public void WaypointSelectionOnSyntheticMap()
    {
        using var map = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(map, new Scalar(150, 120, 90), new Scalar(200, 170, 140));
        using var pin = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", "waypoint_on_map.png"), ImreadModes.Unchanged);

        void Paste(Point centre, bool locked)
        {
            using var bgr = pin.CvtColor(ColorConversionCodes.BGRA2BGR);
            if (locked)
            {
                using var g = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(g, bgr, ColorConversionCodes.GRAY2BGR);
            }

            using var alpha = pin.ExtractChannel(3);
            using var mask = alpha.GreaterThan(128);
            var roi = new Rect(centre.X - pin.Width / 2, centre.Y - pin.Height / 2, pin.Width, pin.Height);
            bgr.CopyTo(new Mat(map, roi), mask);
        }

        Paste(new Point(1100, 600), locked: true);   // closest to the quest, but locked
        Paste(new Point(1250, 650), locked: false);  // nearest unlocked
        Paste(new Point(600, 300), locked: false);
        Paste(new Point(200, 300), locked: false);   // under the reward list UI: ignored

        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var vision = new MapVision();
        var waypoints = vision.FindUnlockedWaypoints(map, grey);
        Assert.True(waypoints.Count == 2, string.Join(" ", waypoints.Select(w => $"({w.X},{w.Y})")));

        var quest = new Point(1080, 580);
        var choice = MapVision.ChooseWaypoint(waypoints, quest, player: null);
        Assert.NotNull(choice);
        Assert.True(MapVision.Distance(choice!.Value, new Point(1250, 650)) < 4);

        // Player already closer to the quest than any waypoint: stay put.
        Assert.Null(MapVision.ChooseWaypoint(waypoints, quest, player: new Point(1150, 600)));
    }

    [Fact]
    public void FindsQuestDiamondOnRealMapAndNoEdgeButton()
    {
        using var map = Data("map_snezhnaya.png");
        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);

        // The quest is on screen here, so the map shows no quest button at its edge.
        Assert.Null(MapVision.FindQuestButton(map, grey));
        var quest = MapVision.FindQuestOnMap(map, grey, MapVision.Centre);
        Assert.NotNull(quest);
        Assert.True(MapVision.Distance(quest!.Value, new Point(831, 411)) < 6, $"found at {quest}");
    }

    [Fact]
    public void EdgeButtonIsADiamondOnADarkDisc()
    {
        using var map = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(map, new Scalar(150, 120, 90), new Scalar(200, 170, 140));

        void Diamond(Point c, int r)
        {
            var outline = new[] { new Point(c.X, c.Y - r), new Point(c.X + r, c.Y), new Point(c.X, c.Y + r), new Point(c.X - r, c.Y) };
            Cv2.Polylines(map, new[] { outline }, true, new Scalar(10, 181, 240), 3);
            var inner = new[] { new Point(c.X, c.Y - r / 2), new Point(c.X + r / 2, c.Y), new Point(c.X, c.Y + r / 2), new Point(c.X - r / 2, c.Y) };
            Cv2.FillPoly(map, new[] { inner }, new Scalar(0, 233, 255));
        }

        Cv2.Circle(map, new Point(1775, 960), 34, new Scalar(40, 35, 30), -1);
        Diamond(new Point(1775, 960), 19); // edge button
        Diamond(new Point(700, 400), 20);  // a quest diamond on the map itself

        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);
        var button = MapVision.FindQuestButton(map, grey);
        Assert.NotNull(button);
        Assert.True(MapVision.Distance(button!.Value, new Point(1775, 960)) < 4, $"button at {button}");

        var quest = MapVision.FindQuestOnMap(map, grey, MapVision.Centre);
        Assert.NotNull(quest);
        Assert.True(MapVision.Distance(quest!.Value, new Point(700, 400)) < 4, $"quest at {quest}");
    }

    [Fact]
    public void FindsStatueAsTeleportPointAndRanksByQuestDistance()
    {
        using var map = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(map, new Scalar(150, 120, 90), new Scalar(200, 170, 140));
        using var statue = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", "statue_on_map.png"), ImreadModes.Unchanged);
        using var bgr = statue.CvtColor(ColorConversionCodes.BGRA2BGR);
        using var alpha = statue.ExtractChannel(3);
        using var mask = alpha.GreaterThan(128);
        bgr.CopyTo(new Mat(map, new Rect(900 - statue.Width / 2, 700 - statue.Height / 2, statue.Width, statue.Height)), mask);

        using var grey = map.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var vision = new MapVision();
        var points = vision.FindTeleportPoints(map, grey);
        Assert.Contains(points, p => MapVision.Distance(p, new Point(900, 700)) < 6);

        var extra = new List<Point>(points) { new(1500, 300) };
        var ranked = MapVision.RankTeleportPoints(extra, new Point(960, 650), player: null);
        Assert.True(MapVision.Distance(ranked[0], new Point(900, 700)) < 6);
        Assert.Empty(MapVision.RankTeleportPoints(extra, new Point(960, 650), player: new Point(950, 660)));
    }

    [Fact]
    public void NarrationNeedsBlackScreenWithCentredText()
    {
        using var narration = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1, Scalar.All(0));
        narration[new Rect(660, 520, 600, 36)].SetTo(Scalar.All(255));
        Assert.NotNull(Vision.IsNarrationScreen(narration));

        // Dark cave / night: dark but not pure black, no text.
        using var cave = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1);
        Cv2.Randu(cave, Scalar.All(2), Scalar.All(30));
        Assert.Null(Vision.IsNarrationScreen(cave));

        // Fully black (loading / fade): no text, nothing to click.
        using var black = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1, Scalar.All(0));
        Assert.Null(Vision.IsNarrationScreen(black));

        // Black with something bright off to the side only (e.g. a UI element), not centred text.
        using var side = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1, Scalar.All(0));
        side[new Rect(1700, 500, 150, 60)].SetTo(Scalar.All(255));
        Assert.Null(Vision.IsNarrationScreen(side));
    }
}
