using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// "Auto-teleport when I pick a waypoint": on the big map, once you click a waypoint (or statue, domain, …)
/// and its side panel shows the Teleport button, press it. If overlapping icons open a pick list,
/// choose the teleport waypoint row (or another teleport point if there is no waypoint).
/// Port of BetterGI's QuickTeleportTrigger, with the list text check done by colour instead of OCR.
/// </summary>
public sealed class WaypointAutoTeleport : IDisposable
{
    public enum Kind
    {
        ClickTeleport,
        ClickListRow,
    }

    public readonly record struct Step(Kind Kind, Point At, string What);

    // Pick-list icons, searched in a narrow column (BetterGI's MapChooseIconRoi). Waypoint first: it wins when present.
    private static readonly Rect ListColumn = new(1270, 100, 50, Vision.Height - 200);

    private static readonly (string File, string Name, double Threshold)[] ListIcons =
    [
        ("waypoint_in_list.png", "waypoint", 0.7),
        ("list_statue.png", "Statue of The Seven", 0.8),
        ("list_domain.png", "domain", 0.8),
        ("list_domain2.png", "domain", 0.8),
        ("list_portable_waypoint.png", "portable waypoint", 0.8),
        ("list_subspace_waypoint.png", "waypoint", 0.8),
        ("list_obsidian_totem.png", "totem pole", 0.8),
        ("list_mansion.png", "mansion", 0.8),
        ("list_meeting_point.png", "meeting point", 0.8),
        ("list_tablet_of_tona.png", "Tablet of Tona", 0.8),
        ("list_moon_tower.png", "tower", 0.8),
    ];

    private readonly Template _teleportButton = new("teleport_button.png", new Rect(1440, 960, 100, 120));
    private readonly Template _mapClose = new("map_close_button.png", new Rect(Vision.Width - 107, 19, 58, 58));
    private readonly Template _mapChoose = new("map_choose.png", new Rect(Vision.Width - 480, 0, 100, 70));
    private readonly Template _mapScale = new("map_scale_button.png", new Rect(30, 440, 40, 200), blackIsTransparent: true);
    private readonly Template _mapSettings = new("map_settings_button.png", new Rect(25, 990, 58, 62));
    private readonly List<(Template Icon, string Name)> _listIcons =
        ListIcons.Select(i => (new Template(i.File, ListColumn, i.Threshold), i.Name)).ToList();

    public bool IsMapOpen(Mat grey) => Vision.Find(grey, _mapScale) != null || Vision.Find(grey, _mapSettings) != null;

    public Point? FindTeleportButton(Mat grey) => Vision.Find(grey, _teleportButton) is { } r ? Centre(r) : null;

    /// <summary>
    /// What to click on this big-map frame, or null if nothing (not the map, or no point selected).
    /// </summary>
    public Step? Decide(Mat bgr, Mat grey)
    {
        if (!IsMapOpen(grey))
        {
            return null;
        }

        if (FindTeleportButton(grey) is { } button)
        {
            return new Step(Kind.ClickTeleport, button, "Teleport");
        }

        // The map's own close (X) and map-switch buttons are hidden while a panel or pick list is open.
        if (Vision.Find(grey, _mapClose) != null || Vision.Find(grey, _mapChoose) != null)
        {
            return null;
        }

        foreach (var (icon, name) in _listIcons)
        {
            // Top-most row of this kind that has a label next to it (an icon alone is not a list row).
            foreach (var r in Vision.FindAll(grey, icon, maxCount: 6).OrderBy(r => r.Y))
            {
                var text = RowText(r);
                if (HasWhiteText(bgr, text))
                {
                    return new Step(Kind.ClickListRow, Centre(text), name);
                }
            }
        }

        return null;
    }

    /// <summary>The 200 px label area to the right of a list icon (BetterGI clicks this area).</summary>
    private static Rect RowText(Rect icon) => new(icon.X + icon.Width, icon.Y - 8, 200, icon.Height + 16);

    /// <summary>White label text (HLS lightness 245+, saturation 15 or less, as BetterGI filters before OCR).</summary>
    public static bool HasWhiteText(Mat bgr, Rect area)
    {
        area &= new Rect(0, 0, bgr.Width, bgr.Height);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return false;
        }

        using var crop = new Mat(bgr, area);
        using var hls = crop.CvtColor(ColorConversionCodes.BGR2HLS);
        using var mask = new Mat();
        Cv2.InRange(hls, new Scalar(0, 245, 0), new Scalar(180, 255, 15), mask);
        return Cv2.CountNonZero(mask) >= 25;
    }

    private static Point Centre(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public void Dispose()
    {
        foreach (var t in new[] { _teleportButton, _mapClose, _mapChoose, _mapScale, _mapSettings })
        {
            t.Dispose();
        }

        foreach (var (icon, _) in _listIcons)
        {
            icon.Dispose();
        }
    }
}
