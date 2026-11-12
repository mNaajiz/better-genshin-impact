using OpenCvSharp;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine;

/// <summary>
/// Which way the camera faces, read from the view cone on the minimap (0 = east, counter-clockwise up to 360).
/// Port of BetterGI's CameraOrientationFromGia: unroll the minimap around its centre and look for the two
/// straight edges of the view cone, which are 90 degrees apart. CPU only, about 1 ms per frame.
/// </summary>
public static class CameraAngle
{
    /// <summary>Minimap in a 1920x1080 frame (BetterGI MapAssets.MimiMapRect).</summary>
    public static readonly Rect MiniMap = new(62, 19, 212, 212);

    public static float Compute(Mat frame1080)
    {
        using var mini = new Mat(frame1080, MiniMap);
        using var grey = mini.Channels() == 3 ? mini.CvtColor(ColorConversionCodes.BGR2GRAY) : mini.Clone();
        return ComputeMiniMap(grey);
    }

    public static float ComputeMiniMap(Mat grey)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(grey, blurred, new Size(3, 3), 0);
        var centre = new Point2f(blurred.Width / 2f, blurred.Height / 2f);
        using var polar = new Mat();
        Cv2.WarpPolar(blurred, polar, new Size(360, 360), centre, 360d, InterpolationFlags.Linear, WarpPolarMode.Linear);
        using var roi = new Mat(polar, new Rect(10, 0, 70, polar.Height)).Clone();
        Cv2.Rotate(roi, roi, RotateFlags.Rotate90Counterclockwise);

        using var scharr = new Mat();
        Cv2.Scharr(roi, scharr, MatType.CV_32F, 1, 0);
        scharr.GetArray<float>(out var array);

        var left = new int[360];
        var right = new int[360];
        foreach (var i in FindPeaks(array))
        {
            left[i % 360]++;
        }

        foreach (var i in FindPeaks(array.Select(x => -x).ToArray()))
        {
            right[i % 360]++;
        }

        var left2 = left.Zip(right, (x, y) => Math.Max(x - y, 0)).ToArray();
        var right2 = right.Zip(left, (x, y) => Math.Max(x - y, 0)).ToArray();

        var sum = new int[360];
        for (var i = -2; i <= 2; i++)
        {
            var shifted = Shift(right2, -90 + i);
            for (var j = 0; j < 360; j++)
            {
                sum[j] += left2[j] * shifted[j] * (3 - Math.Abs(i)) / 3;
            }
        }

        var result = new int[360];
        for (var i = -2; i <= 2; i++)
        {
            var shifted = Shift(sum, i);
            for (var j = 0; j < 360; j++)
            {
                result[j] += shifted[j] * (3 - Math.Abs(i)) / 3;
            }
        }

        var maxIndex = Array.IndexOf(result, result.Max());
        var angle = maxIndex + 45;
        if (angle > 360)
        {
            angle -= 360;
        }

        return angle;
    }

    /// <summary>Signed turn (degrees, -180..180) from <paramref name="angle"/> to <paramref name="target"/>.</summary>
    public static double Delta(double angle, double target)
    {
        var d = (target - angle) % 360;
        if (d > 180)
        {
            d -= 360;
        }
        else if (d <= -180)
        {
            d += 360;
        }

        return d;
    }

    private static List<int> FindPeaks(float[] data)
    {
        var peaks = new List<int>();
        for (var i = 1; i < data.Length - 1; i++)
        {
            if (data[i] > data[i - 1] && data[i] > data[i + 1])
            {
                peaks.Add(i);
            }
        }

        return peaks;
    }

    /// <summary>Rotate right by k (k > 0) or left by -k.</summary>
    internal static int[] Shift(int[] a, int k)
    {
        var n = a.Length;
        k = ((k % n) + n) % n;
        return a.Skip(n - k).Concat(a.Take(n - k)).ToArray();
    }
}
