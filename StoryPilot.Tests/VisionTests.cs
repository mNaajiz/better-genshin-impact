using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Tests;

public class VisionTests
{
    private static Mat Load(string file) => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Assets", file), ImreadModes.Color);

    /// <summary>A dim, noisy 1080p background so CCoeffNormed has texture to work against.</summary>
    private static Mat Background()
    {
        var m = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC3);
        Cv2.Randu(m, new Scalar(30, 30, 30), new Scalar(90, 90, 90));
        return m;
    }

    private static void Paste(Mat frame, Mat image, int x, int y) => image.CopyTo(new Mat(frame, new Rect(x, y, image.Width, image.Height)));

    [Fact]
    public void FindsTalkButtonOnlyWhenPresent()
    {
        using var t = new Template("disabled_ui.png", new Rect(0, 0, 640, 135));
        using var frame = Background();
        using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        Assert.Null(Vision.Find(grey, t));

        using var icon = Load("disabled_ui.png");
        Paste(frame, icon, 60, 30);
        using var grey2 = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        var hit = Vision.Find(grey2, t);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.X, 58, 62);
        Assert.InRange(hit.Value.Y, 28, 32);
    }

    [Fact]
    public void FindsEveryOptionIconTopToBottom()
    {
        using var t = new Template("icon_option.png", new Rect(960, 90, 640, 980));
        using var icon = Load("icon_option.png");
        using var frame = Background();
        int[] ys = [500, 590, 680];
        foreach (var y in ys)
        {
            Paste(frame, icon, 1290, y);
        }

        using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        var hits = Vision.FindAll(grey, t).OrderBy(r => r.Y).ToList();
        Assert.Equal(3, hits.Count);
        for (var i = 0; i < ys.Length; i++)
        {
            Assert.InRange(hits[i].Y, ys[i] - 2, ys[i] + 2);
        }
    }

    [Fact]
    public void DetectsOrangeQuestText()
    {
        using var frame = Background();
        var text = new Rect(1340, 600, 300, 30);
        Assert.True(Vision.OrangeRatio(frame, text) < 0.01);
        // RGB(250, 200, 50) as BGR.
        frame[new Rect(1350, 605, 200, 20)].SetTo(new Scalar(50, 200, 250));
        Assert.True(Vision.OrangeRatio(frame, text) > 0.06);
    }

    [Fact]
    public void BlackScreenRatio()
    {
        using var black = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1, Scalar.All(0));
        black[new Rect(600, 500, 700, 60)].SetTo(Scalar.All(255)); // narration text
        var rate = Vision.MiddleBlackRatio(black);
        Assert.InRange(rate, 0.5, 0.99);

        using var game = new Mat(Vision.Height, Vision.Width, MatType.CV_8UC1, Scalar.All(80));
        Assert.True(Vision.MiddleBlackRatio(game) < 0.5);
    }

    [Fact]
    public void AllTemplatesLoad()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets"), "*.png"))
        {
            using var t = new Template(Path.GetFileName(file), new Rect(0, 0, Vision.Width, Vision.Height));
            Assert.False(t.Image.Empty());
        }
    }
}
