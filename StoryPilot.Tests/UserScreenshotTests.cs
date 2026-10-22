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
}
