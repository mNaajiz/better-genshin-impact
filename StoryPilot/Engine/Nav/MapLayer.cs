// Ported from BetterGI (GameTask/Common/Map/Maps/Base/BaseMapLayerByTemplateMatch.cs), GPL-3.0.
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using static StoryPilot.Engine.Nav.MiniMapMatchConfig;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace StoryPilot.Engine.Nav;

/// <summary>
/// One piece of BetterGI's layered Teyvat map: a small colour image for the rough minimap search and
/// a full-size grey image for the exact one. Positions are Genshin map coordinates (the x/y in route files).
/// </summary>
public sealed class MapLayer : IDisposable
{
    public string LayerGroupId { get; set; } = string.Empty;
    public string LayerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public float Scale { get; set; } = 1;
    public int Floor { get; set; }
    public float Top { get; set; }
    public float Left { get; set; }
    public bool IsOverSize { get; set; }

    [JsonIgnore] public FastSqDiffMatcher? CoarseColorMatcher { get; private set; }
    [JsonIgnore] public Mat FineGrayMap { get; private set; } = new();

    /// <summary>World-space bounds of the layer (Left/Top are the maximum x/y; the map grows toward smaller values).</summary>
    [JsonIgnore] public Rect2f WorldBounds => new(Left - FineGrayMap.Width / Scale, Top - FineGrayMap.Height / Scale,
        FineGrayMap.Width / Scale, FineGrayMap.Height / Scale);

    private static Mat Read(string path, ImreadModes mode)
    {
        // ImDecode, not ImRead: copes with non-ASCII paths.
        var mat = Cv2.ImDecode(File.ReadAllBytes(path), mode);
        if (mat.Empty())
        {
            throw new InvalidDataException($"Couldn't read map image {path}");
        }

        return mat;
    }

    public void Load(string dir)
    {
        using var coarse = Read(Path.Combine(dir, LayerId + "_color.webp"), ImreadModes.Color);
        CoarseColorMatcher = new FastSqDiffMatcher(coarse, new Size(RoughSize, RoughSize));
        FineGrayMap = Read(Path.Combine(dir, LayerId + "_gray" + (IsOverSize ? ".png" : ".webp")), ImreadModes.Grayscale);
    }

    /// <summary>
    /// Loads every template-match layer described by the JSON files at the top of <paramref name="dir"/>
    /// (BetterGI's Assets\Map\Teyvat). <paramref name="keep"/> can drop layers before their images load.
    /// </summary>
    public static List<MapLayer> LoadAll(string dir, Func<MapLayer, bool>? keep = null)
    {
        var layers = new List<MapLayer>();
        if (!Directory.Exists(dir))
        {
            return layers;
        }

        foreach (var json in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            List<MapLayer>? list;
            try
            {
                list = JsonSerializer.Deserialize<List<MapLayer>>(File.ReadAllText(json));
            }
            catch (JsonException)
            {
                continue; // Not a layer list (BetterGI keeps other JSON there too).
            }

            foreach (var layer in list ?? [])
            {
                if (string.IsNullOrEmpty(layer.LayerId) || (keep != null && !keep(layer)))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(dir, layer.LayerId + "_color.webp")))
                {
                    continue;
                }

                layer.Load(dir);
                layers.Add(layer);
            }
        }

        return layers;
    }

    public (Point2f Pos, double Val) RoughMatch(Mat[] maskedMiniMaps, Mat maskF)
    {
        var (pos, val) = CoarseColorMatcher!.Match(maskedMiniMaps, maskF);
        return (MapToWorld(pos, RoughZoom, RoughSize), val);
    }

    public (Point2f Pos, double Val) RoughMatch(Mat[] maskedMiniMaps, Mat maskF, Point2f preLoc)
    {
        var roughPos = WorldToMap(preLoc, RoughZoom);
        var src = CoarseColorMatcher!.Source[0];
        var rect = GetRect(roughPos, (int)(RoughSearchRadius * Scale), RoughSize).Intersect(new Rect(0, 0, src.Width, src.Height));
        if (rect.Width < RoughSize || rect.Height < RoughSize)
        {
            return (default, -1);
        }

        var (pos, val) = CoarseColorMatcher.Match(maskedMiniMaps, maskF, rect);
        return (MapToWorld(rect.TopLeft + pos, RoughZoom, RoughSize), val);
    }

    public (Point2f Pos, double Val) ExactMatch(Mat miniMap, Mat mask, Point2f preLoc)
    {
        var exactPos = WorldToMap(preLoc, ExactZoom);
        var rect = GetRect(exactPos, ExactSearchRadius, ExactSize).Intersect(new Rect(0, 0, FineGrayMap.Width, FineGrayMap.Height));
        if (rect.Width < ExactSize || rect.Height < ExactSize)
        {
            return (default, -1);
        }

        using var bigMap = FineGrayMap[rect];
        var (pos, val) = TemplateMatchHelper.MatchTemplateSubPix(bigMap, miniMap, TemplateMatchModes.SqDiff, mask);
        return (MapToWorld(rect.TopLeft + pos, ExactZoom, ExactSize), val);
    }

    private static Rect GetRect(Point loc, int halfSide, int miniMapSize) =>
        new(loc.X - halfSide - miniMapSize / 2, loc.Y - halfSide - miniMapSize / 2, halfSide * 2 + miniMapSize, halfSide * 2 + miniMapSize);

    private Point WorldToMap(Point2f pos, float zoom) =>
        new((int)Math.Round((Left - pos.X) * Scale / zoom), (int)Math.Round((Top - pos.Y) * Scale / zoom));

    private Point2f MapToWorld(Point2f pos, float zoom, int miniMapSize) =>
        new(Left - (pos.X + miniMapSize / 2.0f) * zoom / Scale, Top - (pos.Y + miniMapSize / 2.0f) * zoom / Scale);

    public void Dispose()
    {
        CoarseColorMatcher?.Dispose();
        FineGrayMap.Dispose();
    }
}
