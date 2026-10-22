using System.Diagnostics;
using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;
using Xunit.Abstractions;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Tests;

/// <summary>"Walk to quest marker": marker detection in every quest colour, quest distance in every colour, steering, stuck detection.</summary>
public class WalkTests(ITestOutputHelper output)
{
    /// <summary>OpenCV hues (0..180) of the quest colours.</summary>
    public static TheoryData<int, QuestColour> Colours => new()
    {
        { 24, QuestColour.Gold },
        { 140, QuestColour.Purple },
        { 108, QuestColour.Blue },
    };

    private static Mat Marker(int hue, bool grey = false)
    {
        using var bgr = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", "quest_marker.png"), ImreadModes.Color);
        using var hsv = bgr.CvtColor(ColorConversionCodes.BGR2HSV);
        for (var y = 0; y < hsv.Rows; y++)
        {
            for (var x = 0; x < hsv.Cols; x++)
            {
                var px = hsv.At<Vec3b>(y, x);
                hsv.Set(y, x, new Vec3b((byte)hue, grey ? (byte)0 : px.Item1, px.Item2));
            }
        }

        return hsv.CvtColor(ColorConversionCodes.HSV2BGR);
    }

    private static void PasteMarker(Mat frame, Mat marker, Point centre)
    {
        using var mask = QuestMarker.DiamondMask(marker.Width, marker.Height);
        marker.CopyTo(new Mat(frame, new Rect(centre.X - marker.Width / 2, centre.Y - marker.Height / 2, marker.Width, marker.Height)), mask);
    }

    /// <summary>A game-ish frame: blue sky gradient on top, noisy green-brown ground below.</summary>
    private static Mat World()
    {
        var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(frame, new Scalar(40, 90, 70), new Scalar(90, 150, 120));
        for (var y = 0; y < 400; y++)
        {
            frame[new Rect(0, y, Vision.Width, 1)].SetTo(new Scalar(235 - y / 8, 190 - y / 10, 120 - y / 10));
        }

        return frame;
    }

    [Theory]
    [MemberData(nameof(Colours))]
    public void FindsMarkerInEachQuestColour(int hue, QuestColour expected)
    {
        using var marker = Marker(hue);
        using var vision = new QuestMarker();
        foreach (var at in new Point[] { new(1100, 620), new(800, 200), new(1880, 700) })
        {
            using var frame = World();
            PasteMarker(frame, marker, at);
            var sw = Stopwatch.StartNew();
            var hit = vision.Find(frame);
            output.WriteLine($"{expected} at {at}: {hit} in {sw.ElapsedMilliseconds} ms");
            Assert.NotNull(hit);
            Assert.True(MapVision.Distance(hit!.Value.Centre, at) <= 2, $"found at {hit.Value.Centre}");
            Assert.Equal(expected, hit.Value.Colour);
        }
    }

    [Fact]
    public void NoMarkerMeansNothingFound()
    {
        using var vision = new QuestMarker();
        using var frame = World();
        Assert.Null(vision.Find(frame));

        // Same shape without colour (white/grey) is not a quest marker.
        using var grey = Marker(0, grey: true);
        PasteMarker(frame, grey, new Point(1000, 600));
        Assert.Null(vision.Find(frame));
    }

    [Fact]
    public void IgnoresQuestIconOnMinimap()
    {
        using var vision = new QuestMarker();
        using var frame = World();
        using var marker = Marker(24);
        PasteMarker(frame, marker, new Point(150, 160));
        Assert.Null(vision.Find(frame));
    }

    [Theory]
    [InlineData(140)]
    [InlineData(108)]
    public void ReadsQuestDistanceInPurpleAndBlue(int hue)
    {
        var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3, new Scalar(200, 180, 160));
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Data", "tracker_274m.png"), ImreadModes.Color);
        using var yellow = DistanceReader.YellowMask(crop);
        using var hsv = crop.CvtColor(ColorConversionCodes.BGR2HSV);
        for (var y = 0; y < hsv.Rows; y++)
        {
            for (var x = 0; x < hsv.Cols; x++)
            {
                // Recolour the text (and its anti-aliased rim) from gold to the quest colour.
                var px = hsv.At<Vec3b>(y, x);
                if (yellow.At<byte>(y, x) != 0 || (px.Item0 is >= 15 and <= 35 && px.Item1 > 90))
                {
                    hsv.Set(y, x, new Vec3b((byte)hue, px.Item1, px.Item2));
                }
            }
        }

        using var recoloured = hsv.CvtColor(ColorConversionCodes.HSV2BGR);
        recoloured.CopyTo(new Mat(frame, new Rect(0, 200, crop.Width, crop.Height)));
        Assert.Equal(274, DistanceReader.Read(frame));
        frame.Dispose();
    }

    [Fact]
    public void SteersTowardMarker()
    {
        // Straight ahead (above the centre): walk, tiny correction.
        var (dx, walk) = WalkSteering.Steer(new Point(965, 300));
        Assert.True(walk);
        Assert.InRange(dx, 0, 10);

        // Ahead but well to the right: turn right first, don't walk yet.
        (dx, walk) = WalkSteering.Steer(new Point(1500, 300));
        Assert.False(walk);
        Assert.True(dx > 50);

        // Ahead-left, slightly: walk while turning left.
        (dx, walk) = WalkSteering.Steer(new Point(860, 200));
        Assert.True(walk);
        Assert.True(dx < 0);

        // Behind: turn hard toward its side, no walking.
        (dx, walk) = WalkSteering.Steer(new Point(700, 900));
        Assert.False(walk);
        Assert.True(dx <= -100);
    }

    [Fact]
    public void DetectsStuckOnlyWhenDistanceStopsFalling()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var stuck = new StuckDetector(TimeSpan.FromSeconds(2));
        var metres = 120;
        for (var ms = 0; ms <= 5000; ms += 150)
        {
            Assert.False(stuck.Update(metres, t0.AddMilliseconds(ms)));
            metres -= ms % 300 == 0 ? 1 : 0; // ~3 m/s
        }

        // Same reading for 2 s: stuck.
        var t1 = t0.AddSeconds(6);
        Assert.False(stuck.Update(--metres, t1));
        Assert.False(stuck.Update(metres, t1.AddMilliseconds(1900)));
        Assert.True(stuck.Update(metres + 1, t1.AddMilliseconds(2100)));

        // After a recovery move there's a fresh window, and progress clears it.
        stuck.Restart(t1.AddMilliseconds(2200));
        Assert.False(stuck.Update(metres, t1.AddMilliseconds(3000)));
        Assert.False(stuck.Update(metres - 3, t1.AddMilliseconds(3300)));

        // Unreadable distance is never "stuck".
        Assert.False(stuck.Update(null, t1.AddSeconds(10)));
    }
}
