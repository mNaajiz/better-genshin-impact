using System.Diagnostics;
using OpenCvSharp;
using StoryPilot.Engine.Nav;
using Xunit;
using Xunit.Abstractions;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Tests;

/// <summary>
/// Minimap position tracking. Needs BetterGI's map pieces, which are not in git: set STORYPILOT_MAP_DIR
/// to an Assets\Map\Teyvat folder (or put one at StoryPilot\Assets\Map\Teyvat). Without them these pass without checking.
/// </summary>
public class NavTests(ITestOutputHelper output)
{
    private static string? FindUp(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, name)))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir == null ? null : Path.Combine(dir, name);
    }

    internal static string? MapDir()
    {
        var env = Environment.GetEnvironmentVariable("STORYPILOT_MAP_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        var sp = FindUp(Path.Combine("StoryPilot", "Assets", "Map", "Teyvat"));
        return sp;
    }

    private static readonly Lazy<MiniMapLocator?> Shared = new(() =>
        MapDir() is { } d ? MiniMapLocator.Load(d) : null);

    [Fact]
    public void PreprocessesASyntheticMiniMap()
    {
        // Only checks the ported preprocessing runs and returns 156x156 image + mask.
        using var mini = new Mat(212, 212, MatType.CV_8UC3, new Scalar(90, 120, 100));
        Cv2.Circle(mini, new Point(106, 106), 8, new Scalar(255, 255, 255), -1);
        using var pre = new MiniMapPreprocessor();
        var (img, mask) = pre.GetMiniMapAndMask(mini);
        using (img)
        using (mask)
        {
            Assert.Equal(new Size(156, 156), img.Size());
            Assert.Equal(new Size(156, 156), mask.Size());
            Assert.Equal(MatType.CV_8UC1, mask.Type());
        }
    }

    [Fact]
    public void LocatesEveryLocalScreenshot()
    {
        var locator = Shared.Value;
        var shots = FindUp("LocalData");
        if (locator == null || shots == null)
        {
            output.WriteLine("No map pieces or no LocalData; skipped.");
            return;
        }

        output.WriteLine($"{locator.LayerCount} map layers loaded");
        foreach (var file in Directory.GetFiles(shots, "*.png"))
        {
            using var frame = Cv2.ImRead(file, ImreadModes.Color);
            if (frame.Width != 1920 || frame.Height != 1080)
            {
                continue;
            }

            locator.Reset();
            var sw = Stopwatch.StartNew();
            var global = locator.Locate(frame);
            var tGlobal = sw.ElapsedMilliseconds;
            sw.Restart();
            var local = locator.Locate(frame);
            output.WriteLine($"{Path.GetFileName(file)}: global {Fmt(global)} in {tGlobal} ms, local {Fmt(local)} in {sw.ElapsedMilliseconds} ms");
            if (global is { } g && local is { } l)
            {
                Assert.True(g.DistanceTo(l) < 3, "local search disagrees with global");
            }
        }
    }

    [Fact]
    public void ConvertsBetweenMapImageAndWorld()
    {
        var w = new Point2f(-1234.5f, 2345.25f);
        var back = BigMapLocator.Img256ToWorld(BigMapLocator.WorldToImg256(w));
        Assert.True(back.DistanceTo(w) < 0.01);
    }

    [Theory]
    [InlineData("map_on_quest.png")]
    [InlineData("map_quest_button.png")]
    public void LocatesTheBigMap(string file)
    {
        var dir = MapDir();
        var shots = FindUp("LocalData");
        if (dir == null || shots == null || !File.Exists(Path.Combine(shots, file)))
        {
            output.WriteLine("No map pieces or screenshot; skipped.");
            return;
        }

        using var big = BigMapLocator.Load(dir);
        Assert.NotNull(big);
        using var frame = Cv2.ImRead(Path.Combine(shots, file), ImreadModes.Color);
        var sw = Stopwatch.StartNew();
        using var view = big!.Locate(frame);
        output.WriteLine(view == null ? "no match" : $"centre ({view.Centre.X:F1}, {view.Centre.Y:F1}), {view.PixelsPerUnit:F3} px/unit, {view.Inliers} inliers, {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(view);
        var p = view!.WorldToScreen(view.Centre);
        Assert.True(Math.Abs(p.X - 960) < 1 && Math.Abs(p.Y - 540) < 1);
    }

    private static string Fmt(Point2f? p) => p is { } v ? $"({v.X:F1}, {v.Y:F1})" : "none";
}
