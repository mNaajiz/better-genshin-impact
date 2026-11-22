using OpenCvSharp;
using StoryPilot.Engine;
using Xunit;

namespace StoryPilot.Tests;

/// <summary>Domain farming: camera angle helpers, the tree model's output decoding, and the templates loading.</summary>
public class DomainTests
{
    [Theory]
    [InlineData(10, 0, -10)]
    [InlineData(350, 0, 10)]
    [InlineData(180, 0, 180)]
    [InlineData(90, 270, 180)]
    [InlineData(0, 45, 45)]
    public void DeltaTakesTheShortWayRound(double angle, double target, double expected)
    {
        Assert.Equal(expected, CameraAngle.Delta(angle, target), 6);
    }

    [Fact]
    public void ShiftRotatesBothWays()
    {
        int[] a = [1, 2, 3, 4, 5];
        Assert.Equal([4, 5, 1, 2, 3], CameraAngle.Shift(a, 2));
        Assert.Equal([3, 4, 5, 1, 2], CameraAngle.Shift(a, -2));
        Assert.Equal(a, CameraAngle.Shift(a, 0));
    }

    [Fact]
    public void CameraAngleRunsOnABlankFrame()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(40));
        var angle = CameraAngle.Compute(frame);
        Assert.InRange(angle, 0, 360);
    }

    [Fact]
    public void LetterboxPadsTopAndBottomFor16By9()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(0));
        var (blob, scale, padX, padY) = TreeDetector.Letterbox(frame);
        using (blob)
        {
            Assert.Equal(640.0 / 1920, scale, 6);
            Assert.Equal(0, padX);
            Assert.Equal(140, padY);
            Assert.Equal(4, blob.Dims);
            Assert.Equal(640, blob.Size(2));
        }
    }

    private static Mat Output(float[,] fieldsByBox)
    {
        var fields = fieldsByBox.GetLength(0);
        // Real exports have thousands of candidates; pad with empty ones so boxes > fields, as in the model.
        var boxes = Math.Max(fieldsByBox.GetLength(1), 32);
        var output = new Mat([1, fields, boxes], MatType.CV_32F);
        var flat = new float[fields * boxes];
        for (var f = 0; f < fields; f++)
        {
            for (var b = 0; b < fieldsByBox.GetLength(1); b++)
            {
                flat[f * boxes + b] = fieldsByBox[f, b];
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(flat, 0, output.Data, flat.Length);
        return output;
    }

    [Fact]
    public void BestPicksTheHighestScoreAndMapsBackToTheFrame()
    {
        // Three candidate boxes, one class: (cx, cy, w, h, score) per column, in 640x640 letterbox space.
        using var output = Output(new float[,]
        {
            { 100, 320, 500 },
            { 300, 320, 300 },
            { 20, 64, 20 },
            { 40, 128, 40 },
            { 0.3f, 0.9f, 0.6f },
        });
        var box = TreeDetector.Best(output, 640.0 / 1920, 0, 140, 1920, 1080);
        Assert.NotNull(box);
        var r = box!.Value;
        // Centre (320, 320) -> frame (960, 540); size 64x128 -> 192x384.
        Assert.InRange(r.X + r.Width / 2, 958, 962);
        Assert.InRange(r.Y + r.Height / 2, 538, 542);
        Assert.InRange(r.Width, 190, 194);
        Assert.InRange(r.Height, 382, 386);
    }

    [Fact]
    public void BestIgnoresLowScores()
    {
        using var output = Output(new float[,] { { 320 }, { 320 }, { 64 }, { 64 }, { 0.2f } });
        Assert.Null(TreeDetector.Best(output, 1, 0, 0, 640, 640));
    }

    [Fact]
    public void DomainTemplatesLoad()
    {
        foreach (var file in new[] { "domain_confirm.png", "domain_exit.png", "domain_timer.png" })
        {
            using var t = new Template(file, new OpenCvSharp.Rect(0, 0, 1920, 1080), useAlpha: true);
            Assert.True(t.Image.Width > 20);
            Assert.NotNull(t.Mask);
        }
    }

    [Fact]
    public void DefaultRotationIsYourMavuikaTeam()
    {
        var steps = DomainCombat.Parse(DomainCombat.DefaultRotation);
        Assert.Equal([1, 2, 3, 4, 1], steps.Select(st => st.Slot));
        Assert.Equal("q", steps[4].Actions[0].Name);
        Assert.Equal(8, steps[4].Actions[1].Seconds);
    }

    [Fact]
    public void RotationParsesHoldSecondsAndComments()
    {
        var steps = DomainCombat.Parse("// Zhongli\r\n2: e(hold), wait(0.2), charge(1.5)\n\n# end");
        var step = Assert.Single(steps);
        Assert.Equal(2, step.Slot);
        Assert.True(step.Actions[0].Hold);
        Assert.Equal(0.2, step.Actions[1].Seconds, 6);
        Assert.Equal("charge", step.Actions[2].Name);
    }

    [Theory]
    [InlineData("5: e")]
    [InlineData("e, q")]
    [InlineData("1: fly")]
    [InlineData("1: wait(soon)")]
    [InlineData("")]
    public void BadRotationsSayWhatIsWrong(string text)
    {
        Assert.Throws<FormatException>(() => DomainCombat.Parse(text));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ActiveSlotIsTheGreyNumberBox(int active)
    {
        using var grey = new Mat(1080, 1920, MatType.CV_8UC1, Scalar.All(100));
        int[] ys = [276, 367, 458, 549];
        for (var i = 0; i < 4; i++)
        {
            grey[new OpenCvSharp.Rect(1858, ys[i] - 14, 28, 28)].SetTo(Scalar.All(i + 1 == active ? 180 : 229));
        }

        Assert.Equal(active, DomainFarmer.ActiveSlotOf(grey));
    }

    [Fact]
    public void ActiveSlotIsUnknownWhenAllBoxesLookAlike()
    {
        using var grey = new Mat(1080, 1920, MatType.CV_8UC1, Scalar.All(200));
        Assert.Equal(0, DomainFarmer.ActiveSlotOf(grey));
    }

    [Fact]
    public void XilonenAttacksThreeTimesInTheDefaultRotation()
    {
        var xilonen = DomainCombat.Parse(DomainCombat.DefaultRotation)[2];
        Assert.Equal(3, xilonen.Slot);
        Assert.Equal(["e", "attack", "attack", "attack"], xilonen.Actions.Select(a => a.Name));
    }

    [Fact]
    public void VaryKeepsDelaysNearTheBaseButNeverIdentical()
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var v = DomainFarmer.Vary(1000);
            Assert.InRange(v, 850, 1310);
            seen.Add(v);
        }

        Assert.True(seen.Count > 50);
        Assert.Equal(0, DomainFarmer.Vary(0));
    }

    [Fact]
    public void ResinCountTemplatesLoad()
    {
        foreach (var file in new[] { "resin_condensed_top.png", "resin_digit_0.png", "resin_digit_1.png", "resin_digit_2.png", "resin_digit_4.png", "resin_digit_5.png" })
        {
            using var t = new Template(file, new OpenCvSharp.Rect(0, 0, 1920, 1080));
            Assert.True(t.Image.Height >= 20);
        }
    }

    [Fact]
    public void ChallengeStartedBannerLoads()
    {
        using var t = new Template("domain_defeat.png", new OpenCvSharp.Rect(0, 0, 1920, 1080));
        Assert.True(t.Image.Width > 80);
    }
}
