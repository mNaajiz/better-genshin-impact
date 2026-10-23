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

    private static Scalar Hsv(int h, int s, int v)
    {
        using var px = new Mat(1, 1, MatType.CV_8UC3, new Scalar(h, s, v));
        using var bgr = px.CvtColor(ColorConversionCodes.HSV2BGR);
        var c = bgr.At<Vec3b>(0, 0);
        return new Scalar(c.Item0, c.Item1, c.Item2);
    }

    private static void Diamond(Mat frame, Point centre, int radius, Scalar colour, int thickness = -1)
    {
        Point[] pts = [new(centre.X, centre.Y - radius), new(centre.X + radius, centre.Y), new(centre.X, centre.Y + radius), new(centre.X - radius, centre.Y)];
        if (thickness < 0)
        {
            Cv2.FillConvexPoly(frame, pts, colour);
        }
        else
        {
            Cv2.Polylines(frame, [pts], true, colour, thickness);
        }
    }

    /// <summary>
    /// A game-ish frame: blue sky gradient on top, noisy ground below, pale yellow lamps and path sparkles
    /// (saturation ~130..150, like the real ones), and the tracked quest's icon under the minimap in <paramref name="hue"/>.
    /// </summary>
    private static Mat World(int? hue)
    {
        var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(frame, new Scalar(40, 90, 70), new Scalar(90, 150, 120));
        for (var y = 0; y < 400; y++)
        {
            frame[new Rect(0, y, Vision.Width, 1)].SetTo(new Scalar(235 - y / 8, 190 - y / 10, 120 - y / 10));
        }

        foreach (var lamp in new Point[] { new(650, 160), new(1383, 170), new(820, 197) })
        {
            Cv2.Circle(frame, lamp, 14, Hsv(17, 126, 238), -1);
        }

        for (var i = 0; i < 25; i++)
        {
            Cv2.Circle(frame, new Point(500 + i * 20, 750 - i * 18), 4, Hsv(28, 150, 230), -1);
        }

        if (hue is { } h)
        {
            Diamond(frame, new Point(58, 264), 14, Hsv(h, 240, 240));
        }

        return frame;
    }

    [Theory]
    [MemberData(nameof(Colours))]
    public void FindsMarkerInEachQuestColour(int hue, QuestColour expected)
    {
        foreach (var at in new Point[] { new(1100, 620), new(800, 200), new(1880, 700) })
        {
            // Small outlined diamond (like the real one) and a bigger filled icon.
            foreach (var (radius, thickness) in new[] { (9, 2), (20, -1) })
            {
                using var frame = World(hue);
                Diamond(frame, at, radius, Hsv(hue, 252, 250), thickness);
                var sw = Stopwatch.StartNew();
                var hit = QuestMarker.Find(frame);
                output.WriteLine($"{expected} r{radius} at {at}: {hit} in {sw.ElapsedMilliseconds} ms");
                Assert.NotNull(hit);
                Assert.True(MapVision.Distance(hit!.Value.Centre, at) <= 3, $"found at {hit.Value.Centre}");
                Assert.Equal(expected, hit.Value.Colour);
            }
        }
    }

    [Fact]
    public void IgnoresMarkersOfOtherColoursWhenQuestColourIsKnown()
    {
        // The tracked quest is purple; a gold diamond elsewhere (another quest) is not the target.
        using var frame = World(140);
        Diamond(frame, new Point(900, 500), 18, Hsv(24, 252, 250));
        Assert.Null(QuestMarker.Find(frame));
        Diamond(frame, new Point(1200, 450), 12, Hsv(140, 252, 250), 2);
        var hit = QuestMarker.Find(frame);
        Assert.NotNull(hit);
        Assert.True(MapVision.Distance(hit!.Value.Centre, new Point(1200, 450)) <= 3);
    }

    [Fact]
    public void NoMarkerMeansNothingFound()
    {
        // Lamps, sparkles and sky only.
        using var frame = World(24);
        Assert.Null(QuestMarker.Find(frame));

        // The same shape without colour (white) is not a quest marker.
        Diamond(frame, new Point(1000, 600), 15, new Scalar(250, 250, 250));
        Assert.Null(QuestMarker.Find(frame));
    }

    [Fact]
    public void IgnoresQuestIconsInTheHud()
    {
        using var frame = World(24);
        Diamond(frame, new Point(216, 200), 12, Hsv(24, 245, 245)); // on the minimap
        Diamond(frame, new Point(1700, 300), 12, Hsv(24, 245, 245)); // next to the party list
        Assert.Null(QuestMarker.Find(frame));
    }

    [Fact]
    public void ReadsTrackedQuestColourFromTrackerIcon()
    {
        foreach (var hue in new[] { 24, 140, 108 })
        {
            using var frame = World(hue);
            using var hsv = frame.CvtColor(ColorConversionCodes.BGR2HSV);
            Assert.InRange(QuestMarker.TrackedQuestHue(hsv) ?? -1, hue - 2, hue + 2);
        }

        using var none = World(null);
        using var noneHsv = none.CvtColor(ColorConversionCodes.BGR2HSV);
        Assert.Null(QuestMarker.TrackedQuestHue(noneHsv));
    }

    [Theory]
    [InlineData(140)]
    [InlineData(108)]
    public void ReadsQuestDistanceInPurpleAndBlue(int hue)
    {
        using var frame = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3, new Scalar(200, 180, 160));
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
    }

    [Fact]
    public void CountsLoopsInDigits()
    {
        Mat Glyph(string text)
        {
            var m = new Mat(40, 30, MatType.CV_8UC1, Scalar.All(0));
            Cv2.PutText(m, text, new Point(3, 32), HersheyFonts.HersheySimplex, 1.1, Scalar.All(255), 3);
            return m;
        }

        using var eight = Glyph("8");
        using var three = Glyph("3");
        using var zero = Glyph("0");
        Assert.Equal(2, DistanceReader.Holes(eight));
        Assert.Equal(0, DistanceReader.Holes(three));
        Assert.Equal(1, DistanceReader.Holes(zero));
    }

    [Fact]
    public void SteersTowardMarker()
    {
        // Straight ahead: walk, no turning.
        var (dx, walk) = WalkSteering.Steer(new Point(970, 420), 0);
        Assert.True(walk);
        Assert.Equal(0, dx);

        // Ahead but well to the right: turn right, don't walk yet.
        (dx, walk) = WalkSteering.Steer(new Point(1500, 400), 0);
        Assert.False(walk);
        Assert.Equal(108, dx);

        // A little to the left: walk while turning left.
        (dx, walk) = WalkSteering.Steer(new Point(860, 300), 0);
        Assert.True(walk);
        Assert.Equal(-20, dx);

        // Pinned to the bottom (behind): turn hard toward its side, no walking.
        (dx, walk) = WalkSteering.Steer(new Point(700, 900), 0);
        Assert.False(walk);
        Assert.Equal(-300, dx);

        // Far left edge: a big but bounded turn.
        Assert.Equal(-192, WalkSteering.Steer(new Point(0, 400), 0).MouseDx);
    }

    [Fact]
    public void SteeringSettlesAfterOvershoot()
    {
        // Simulated camera where 1 px of mouse moves the marker ~3 px on screen (high sensitivity), so a
        // full-gain turn overshoots. Damping must still bring it inside the dead zone within a few steps.
        var x = 1500.0;
        var previous = 0;
        var steps = 0;
        while (Math.Abs(x - 960) > WalkSteering.DeadZone && steps < 20)
        {
            var (dx, _) = WalkSteering.Steer(new Point((int)x, 400), previous);
            x -= dx * 3.0;
            previous = dx;
            steps++;
        }

        Assert.True(steps < 20, $"still swinging at x={x}");
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
    /// <summary>Golden V-trail sparkles (pure yellow at full brightness) at the given points.</summary>
    private static void Trail(Mat frame, IEnumerable<Point> points)
    {
        foreach (var p in points)
        {
            Cv2.Circle(frame, p, 3, Hsv(30, 165, 255), -1);
            Cv2.Line(frame, new Point(p.X - 7, p.Y), new Point(p.X + 7, p.Y), Hsv(30, 120, 255), 1);
        }
    }

    private static IEnumerable<Point> Line(Point from, Point to, int count) =>
        Enumerable.Range(0, count).Select(i => new Point(from.X + (to.X - from.X) * i / (count - 1), from.Y + (to.Y - from.Y) * i / (count - 1)));

    [Fact]
    public void FindsStraightTrailAhead()
    {
        using var frame = World(24);
        Trail(frame, Line(new Point(965, 780), new Point(990, 220), 14));
        var sw = Stopwatch.StartNew();
        var hit = QuestTrail.Find(frame);
        output.WriteLine($"{hit} in {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Aim.X, 960, 995);
        Assert.InRange(hit.Value.Aim.Y, QuestTrail.LookAheadY - 70, QuestTrail.LookAheadY + 70);
        Assert.False(hit.Value.SharpCurve);
        Assert.True(WalkSteering.Steer(hit.Value.Aim, 0).Walk);
    }

    [Fact]
    public void AimsAtTrailOffToTheSide()
    {
        // Trail starts left of the character and leads up and to the left: turn left.
        using var frame = World(24);
        Trail(frame, Line(new Point(600, 790), new Point(300, 250), 14));
        var hit = QuestTrail.Find(frame);
        Assert.NotNull(hit);
        Assert.True(hit!.Value.Aim.X < 600, $"aim {hit.Value.Aim}");
        Assert.True(WalkSteering.Steer(hit.Value.Aim, 0).MouseDx < 0);

        using var right = World(24);
        Trail(right, Line(new Point(1100, 790), new Point(1450, 260), 14));
        var r = QuestTrail.Find(right);
        Assert.NotNull(r);
        Assert.True(WalkSteering.Steer(r!.Value.Aim, 0).MouseDx > 0);
    }

    [Fact]
    public void SharpTrailBendIsNoticed()
    {
        // Straight ahead, then a hard turn to the right.
        using var frame = World(24);
        Trail(frame, Line(new Point(960, 790), new Point(960, 470), 9).Concat(Line(new Point(1010, 450), new Point(1500, 330), 9)));
        var hit = QuestTrail.Find(frame);
        Assert.NotNull(hit);
        Assert.True(hit!.Value.SharpCurve);
    }

    [Fact]
    public void NoTrailFromLampsSparklesOrTrailBehind()
    {
        // Pale path sparkles and lamps only.
        using var frame = World(24);
        Assert.Null(QuestTrail.Find(frame));

        // A couple of stray yellow sparks are not a trail.
        Trail(frame, [new Point(700, 500), new Point(1200, 600)]);
        Assert.Null(QuestTrail.Find(frame));

        // A trail beside / behind the feet (bottom of the screen) isn't ahead: leave it to the marker.
        using var behind = World(24);
        Trail(behind, Line(new Point(100, 940), new Point(500, 860), 8));
        Assert.Null(QuestTrail.Find(behind));

        // Sparkles in a row at one height (window sills) don't lead anywhere.
        using var row = World(24);
        Trail(row, Line(new Point(600, 500), new Point(1300, 520), 10));
        Assert.Null(QuestTrail.Find(row));
    }

    [Fact]
    public void TrailWinsOverMarkerButMarkerIsTheFallback()
    {
        // With a trail, the walker aims along it even if the marker is elsewhere; without one, at the marker.
        using var frame = World(24);
        Diamond(frame, new Point(1500, 300), 12, Hsv(24, 252, 250), 2);
        Assert.Null(QuestTrail.Find(frame));
        Assert.NotNull(QuestMarker.Find(frame));
        Trail(frame, Line(new Point(900, 790), new Point(700, 250), 14));
        var hit = QuestTrail.Find(frame);
        Assert.NotNull(hit);
        Assert.True(hit!.Value.Aim.X < 960);
    }

    [Fact]
    public void SprintsOnlyWhenStraightAndFarEnough()
    {
        Assert.True(QuestWalker.SprintAllowed(0, false, 80));
        Assert.True(QuestWalker.SprintAllowed(-20, false, null));
        Assert.False(QuestWalker.SprintAllowed(60, false, 80)); // tight turn
        Assert.False(QuestWalker.SprintAllowed(0, true, 80)); // trail bends sharply
        Assert.False(QuestWalker.SprintAllowed(0, false, 15)); // nearly there
        Assert.False(QuestWalker.SprintAllowed(0, false, 9));
    }

    [Fact]
    public void SprintsInRandomBursts()
    {
        var bursts = new SprintBursts(new Random(7));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var runs = new List<double>();
        var rests = new List<double>();
        var state = false;
        var since = t0;
        for (var ms = 0; ms <= 120_000; ms += 100)
        {
            var now = t0.AddMilliseconds(ms);
            var on = bursts.Update(now, true);
            if (on != state)
            {
                (state ? runs : rests).Add((now - since).TotalSeconds);
                state = on;
                since = now;
            }
        }

        output.WriteLine($"runs {string.Join(", ", runs.Select(r => r.ToString("0.0")))}");
        output.WriteLine($"rests {string.Join(", ", rests.Skip(1).Select(r => r.ToString("0.0")))}");
        Assert.True(runs.Count >= 10);
        Assert.All(runs, r => Assert.InRange(r, 0.9, 4.2));
        Assert.All(rests.Skip(1), r => Assert.InRange(r, 1.9, 6.2));
        Assert.True(runs.Max() - runs.Min() > 1, "burst lengths should vary");

        // Not allowed (turning, near the end): never sprints, and a burst in progress ends at once.
        var t1 = t0.AddMinutes(10);
        while (!bursts.Update(t1, true))
        {
            t1 = t1.AddMilliseconds(100);
        }

        Assert.False(bursts.Update(t1.AddMilliseconds(100), false));
        Assert.False(bursts.Update(t1.AddMilliseconds(200), true)); // resting after the cut-off burst
    }
}
