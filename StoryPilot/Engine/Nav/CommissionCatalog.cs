using System.Text.Json;
using OpenCvSharp;

namespace StoryPilot.Engine.Nav;

/// <summary>One way to do a commission at one place (a folder of the community AutoCommissionNova data).</summary>
public sealed class CommissionEntry
{
    /// <summary>Chinese commission name, as the community data names it.</summary>
    public required string Commission { get; init; }
    public required string Kind { get; init; }
    public required string Location { get; init; }
    public required string Variant { get; init; }
    public Point2f? Anchor { get; init; }
    public required JsonElement Steps { get; init; }
    public required Dictionary<string, List<RoutePoint>> Routes { get; init; }

    public bool IsCombat => Kind == "basic";

    public IReadOnlyList<RoutePoint> MainRoute => Routes.TryGetValue("_path.json", out var r) ? r : [];

    public RoutePoint? Teleport => MainRoute.FirstOrDefault(p => p.IsTeleport);

    public override string ToString() => $"{Commission} @ {Variant}";
}

/// <summary>The imported commission routes (Assets\Commissions\*.json, made by tools/import_commissions.py).</summary>
public sealed class CommissionCatalog(List<CommissionEntry> entries)
{
    public IReadOnlyList<CommissionEntry> Entries => entries;

    /// <summary>A commission marker further than this from every route end isn't one we know.</summary>
    public const float MaxMatchDistance = 40;

    public static CommissionCatalog Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var list = new List<CommissionEntry>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var routes = new Dictionary<string, List<RoutePoint>>();
            foreach (var r in e.GetProperty("routes").EnumerateObject())
            {
                routes[r.Name] = r.Value.EnumerateArray().Select(p => new RoutePoint(
                    Str(p, "type"), Str(p, "move_mode"), Str(p, "action"), Str(p, "action_params"),
                    p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle())).ToList();
            }

            Point2f? anchor = e.GetProperty("anchor").ValueKind == JsonValueKind.Object
                ? new Point2f(e.GetProperty("anchor").GetProperty("x").GetSingle(), e.GetProperty("anchor").GetProperty("y").GetSingle())
                : null;
            list.Add(new CommissionEntry
            {
                Commission = Str(e, "commission"),
                Kind = Str(e, "kind"),
                Location = Str(e, "location"),
                Variant = Str(e, "variant"),
                Anchor = anchor,
                Steps = e.GetProperty("steps").Clone(),
                Routes = routes,
            });
        }

        return new CommissionCatalog(list);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The entry whose route ends nearest a commission marker's map position, if close enough.</summary>
    public CommissionEntry? Nearest(Point2f marker, float maxDistance = MaxMatchDistance)
    {
        CommissionEntry? best = null;
        var bestD = double.MaxValue;
        foreach (var e in entries)
        {
            if (e.Anchor is not { } a)
            {
                continue;
            }

            var d = a.DistanceTo(marker);
            if (d < bestD)
            {
                bestD = d;
                best = e;
            }
        }

        return bestD <= maxDistance ? best : null;
    }
}
