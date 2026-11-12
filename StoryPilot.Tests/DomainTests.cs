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
        foreach (var file in new[] { "domain_confirm.png", "domain_exit.png", "in_domain.png" })
        {
            using var t = new Template(file, new OpenCvSharp.Rect(0, 0, 1920, 1080), useAlpha: true);
            Assert.True(t.Image.Width > 20);
            Assert.NotNull(t.Mask);
        }
    }
}
