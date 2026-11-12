using OpenCvSharp;
using OpenCvSharp.Dnn;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine;

/// <summary>
/// Finds the domain reward tree (Petrified Tree / Ley Line Blossom) with BetterGI's YOLOv8 model
/// (Assets\Model\bgi_tree.onnx, copied from a BetterGI install at build time). Runs on the CPU through
/// OpenCV's DNN module, so no extra runtime is needed.
/// </summary>
public sealed class TreeDetector : IDisposable
{
    public const int InputSize = 640;
    public const float MinConfidence = 0.5f;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Model", "bgi_tree.onnx");

    private readonly Net _net;

    private TreeDetector(Net net)
    {
        _net = net;
        _net.SetPreferableBackend(Backend.OPENCV);
        _net.SetPreferableTarget(Target.CPU);
    }

    /// <summary>Null when the model file is missing.</summary>
    public static TreeDetector? TryLoad()
    {
        if (!File.Exists(ModelPath))
        {
            return null;
        }

        return new TreeDetector(CvDnn.ReadNetFromOnnx(ModelPath) ?? throw new InvalidOperationException("Could not load the tree model"));
    }

    /// <summary>Best tree box in frame coordinates, or null.</summary>
    public Rect? Detect(Mat bgr)
    {
        var (blob, scale, padX, padY) = Letterbox(bgr);
        using (blob)
        {
            _net.SetInput(blob);
            using var output = _net.Forward();
            return Best(output, scale, padX, padY, bgr.Width, bgr.Height);
        }
    }

    /// <summary>Resize keeping the aspect ratio, pad to a square (grey 114 like Ultralytics), RGB, 0..1.</summary>
    internal static (Mat Blob, double Scale, int PadX, int PadY) Letterbox(Mat bgr)
    {
        var scale = Math.Min((double)InputSize / bgr.Width, (double)InputSize / bgr.Height);
        var w = (int)Math.Round(bgr.Width * scale);
        var h = (int)Math.Round(bgr.Height * scale);
        var padX = (InputSize - w) / 2;
        var padY = (InputSize - h) / 2;
        using var resized = bgr.Resize(new Size(w, h), 0, 0, InterpolationFlags.Linear);
        using var square = new Mat(InputSize, InputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
        resized.CopyTo(new Mat(square, new Rect(padX, padY, w, h)));
        var blob = CvDnn.BlobFromImage(square, 1 / 255.0, new Size(InputSize, InputSize), new Scalar(), swapRB: true, crop: false);
        return (blob, scale, padX, padY);
    }

    /// <summary>
    /// YOLOv8 output is [1, 4 + classes, boxes] (cx, cy, w, h, class scores). Some exports are transposed
    /// to [1, boxes, 4 + classes]; both are handled. Returns the highest-scoring box mapped back to the frame.
    /// </summary>
    internal static Rect? Best(Mat output, double scale, int padX, int padY, int frameW, int frameH)
    {
        var a = output.Size(1);
        var b = output.Size(2);
        var transposed = a > b; // rows are boxes
        var boxes = transposed ? a : b;
        var fields = transposed ? b : a;
        if (fields < 5)
        {
            return null;
        }

        using var m = Mat.FromPixelData(a, b, MatType.CV_32F, output.Data);
        m.GetArray<float>(out var data);
        float At(int field, int box) => transposed ? data[box * fields + field] : data[field * boxes + box];

        var bestScore = MinConfidence;
        Rect? best = null;
        for (var i = 0; i < boxes; i++)
        {
            var score = 0f;
            for (var c = 4; c < fields; c++)
            {
                score = Math.Max(score, At(c, i));
            }

            if (score <= bestScore)
            {
                continue;
            }

            var cx = (At(0, i) - padX) / scale;
            var cy = (At(1, i) - padY) / scale;
            var w = At(2, i) / scale;
            var h = At(3, i) / scale;
            var x = (int)Math.Round(cx - w / 2);
            var y = (int)Math.Round(cy - h / 2);
            var r = new Rect(x, y, (int)Math.Round(w), (int)Math.Round(h)) & new Rect(0, 0, frameW, frameH);
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }

            bestScore = score;
            best = r;
        }

        return best;
    }

    public void Dispose() => _net.Dispose();
}
