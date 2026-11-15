using OpenCvSharp;
using Point = OpenCvSharp.Point;

namespace StoryPilot.Engine;

/// <summary>
/// After a story dialogue ends: if the tracked quest is far away, open the quest's map position
/// (J → Navigate, which centres the map on the quest) and teleport to the unlocked waypoint nearest to it.
/// Based on BetterGI's AutoTrackTask, without OCR. Every step is checked on screen; anything unexpected
/// closes the map and gives up quietly.
/// </summary>
public sealed class QuestTeleport(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log) : IDisposable
{
    private const int VK_J = 0x4A;
    private const int VK_M = 0x4D;

    private readonly MapVision _map = new();
    private readonly WaypointAutoTeleport _list = new();

    /// <summary>Set once posted clicks are seen not to work on the map; then only teleport with the game in front.</summary>
    private bool _backgroundClicksFail;

    private nint _hWnd;
    private uint _inputBaseline;
    private bool _gameWasInFront;

    public enum Result
    {
        Teleported,
        NotNeeded,
        GaveUp,
        Cancelled,
    }

    /// <summary>
    /// The quest distance shown under the quest name, or null if there's no tracked quest / unreadable.
    /// </summary>
    public static int? ReadQuestDistance(Mat frame1080) => DistanceReader.Read(frame1080);

    public Result Run(nint hWnd, GameInput input, int minDistanceMetres, int clientW, int clientH)
    {
        _hWnd = hWnd;
        _gameWasInFront = input.IsGameInFront;
        _inputBaseline = Native.LastInputTick();

        if (_backgroundClicksFail && !input.IsGameInFront)
        {
            log("Teleport skipped: the map needs the game in front on this PC");
            return Result.NotNeeded;
        }

        int? metres;
        using (var frame = Grab1080())
        {
            if (frame == null)
            {
                return Result.GaveUp;
            }

            metres = ReadQuestDistance(frame);
        }

        if (metres != null && metres < minDistanceMetres)
        {
            log($"Quest is {metres} m away — close enough, no teleport");
            return Result.NotNeeded;
        }

        log(metres != null
            ? $"Quest is {metres} m away — opening the map"
            : "Couldn't read the quest distance — checking on the map");

        var quest = OpenMapOnQuest(input, clientW, clientH, out var cancelled);
        if (cancelled)
        {
            return Cancelled(input, mapOpen: true);
        }

        if (quest == null)
        {
            BackToGame(input);
            return Result.GaveUp;
        }

        List<Point> targets;
        using (var frame = Grab1080())
        {
            if (frame == null)
            {
                return CloseMap(input, "no frame");
            }

            using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            var points = _map.FindTeleportPoints(frame, grey);
            var player = MapVision.FindPlayer(frame);
            targets = MapVision.RankTeleportPoints(points, quest.Value, player);
            if (targets.Count == 0)
            {
                if (points.Count > 0 && player != null)
                {
                    log("No teleport point is much closer to the quest than you already are");
                    BackToGame(input);
                    return Result.NotNeeded;
                }

                return CloseMap(input, "no unlocked waypoint or statue near the quest");
            }
        }

        // Nearest first. A pin can turn out to be something else (no Teleport button): close its panel, try the next.
        foreach (var target in targets.Take(4))
        {
            Click(input, target.X, target.Y, clientW, clientH);
            if (!Wait(1000))
            {
                return Cancelled(input, mapOpen: true);
            }

            var teleport = FindTeleportButton(input, clientW, clientH);
            if (teleport == null)
            {
                if (!PanelOpen())
                {
                    // Nothing opened at all: the click didn't reach the map.
                    NoteClickFailure(input);
                    return CloseMap(input, "the map didn't react to the click");
                }

                log("That point has no Teleport button, trying the next one");
                input.Key(Native.VK_ESCAPE); // closes the side panel / list, the map stays
                if (!Wait(700))
                {
                    return Cancelled(input, mapOpen: true);
                }

                if (!MapOpen())
                {
                    return Result.GaveUp;
                }

                continue;
            }

            Click(input, teleport.Value.X, teleport.Value.Y, clientW, clientH);
            if (!Wait(2500))
            {
                return Result.Cancelled;
            }

            if (MapOpen())
            {
                return CloseMap(input, "still on the map after pressing Teleport");
            }

            log("Teleported to the point nearest the quest");
            return Result.Teleported;
        }

        return CloseMap(input, "none of the nearby points could be teleported to");
    }

    /// <summary>
    /// Opens the big map with the tracked quest on screen and returns where the quest is.
    /// M opens the map on the player; if the quest is off screen, the map shows a round quest button at its
    /// edge (bottom right) which moves the map to the quest. Falls back to the quest menu (J → Navigate).
    /// </summary>
    private Point? OpenMapOnQuest(GameInput input, int clientW, int clientH, out bool cancelled)
    {
        cancelled = false;
        input.Key(VK_M);
        if (!WaitForMap(out cancelled) && !cancelled)
        {
            input.Key(VK_M);
            WaitForMap(out cancelled);
        }

        if (cancelled)
        {
            return null;
        }

        for (var attempt = 0; attempt < 2 && MapOpen(); attempt++)
        {
            using (var frame = Grab1080())
            {
                if (frame == null)
                {
                    return null;
                }

                using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
                if (MapVision.FindQuestButton(frame, grey) is not { } button)
                {
                    // No edge button: the quest is already on screen (or nothing is tracked).
                    if (MapVision.FindQuestOnMap(frame, grey, MapVision.Centre) is { } onScreen)
                    {
                        log("Quest is on the map screen");
                        return onScreen;
                    }

                    break;
                }

                log("Moving the map to the quest");
                Click(input, button.X, button.Y, clientW, clientH);
            }

            // The map glides over to the quest.
            if (!Wait(1600))
            {
                cancelled = true;
                return null;
            }

            using (var frame = Grab1080())
            {
                if (frame == null)
                {
                    return null;
                }

                using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
                if (MapVision.FindQuestOnMap(frame, grey, MapVision.Centre, maxDistance: 250) is { } quest)
                {
                    return quest;
                }

                if (MapVision.FindQuestButton(frame, grey) != null)
                {
                    NoteClickFailure(input); // the map didn't move; next attempt uses a real click if it can
                }
            }
        }

        // Fallback: quest menu → Navigate (or Track first), which also centres the map on the quest.
        log("Quest button not found on the map, trying the quest menu");
        BackToGame(input);
        input.Key(VK_J);
        if (!Wait(1200))
        {
            cancelled = true;
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Click(input, 1670, 1020, clientW, clientH);
            if (!Wait(1500))
            {
                cancelled = true;
                return null;
            }

            if (MapOpen())
            {
                using var frame = Grab1080();
                using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
                return frame != null && grey != null && MapVision.FindQuestOnMap(frame, grey, MapVision.Centre, maxDistance: 250) is { } quest
                    ? quest
                    : MapVision.Centre;
            }
        }

        NoteClickFailure(input);
        log("Teleport: couldn't get the map onto the quest, giving up");
        return null;
    }

    private bool WaitForMap(out bool cancelled)
    {
        cancelled = false;
        for (var i = 0; i < 6; i++)
        {
            if (!Wait(400))
            {
                cancelled = true;
                return false;
            }

            if (MapOpen())
            {
                // Let the opening animation finish.
                if (!Wait(500))
                {
                    cancelled = true;
                    return false;
                }

                return true;
            }
        }

        return false;
    }

    private bool MapOpen()
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null && _map.IsMapOpen(grey);
    }

    /// <summary>
    /// A side panel or pick list is open: the map's own close (X) button is hidden then.
    /// </summary>
    private bool PanelOpen()
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null && !_map.IsMapCloseVisible(grey);
    }

    private Point? FindTeleportButton(GameInput input, int clientW, int clientH)
    {
        using var frame = Grab1080();
        if (frame == null)
        {
            return null;
        }

        using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        if (_map.FindTeleportButton(grey) is { } button)
        {
            return button;
        }

        // Overlapping icons open a pick list first: choose its waypoint / statue / other teleport row.
        if (_list.Decide(frame, grey) is { Kind: WaypointAutoTeleport.Kind.ClickListRow } row)
        {
            Click(input, row.At.X, row.At.Y, clientW, clientH);
            if (!Wait(1000))
            {
                return null;
            }

            using var frame2 = Grab1080();
            using var grey2 = frame2?.CvtColor(ColorConversionCodes.BGR2GRAY);
            return grey2 == null ? null : _map.FindTeleportButton(grey2);
        }

        return null;
    }

    /// <summary>
    /// Posted click first (works in the background if the game accepts it). If the game is in front and
    /// posted clicks are known not to work, use a real click instead.
    /// </summary>
    private void Click(GameInput input, int x1080, int y1080, int clientW, int clientH)
    {
        var x = x1080 * clientW / Vision.Width;
        var y = y1080 * clientH / Vision.Height;
        if (_backgroundClicksFail && input.ForegroundClick(x, y))
        {
            _inputBaseline = Native.LastInputTick(); // our own click is not the player's
            return;
        }

        input.Click(x, y);
    }

    private void NoteClickFailure(GameInput input)
    {
        if (!_backgroundClicksFail)
        {
            _backgroundClicksFail = true;
            log(input.IsGameInFront
                ? "Map ignored background clicks; will use real clicks while the game is in front"
                : "Map ignored background clicks; teleport will only run while the game is in front");
        }
    }

    /// <summary>
    /// Waits, returning false if the user stopped StoryPilot or started using the game themselves.
    /// </summary>
    private bool Wait(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (!keepGoing() || UserTookOver())
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return keepGoing() && !UserTookOver();
    }

    /// <summary>
    /// The player switched to the game, or used keyboard/mouse while it is in front.
    /// Input in other windows (you working elsewhere) doesn't count.
    /// </summary>
    private bool UserTookOver()
    {
        var inFront = Native.GetForegroundWindow() == _hWnd;
        if (!inFront)
        {
            _inputBaseline = Native.LastInputTick();
            return false;
        }

        if (!_gameWasInFront)
        {
            return true;
        }

        return Native.LastInputTick() != _inputBaseline;
    }

    private Result Cancelled(GameInput input, bool mapOpen)
    {
        log("Teleport cancelled");
        // If the user took over, leave the screen to them; if StoryPilot was stopped, tidy up.
        if (!keepGoing() && !UserTookOver() && mapOpen)
        {
            BackToGame(input);
        }

        return Result.Cancelled;
    }

    private Result CloseMap(GameInput input, string why)
    {
        log($"Teleport: {why}; closing the map");
        BackToGame(input);
        return Result.GaveUp;
    }

    /// <summary>
    /// Presses Esc until the normal game view (Paimon icon) is back, at most three times.
    /// Never presses Esc on the normal view, since that would open the Paimon menu.
    /// </summary>
    private void BackToGame(GameInput input)
    {
        for (var i = 0; i < 3; i++)
        {
            using var frame = Grab1080();
            using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
            if (grey == null || _map.IsMainUi(grey))
            {
                return;
            }

            input.Key(Native.VK_ESCAPE);
            Thread.Sleep(700);
        }
    }

    private Mat? Grab1080()
    {
        var raw = grab();
        if (raw == null || raw.Empty())
        {
            raw?.Dispose();
            return null;
        }

        if (raw.Width == Vision.Width && raw.Height == Vision.Height)
        {
            return raw;
        }

        var resized = raw.Resize(new OpenCvSharp.Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        raw.Dispose();
        return resized;
    }

    public void Dispose()
    {
        _map.Dispose();
        _list.Dispose();
    }
}
