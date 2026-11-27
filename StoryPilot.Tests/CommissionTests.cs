using OpenCvSharp;
using StoryPilot.Engine.Nav;
using Xunit;

namespace StoryPilot.Tests;

public class CommissionTests
{
    private static CommissionCatalog Catalog() =>
        CommissionCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Commissions", "mondstadt.json"));

    [Fact]
    public void LoadsTheMondstadtRoutes()
    {
        var c = Catalog();
        Assert.Equal(101, c.Entries.Count);
        Assert.Equal(49, c.Entries.Count(e => e.IsCombat));
        Assert.All(c.Entries.Where(e => e.IsCombat), e => Assert.NotNull(e.Teleport));
    }

    [Fact]
    public void PicksTheRouteNearestAMarker()
    {
        var c = Catalog();
        var e = c.Entries.First(x => x.IsCombat);
        var a = e.Anchor!.Value;
        Assert.Same(e, c.Nearest(new Point2f(a.X + 10, a.Y - 10)));
        Assert.Null(c.Nearest(new Point2f(a.X + 5000, a.Y)));
    }

    [Theory]
    [InlineData(0, 0, 10, 0, 0)]
    [InlineData(0, 0, 0, 10, 90)]
    [InlineData(0, 0, -10, 0, 180)]
    [InlineData(0, 0, 0, -10, 270)]
    public void OrientationMatchesBetterGi(float x1, float y1, float x2, float y2, int expected) =>
        Assert.Equal(expected, RouteSteering.OrientationTo(new Point2f(x1, y1), new Point2f(x2, y2)));

    [Theory]
    [InlineData(10, 350, 20)]
    [InlineData(350, 10, -20)]
    [InlineData(90, 90, 0)]
    [InlineData(0, 180, -180)]
    public void WrapsAngleDifferences(float cam, float target, float expected) =>
        Assert.Equal(expected, RouteSteering.AngleDiff(cam, target), 3);

    [Fact]
    public void TurnsHarderForBigErrors()
    {
        Assert.Equal(-4, RouteSteering.TurnDx(4));
        Assert.Equal(-40, RouteSteering.TurnDx(20));
        Assert.Equal(-480, RouteSteering.TurnDx(120));
        Assert.Equal(150, RouteSteering.TurnDx(-50));
    }
}
