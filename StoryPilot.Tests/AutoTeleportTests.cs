using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Tests;

/// <summary>
/// "Auto-teleport when I pick a waypoint" on synthetic big-map frames built from the asset templates.
/// </summary>
public class AutoTeleportTests
{
    private static Mat Load(string file) => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", file), ImreadModes.Color);

    private static void Paste(Mat frame, string asset, int x, int y)
    {
        using var image = Load(asset);
        image.CopyTo(new Mat(frame, new Rect(x, y, image.Width, image.Height)));
    }

    /// <summary>A noisy frame; with <paramref name="map"/> it has the big map's zoom slider button.</summary>
    private static Mat Frame(bool map = true)
    {
        var m = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(m, new Scalar(30, 30, 30), new Scalar(90, 90, 90));
        if (map)
        {
            Paste(m, "map_scale_button.png", 38, 520);
        }

        return m;
    }

    /// <summary>A pick-list row: icon in the list column, white label to its right.</summary>
    private static void ListRow(Mat frame, string icon, int y, bool label = true)
    {
        Paste(frame, icon, 1282, y);
        if (label)
        {
            frame[new Rect(1340, y + 12, 140, 12)].SetTo(Scalar.All(255));
        }
    }

    private static WaypointAutoTeleport.Step? Decide(Mat frame)
    {
        using var vision = new WaypointAutoTeleport();
        using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        return vision.Decide(frame, grey);
    }

    [Fact]
    public void PressesTeleportWhenPanelShowsIt()
    {
        using var frame = Frame();
        Paste(frame, "teleport_button.png", 1470, 1010);
        var step = Decide(frame);
        Assert.NotNull(step);
        Assert.Equal(WaypointAutoTeleport.Kind.ClickTeleport, step!.Value.Kind);
        Assert.InRange(step.Value.At.X, 1488, 1493);
        Assert.InRange(step.Value.At.Y, 1029, 1033);
    }

    [Fact]
    public void IgnoresTeleportButtonOutsideTheMap()
    {
        using var frame = Frame(map: false);
        Paste(frame, "teleport_button.png", 1470, 1010);
        Assert.Null(Decide(frame));
    }

    [Fact]
    public void DoesNothingOnMapWithNothingSelected()
    {
        using var frame = Frame();
        Paste(frame, "map_close_button.png", 1830, 30);
        // Even a list-like row is ignored while the map's own close button shows (no panel/list open).
        ListRow(frame, "waypoint_in_list.png", 400);
        Assert.Null(Decide(frame));

        using var plain = Frame();
        Assert.Null(Decide(plain));
    }

    [Fact]
    public void PicksWaypointRowWhenSeveralPointsOverlap()
    {
        using var frame = Frame();
        ListRow(frame, "list_domain.png", 300);
        ListRow(frame, "waypoint_in_list.png", 400);
        ListRow(frame, "list_statue.png", 500);
        var step = Decide(frame);
        Assert.NotNull(step);
        Assert.Equal(WaypointAutoTeleport.Kind.ClickListRow, step!.Value.Kind);
        Assert.Equal("waypoint", step.Value.What);
        // Middle of the label area to the right of the waypoint icon (icon 30x43 at 1282,400).
        Assert.InRange(step.Value.At.X, 1408, 1416);
        Assert.InRange(step.Value.At.Y, 418, 424);
    }

    [Fact]
    public void PicksStatueOrDomainWhenNoWaypointInList()
    {
        using var statue = Frame();
        ListRow(statue, "list_statue.png", 350);
        Assert.Equal("Statue of The Seven", Decide(statue)?.What);

        using var domain = Frame();
        ListRow(domain, "list_domain.png", 600);
        var step = Decide(domain);
        Assert.Equal(WaypointAutoTeleport.Kind.ClickListRow, step?.Kind);
        Assert.Equal("domain", step?.What);
    }

    [Fact]
    public void ListIconWithoutLabelIsNotARow()
    {
        using var frame = Frame();
        ListRow(frame, "waypoint_in_list.png", 400, label: false);
        Assert.Null(Decide(frame));
    }

    [Fact]
    public void WhiteTextCheck()
    {
        using var frame = Frame();
        var area = new Rect(100, 100, 200, 40);
        Assert.False(WaypointAutoTeleport.HasWhiteText(frame, area));
        frame[new Rect(120, 110, 60, 8)].SetTo(new Scalar(40, 200, 255)); // bright but coloured
        Assert.False(WaypointAutoTeleport.HasWhiteText(frame, area));
        frame[new Rect(120, 120, 60, 8)].SetTo(Scalar.All(250));
        Assert.True(WaypointAutoTeleport.HasWhiteText(frame, area));
    }
}
