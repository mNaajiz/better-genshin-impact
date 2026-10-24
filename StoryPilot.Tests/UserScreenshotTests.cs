using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;
using Point = OpenCvSharp.Point;

namespace StoryPilot.Tests;

/// <summary>
/// Regression tests on the user's own game screenshots. Those files stay on this PC only
/// (StoryPilot.Tests/LocalData, git-ignored); where they are missing these tests pass without checking anything.
/// </summary>
public class UserScreenshotTests
{
    private static Mat? Local(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "LocalData")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        var path = dir == null ? null : Path.Combine(dir, "LocalData", file);
        return path != null && File.Exists(path) ? Cv2.ImRead(path, ImreadModes.Color) : null;
    }

    [Theory]
    [InlineData("marker_diamond_78m.png", 1022, 326)] // small gold diamond above "78m"
    [InlineData("marker_door_83m.png", 1071, 172)] // gold "go inside" door icon, path sparkles below it
    public void FindsGoldQuestMarker(string file, int x, int y)
    {
        using var frame = Local(file);
        if (frame == null)
        {
            return;
        }

        var hit = QuestMarker.Find(frame);
        Assert.NotNull(hit);
        Assert.True(MapVision.Distance(hit!.Value.Centre, new Point(x, y)) <= 12, $"found at {hit.Value.Centre}");
        Assert.Equal(QuestColour.Gold, hit.Value.Colour);
    }

    [Fact]
    public void ReadsTrackerDistance83()
    {
        using var frame = Local("marker_door_83m.png");
        if (frame == null)
        {
            return;
        }

        Assert.Equal(83, DistanceReader.Read(frame));
    }

    [Fact]
    public void QuestAreaReachedHasNoDistance()
    {
        using var frame = Local("marker_diamond_78m.png");
        if (frame == null)
        {
            return;
        }

        Assert.Null(DistanceReader.Read(frame));
    }
    [Fact]
    public void FollowsTrailUpTheStairs()
    {
        // V trail straight up the stairs ahead (marker not visible, "Higher 60m").
        using var frame = Local("trail_stairs_60m.png");
        if (frame == null)
        {
            return;
        }

        var hit = QuestTrail.Find(frame);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Aim.X, 930, 1000);
        Assert.False(hit.Value.SharpCurve);
        var (dx, walk) = WalkSteering.Steer(hit.Value.Aim, 0);
        Assert.True(walk);
        Assert.InRange(dx, -10, 10);
    }

    [Fact]
    public void CutsTheCornerOfTheTrailTowardTheDoorMarker()
    {
        // The trail starts left of the character and swings right toward the door marker further on.
        // On its own it aims left; blended with the marker the walk goes more directly across the plaza.
        using var frame = Local("marker_door_83m.png");
        if (frame == null)
        {
            return;
        }

        var hit = QuestTrail.Find(frame);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Aim.X, 600, 800);
        Assert.True(WalkSteering.Steer(hit.Value.Aim, 0).MouseDx < 0);

        // Trail (left) and door marker (slightly right) roughly agree: cut the corner, leaning to the marker.
        var marker = QuestMarker.Find(frame);
        Assert.NotNull(marker);
        var trail = Heading.Of(hit.Value.Aim);
        var door = Heading.Of(marker!.Value.Centre);
        var blended = Heading.Blend(trail, door, 83)!.Value;
        Assert.True(blended > (trail + door) / 2 && blended < door, $"trail {trail:0.0}°, marker {door:0.0}°, walk {blended:0.0}°");
    }

    [Theory]
    [InlineData("marker_diamond_78m.png")] // lamps, no trail
    [InlineData("sparkles_behind_274m.png")] // trail sparkles only beside the feet: use the marker
    public void NoTrailAhead(string file)
    {
        using var frame = Local(file);
        if (frame == null)
        {
            return;
        }

        Assert.Null(QuestTrail.Find(frame));
    }
}
