// Based on Fischless.GameCapture/Graphics/GraphicsCapture.cs, GPL-3.0.
// Differences: frames are only copied to the CPU when the engine asks for one,
// and only the game's client area is copied. Nothing is written to the registry.
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;
using SharpDX.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace StoryPilot.Capture;

/// <summary>
/// Windows Graphics Capture of a single window. Works while the window is covered by
/// other windows (but not while it is minimized).
/// </summary>
public sealed class WgcCapture : IDisposable
{
    private readonly object _lock = new();
    private nint _hWnd;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _device;
    private Texture2D? _staging;
    private Windows.Graphics.SizeInt32 _poolSize;

    private bool _wantFrame;
    private Mat? _requested;
    private readonly AutoResetEvent _frameReady = new(false);

    public bool IsCapturing { get; private set; }

    public void Start(nint hWnd)
    {
        Stop();
        _hWnd = hWnd;
        _item = CaptureHelper.CreateItemForWindow(hWnd) ?? throw new InvalidOperationException("Could not create a capture item for the game window.");
        _device = Direct3D11Helper.CreateDevice();
        _poolSize = _item.Size;
        // Free-threaded pool: FrameArrived fires on a worker thread, no dispatcher needed.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += (_, _) => Stop();
        _session = _pool.CreateCaptureSession(_item);
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
        {
            _session.IsCursorCaptureEnabled = false;
        }

        if (ApiInformation.IsWriteablePropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            _session.IsBorderRequired = false;
        }

        _session.StartCapture();
        IsCapturing = true;
    }

    /// <summary>
    /// Returns a BGR copy of the game's client area, or null if no frame arrived in time.
    /// </summary>
    public Mat? Grab(int timeoutMs = 500)
    {
        if (!IsCapturing)
        {
            return null;
        }

        lock (_lock)
        {
            _requested?.Dispose();
            _requested = null;
            _wantFrame = true;
        }

        // The game only presents new frames when something changes, so a static
        // dialogue screen may not produce one; fall back to the last frame we copied.
        _frameReady.WaitOne(timeoutMs);
        lock (_lock)
        {
            _wantFrame = false;
            var mat = _requested;
            _requested = null;
            if (mat != null)
            {
                _last?.Dispose();
                _last = mat.Clone();
                return mat;
            }

            return _last?.Clone();
        }
    }

    private Mat? _last;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_pool == null || _hWnd == 0)
            {
                return;
            }

            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                if (Native.IsIconic(_hWnd))
                {
                    return;
                }

                _poolSize = frame.ContentSize;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize);
                _staging?.Dispose();
                _staging = null;
                return;
            }

            if (!_wantFrame)
            {
                return;
            }

            try
            {
                using var texture = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface);
                var region = ClientRegion(_hWnd, texture.Description.Width, texture.Description.Height);
                if (region == null)
                {
                    return;
                }

                var r = region.Value;
                var w = r.Right - r.Left;
                var h = r.Bottom - r.Top;
                if (_staging == null || _staging.Description.Width != w || _staging.Description.Height != h)
                {
                    _staging?.Dispose();
                    _staging = Direct3D11Helper.CreateStagingTexture(texture.Device, w, h, null);
                }

                var context = texture.Device.ImmediateContext;
                context.CopySubresourceRegion(texture, 0, r, _staging, 0);
                var box = context.MapSubresource(_staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    using var bgra = Mat.FromPixelData(h, w, MatType.CV_8UC4, box.DataPointer, box.RowPitch);
                    _requested = bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
                }
                finally
                {
                    context.UnmapSubresource(_staging, 0);
                }

                _wantFrame = false;
                _frameReady.Set();
            }
            catch (SharpDX.SharpDXException)
            {
                // Device hiccup (e.g. driver reset); try again on the next frame.
            }
        }
    }

    /// <summary>
    /// The captured surface covers the window's extended frame bounds; cut out the client area.
    /// </summary>
    private static ResourceRegion? ClientRegion(nint hWnd, int surfaceWidth, int surfaceHeight)
    {
        if (Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
        {
            return null;
        }

        if (!Native.GetClientRect(hWnd, out var client) || client.Width <= 0 || client.Height <= 0)
        {
            return null;
        }

        var origin = new Native.POINT();
        Native.ClientToScreen(hWnd, ref origin);
        var left = Math.Max(0, origin.X - frame.Left);
        var top = Math.Max(0, origin.Y - frame.Top);
        var right = Math.Min(surfaceWidth, left + client.Width);
        var bottom = Math.Min(surfaceHeight, top + client.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new ResourceRegion(left, top, 0, right, bottom, 1);
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsCapturing = false;
            _hWnd = 0;
            if (_pool != null)
            {
                _pool.FrameArrived -= OnFrameArrived;
            }

            _session?.Dispose();
            _session = null;
            _pool?.Dispose();
            _pool = null;
            _item = null;
            _staging?.Dispose();
            _staging = null;
            _device?.Dispose();
            _device = null;
            _requested?.Dispose();
            _requested = null;
            _last?.Dispose();
            _last = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _frameReady.Dispose();
    }
}
