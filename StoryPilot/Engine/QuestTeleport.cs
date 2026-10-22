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

    private readonly MapVision _map = new();

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

        if (metres == null)
        {
            log("Teleport: couldn't read the quest distance, skipping");
            return Result.NotNeeded;
        }

        if (metres < minDistanceMetres)
        {
            log($"Quest is {metres} m away — close enough, no teleport");
            return Result.NotNeeded;
        }

        log($"Quest is {metres} m away — looking for a waypoint");
        input.Key(VK_J);
        if (!Wait(1200))
        {
            return Cancelled(input, mapOpen: false);
        }

        // Quest menu: the bottom-right button is "Navigate" (or "Track" first if the quest isn't tracked).
        var opened = false;
        for (var attempt = 0; attempt < 2 && !opened; attempt++)
        {
            Click(input, 1670, 1020, clientW, clientH);
            if (!Wait(1500))
            {
                return Cancelled(input, mapOpen: true);
            }

            using var frame = Grab1080();
            using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
            opened = grey != null && _map.IsMapOpen(grey);
        }

        if (!opened)
        {
            NoteClickFailure(input);
            log("Teleport: the map didn't open from the quest menu, giving up");
            BackToGame(input);
            return Result.GaveUp;
        }

        Point? target;
        using (var frame = Grab1080())
        {
            if (frame == null)
            {
                return CloseMap(input, "no frame");
            }

            using var grey = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            var waypoints = _map.FindUnlockedWaypoints(frame, grey);
            var player = MapVision.FindPlayer(frame);
            target = MapVision.ChooseWaypoint(waypoints, MapVision.Centre, player);
            if (target == null)
            {
                return CloseMap(input, waypoints.Count == 0
                    ? "no unlocked waypoint near the quest"
                    : "no waypoint is much closer than you already are");
            }
        }

        Click(input, target.Value.X, target.Value.Y, clientW, clientH);
        if (!Wait(1000))
        {
            return Cancelled(input, mapOpen: true);
        }

        var teleport = FindTeleportButton(input, clientW, clientH);
        if (teleport == null)
        {
            NoteClickFailure(input);
            return CloseMap(input, "the waypoint's Teleport button didn't appear");
        }

        Click(input, teleport.Value.X, teleport.Value.Y, clientW, clientH);
        if (!Wait(2500))
        {
            return Result.Cancelled;
        }

        using (var frame = Grab1080())
        using (var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY))
        {
            if (grey != null && _map.IsMapOpen(grey))
            {
                return CloseMap(input, "still on the map after pressing Teleport");
            }
        }

        log("Teleported to the waypoint nearest the quest");
        return Result.Teleported;
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

        // Overlapping icons open a pick list first.
        if (_map.FindWaypointInList(grey) is { } row)
        {
            Click(input, row.X + 60, row.Y, clientW, clientH);
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

    public void Dispose() => _map.Dispose();
}
