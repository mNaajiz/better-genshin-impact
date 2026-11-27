// Matching logic ported from BetterGI (SceneBaseMapByTemplateMatch.cs, NavigationInstance.cs), GPL-3.0.
using OpenCvSharp;
using static StoryPilot.Engine.Nav.MiniMapMatchConfig;
using Rect = OpenCvSharp.Rect;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine.Nav;

/// <summary>
/// Where the player is on the Teyvat map, from the minimap in the top-left corner. Uses BetterGI's
/// template-match map pieces (Assets\Map\Teyvat, copied from a BetterGI build; not in git).
/// CPU only. Positions are Genshin map coordinates, the same x/y as the community route files.
/// </summary>
public sealed class MiniMapLocator : IDisposable
{
    /// <summary>The minimap on a 1920x1080 frame (BetterGI's MimiMapRect1080P).</summary>
    public static readonly Rect MiniMapRect = new(62, 19, 212, 212);

    /// <summary>A local result further than this from the last one is distrusted and a global search runs.</summary>
    public const float MaxJump = 150;

    private readonly MiniMapPreprocessor _pre = new();
    private readonly List<MapLayer> _layers;
    private MapLayer? _prevLayer;
    private Point2f? _prev;

    public MiniMapLocator(List<MapLayer> layers) => _layers = layers;

    /// <summary>Loads the map pieces from <paramref name="dir"/>; empty (and useless) when the folder is missing.</summary>
    public static MiniMapLocator Load(string dir, Func<MapLayer, bool>? keep = null) => new(MapLayer.LoadAll(dir, keep));

    public int LayerCount => _layers.Count;

    public Point2f? Previous => _prev;

    /// <summary>Forget the last position (after a teleport), so the next search covers the whole map.</summary>
    public void Reset()
    {
        _prev = null;
        _prevLayer = null;
    }

    /// <summary>Set the expected position (e.g. the waypoint just teleported to), so the next search is local.</summary>
    public void SetPrevious(Point2f pos) => _prev = pos;

    /// <summary>The player's map position in a 1920x1080 BGR frame, or null when the minimap can't be matched.</summary>
    public Point2f? Locate(Mat frame)
    {
        using var mini = new Mat(frame, MiniMapRect);
        return LocateMiniMap(mini);
    }

    /// <summary>Same, from an already-cropped 212x212 minimap.</summary>
    public Point2f? LocateMiniMap(Mat miniMap)
    {
        if (_layers.Count == 0)
        {
            return null;
        }

        var (map, mask) = _pre.GetMiniMapAndMask(miniMap);
        using (map)
        using (mask)
        using (var ctx = new MatchContext(map, mask))
        {
            MatchResult result;
            if (_prev is { } prev)
            {
                // Stricter threshold for local results, as BetterGI's GetPositionStable does.
                result = Local(ctx, prev);
                if (!result.IsSuccess(0) || result.MapPos.DistanceTo(prev) > MaxJump)
                {
                    result = Global(ctx);
                }
            }
            else
            {
                result = Global(ctx);
            }

            if (!result.IsSuccess(2))
            {
                return null;
            }

            _prev = result.MapPos;
            _prevLayer = result.Layer;
            return result.MapPos;
        }
    }

    private struct MatchResult
    {
        public MapLayer? Layer;
        public Point2f MapPos;
        public double Confidence;

        public readonly bool IsSuccess(int rank)
        {
            var i = Math.Clamp(rank, 0, ConfidenceThresholds.Length - 1);
            return Layer != null && Confidence <= 1 && Confidence >= ConfidenceThresholds[i];
        }
    }

    private MatchResult Global(MatchContext ctx)
    {
        var r = new MatchResult();
        foreach (var layer in _layers)
        {
            var (pos, val) = layer.RoughMatch(ctx.MaskedMiniMapRoughs, ctx.MaskRoughF);
            if (!ctx.NormalizerRough.Update(val + ctx.TplSumSq))
            {
                continue;
            }

            r.Layer = layer;
            r.MapPos = pos;
        }

        r.Confidence = ctx.NormalizerRough.Confidence();
        Exact(ctx, ref r);
        return r;
    }

    private MatchResult Local(MatchContext ctx, Point2f pos)
    {
        var r = new MatchResult { MapPos = pos };
        // Try the layer we were on first; only if that isn't convincing, every layer near the position.
        var order = _prevLayer != null ? _layers.Where(l => l != _prevLayer).Prepend(_prevLayer) : _layers;
        var first = true;
        foreach (var layer in order)
        {
            var (p, val) = layer.RoughMatch(ctx.MaskedMiniMapRoughs, ctx.MaskRoughF, pos);
            if (p != default && ctx.NormalizerRough.Update(val + ctx.TplSumSq))
            {
                r.Layer = layer;
                r.MapPos = p;
                r.Confidence = ctx.NormalizerRough.Confidence();
            }

            if (first && _prevLayer != null && r.IsSuccess(0))
            {
                break;
            }

            first = false;
        }

        Exact(ctx, ref r);
        return r;
    }

    private static void Exact(MatchContext ctx, ref MatchResult r)
    {
        if (r.Layer == null || !r.IsSuccess(2))
        {
            return;
        }

        var (pos, val) = r.Layer.ExactMatch(ctx.MiniMapExact, ctx.MaskExact, r.MapPos);
        if (pos != default && ctx.NormalizerExact.Update(val))
        {
            r.MapPos = pos;
            r.Confidence = ctx.NormalizerExact.Confidence();
        }
        else
        {
            r.Confidence = 0;
        }
    }

    public void Dispose()
    {
        _pre.Dispose();
        foreach (var l in _layers)
        {
            l.Dispose();
        }
    }
}
