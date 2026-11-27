// SIFT matching ported from BetterGI (Feature2DExtensions.KnnMatch, BigMapTeyvat256Layer, FeatureStorageHelper,
// TeyvatMap coordinate conversion), GPL-3.0.
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine.Nav;

/// <summary>
/// What part of Teyvat the open big map (M) shows. SIFT features of the 1/8-scale Teyvat map
/// (Teyvat_0_256_SIFT.kp.bin + .mat.png from BetterGI's Assets\Map\Teyvat) matched against a 1/4-size screenshot.
/// Gives a screen ↔ Genshin-map-coordinate transform, so a commission marker at the screen centre gets
/// its map position, and a route's teleport waypoint (map position) gets a screen point to click.
/// </summary>
public sealed class BigMapLocator : IDisposable
{
    // TeyvatMap: 22x19 blocks of 2048 px, origin at block (15+1, 11+1); Genshin units are 1024-level (2 px of 2048).
    private const float Origin2048X = (15 + 1) * 2048f;
    private const float Origin2048Y = (11 + 1) * 2048f;
    private const float Px2048PerUnit = 2;
    private const float Scale256To2048 = 8;
    private const double QueryScale = 0.25;

    private readonly KeyPoint[] _trainKeyPoints;
    private readonly Mat _trainDescriptors;
    private readonly Feature2D _sift = SIFT.Create();
    private readonly DescriptorMatcher _matcher = DescriptorMatcher.Create("FlannBased");

    private BigMapLocator(KeyPoint[] kp, Mat desc)
    {
        _trainKeyPoints = kp;
        _trainDescriptors = desc;
    }

    /// <summary>Null when the feature files aren't in <paramref name="dir"/>.</summary>
    public static BigMapLocator? Load(string dir)
    {
        var kpPath = Path.Combine(dir, "Teyvat_0_256_SIFT.kp.bin");
        var matPath = Path.Combine(dir, "Teyvat_0_256_SIFT.mat.png");
        if (!File.Exists(kpPath) || !File.Exists(matPath))
        {
            return null;
        }

        return new BigMapLocator(LoadKeyPoints(kpPath), LoadDescriptors(matPath));
    }

    private static unsafe KeyPoint[] LoadKeyPoints(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var size = Marshal.SizeOf<KeyPoint>();
        if (bytes.Length % size != 0)
        {
            throw new InvalidDataException("Unrecognised KeyPoint file " + path);
        }

        var kps = new KeyPoint[bytes.Length / size];
        fixed (byte* src = bytes)
        fixed (KeyPoint* dst = kps)
        {
            Buffer.MemoryCopy(src, dst, bytes.Length, bytes.Length);
        }

        return kps;
    }

    private static Mat LoadDescriptors(string path)
    {
        using var img = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
        var mat = new Mat();
        img.ConvertTo(mat, MatType.CV_32FC1);
        return mat;
    }

    public static Point2f Img256ToWorld(Point2f p) =>
        new((Origin2048X - p.X * Scale256To2048) / Px2048PerUnit, (Origin2048Y - p.Y * Scale256To2048) / Px2048PerUnit);

    public static Point2f WorldToImg256(Point2f w) =>
        new((Origin2048X - w.X * Px2048PerUnit) / Scale256To2048, (Origin2048Y - w.Y * Px2048PerUnit) / Scale256To2048);

    /// <summary>
    /// Matches an open big map (1920x1080 BGR frame). Null when it can't be matched (map not open, too zoomed in/out,
    /// covered by a panel...).
    /// </summary>
    public BigMapView? Locate(Mat frame)
    {
        using var grey = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var small = grey.Resize(new Size(), QueryScale, QueryScale, InterpolationFlags.Area);
        using var queryDesc = new Mat();
        _sift.DetectAndCompute(small, null, out var queryKp, queryDesc);
        if (queryKp.Length == 0 || queryDesc.Empty())
        {
            return null;
        }

        var matches = _matcher.KnnMatch(queryDesc, _trainDescriptors, 2);
        var good = matches.Where(m => m.Length == 2 && m[0].Distance < 0.75 * m[1].Distance).Select(m => m[0]).ToList();
        if (good.Count < 7)
        {
            return null;
        }

        var src = good.Select(m => new Point2d(queryKp[m.QueryIdx].Pt.X / QueryScale, queryKp[m.QueryIdx].Pt.Y / QueryScale));
        var dst = good.Select(m => new Point2d(_trainKeyPoints[m.TrainIdx].Pt.X, _trainKeyPoints[m.TrainIdx].Pt.Y));
        using var inliers = new Mat();
        var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3.0 / QueryScale, inliers);
        if (h.Empty())
        {
            return null;
        }

        var inlierCount = Cv2.CountNonZero(inliers);
        if (inlierCount < 7)
        {
            h.Dispose();
            return null;
        }

        return new BigMapView(h, frame.Width, frame.Height, inlierCount);
    }

    public void Dispose()
    {
        _trainDescriptors.Dispose();
        _sift.Dispose();
        _matcher.Dispose();
    }
}

/// <summary>A matched big-map screen: converts between screen pixels (1920x1080) and Genshin map coordinates.</summary>
public sealed class BigMapView : IDisposable
{
    private readonly Mat _screenTo256;
    private readonly Mat _256ToScreen;

    public BigMapView(Mat screenTo256, int width, int height, int inliers)
    {
        _screenTo256 = screenTo256;
        _256ToScreen = screenTo256.Inv();
        Inliers = inliers;
        Centre = ScreenToWorld(new Point2f(width / 2f, height / 2f));
        var right = ScreenToWorld(new Point2f(width / 2f + 100, height / 2f));
        PixelsPerUnit = 100 / Centre.DistanceTo(right);
    }

    public int Inliers { get; }

    /// <summary>Map position at the middle of the screen.</summary>
    public Point2f Centre { get; }

    /// <summary>Screen pixels per Genshin map unit at the current zoom.</summary>
    public double PixelsPerUnit { get; }

    public Point2f ScreenToWorld(Point2f screen) =>
        BigMapLocator.Img256ToWorld(Cv2.PerspectiveTransform([screen], _screenTo256)[0]);

    public Point2f WorldToScreen(Point2f world) =>
        Cv2.PerspectiveTransform([BigMapLocator.WorldToImg256(world)], _256ToScreen)[0];

    public Point WorldToScreenPoint(Point2f world)
    {
        var p = WorldToScreen(world);
        return new Point((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    public void Dispose()
    {
        _screenTo256.Dispose();
        _256ToScreen.Dispose();
    }
}
