using System.Diagnostics;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;
using StoryPilot.Capture;

namespace StoryPilot.Engine;

/// <summary>
/// The story auto-play loop. A trimmed port of BetterGI's AutoSkipTrigger:
/// advance dialogue, pick options, click through black screens / skip buttons, close pop-ups.
/// Everything is done with screen templates + PostMessage input, so the game can sit
/// windowed behind other windows (not minimized).
/// </summary>
public sealed class StoryEngine : IDisposable
{
    private const double PostDialogueSeconds = 10;
    private const double NarrationSeconds = 20;

    private readonly Settings _settings;
    private readonly Template _talkUi;
    private readonly Template _optionIcon;
    private readonly Template _exclamationIcon;
    private readonly Template _chatPickF;
    private readonly Template _hangoutSkip;
    private readonly Template _pageClose;
    private readonly Template _guidingNotes;
    private readonly Template _chatHistory;
    private readonly Template _valiantChronicles;
    private readonly Template _paimonMenu;
    private readonly Template _mapScale;
    private readonly Template _mapSettings;

    private Thread? _thread;
    private volatile bool _running;
    private WgcCapture? _capture;
    private nint _hWnd;
    private GameInput? _input;

    private DateTime _lastTalk = DateTime.MinValue;
    private DateTime _lastOptionAction = DateTime.MinValue;
    private DateTime _optionSeenSince = DateTime.MinValue;
    private DateTime _lastClick = DateTime.MinValue;
    private DateTime _lastPopupAction = DateTime.MinValue;
    private int _pageCloseStreak;
    private bool _storyPendingTeleport;
    private DateTime _mainUiSince = DateTime.MinValue;
    private QuestTeleport? _teleport;
    private WaypointAutoTeleport? _autoTeleport;
    private volatile bool _mapOpen;
    private DateTime _nextAutoTeleport = DateTime.MinValue;

    public event Action<string>? Log;
    public event Action<string>? StatusChanged;

    public int Advanced { get; private set; }
    public int OptionsPicked { get; private set; }
    public int Skipped { get; private set; }

    public bool IsRunning => _running;

    public StoryEngine(Settings settings)
    {
        _settings = settings;
        // Regions are BetterGI's, evaluated for a 1920x1080 frame.
        _talkUi = new Template("disabled_ui.png", new Rect(0, 0, 640, 135));
        var optionArea = new Rect(960, 90, 640, 980);
        _optionIcon = new Template("icon_option.png", optionArea);
        _exclamationIcon = new Template("icon_exclamation.png", optionArea);
        _chatPickF = new Template("chat_pick_f.png", new Rect(1200, 350, 50, 510));
        _hangoutSkip = new Template("hangout_skip.png", new Rect(0, 0, 384, 135));
        _pageClose = new Template("page_close.png", new Rect(1680, 0, 240, 135));
        _guidingNotes = new Template("guiding_notes.png", new Rect(33, 4, 94, 95));
        _chatHistory = new Template("chat_history.png", new Rect(26, 3, 100, 93));
        _valiantChronicles = new Template("valiant_chronicles.png", new Rect(22, 7, 104, 86));
        _paimonMenu = new Template("paimon_menu.png", new Rect(0, 0, 480, 270));
        _mapScale = new Template("map_scale_button.png", new Rect(30, 440, 40, 200), blackIsTransparent: true);
        _mapSettings = new Template("map_settings_button.png", new Rect(25, 990, 58, 62));
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StoryPilot", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(5000);
        _thread = null;
        ReleaseGame();
        StatusChanged?.Invoke("Stopped");
    }

    private void Loop()
    {
        var sw = new Stopwatch();
        while (_running)
        {
            sw.Restart();
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log?.Invoke($"Error: {e.Message}");
                ReleaseGame();
                Thread.Sleep(1000);
            }

            // Check the map more often so a picked waypoint teleports without a noticeable wait.
            var tick = _mapOpen ? Math.Min(_settings.TickMs, 100) : _settings.TickMs;
            var wait = Math.Max(50, tick) - (int)sw.ElapsedMilliseconds;
            if (wait > 0)
            {
                Thread.Sleep(wait);
            }
        }
    }

    private void Tick()
    {
        if (!EnsureGame())
        {
            return;
        }

        if (Native.IsIconic(_hWnd))
        {
            StatusChanged?.Invoke("Game is minimized — restore it (it can stay behind other windows)");
            return;
        }

        using var raw = _capture!.Grab();
        if (raw == null || raw.Empty())
        {
            StatusChanged?.Invoke("Waiting for game frames…");
            return;
        }

        var clientW = raw.Width;
        var clientH = raw.Height;
        using var bgr = raw.Width == Vision.Width && raw.Height == Vision.Height
            ? raw.Clone()
            : raw.Resize(new Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        using var grey = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);

        _mapOpen = false;
        var talking = Vision.Find(grey, _talkUi) != null;
        StatusChanged?.Invoke(talking ? "In dialogue — skipping" : "Running — waiting for dialogue");

        if (talking)
        {
            _lastTalk = DateTime.Now;
            _storyPendingTeleport = true;
            _mainUiSince = DateTime.MinValue;
            OnDialogue(bgr, grey, clientW, clientH);
            return;
        }

        if (_settings.AutoTeleportOnWaypoint && TryAutoTeleport(bgr, grey, clientW, clientH))
        {
            return;
        }

        if (_settings.TeleportToNextQuest && TryTeleportAfterStory(grey, clientW, clientH))
        {
            return;
        }

        _optionSeenSince = DateTime.MinValue;
        if ((DateTime.Now - _lastTalk).TotalSeconds <= PostDialogueSeconds && _settings.ClosePopups)
        {
            if (HandlePostDialoguePopups(bgr, grey, clientW, clientH))
            {
                return;
            }
        }
        else
        {
            _pageCloseStreak = 0;
        }

        // Only right after a story StoryPilot was handling, so dark caves or your own play aren't clicked.
        if (_settings.SkipCutscenes && (DateTime.Now - _lastTalk).TotalSeconds <= NarrationSeconds)
        {
            ClickBlackScreen(grey);
        }
    }

    /// <summary>
    /// Once per story: shortly after the dialogue is over and the normal game view is back,
    /// teleport toward the next quest step if it is far away.
    /// </summary>
    private bool TryTeleportAfterStory(Mat grey, int clientW, int clientH)
    {
        if (!_storyPendingTeleport)
        {
            return false;
        }

        if ((DateTime.Now - _lastTalk).TotalSeconds > PostDialogueSeconds + 5)
        {
            _storyPendingTeleport = false; // story ended somewhere else (menu, cutscene); don't act later
            return false;
        }

        if (Vision.Find(grey, _paimonMenu) == null)
        {
            _mainUiSince = DateTime.MinValue;
            return false;
        }

        if (_mainUiSince == DateTime.MinValue)
        {
            _mainUiSince = DateTime.Now;
            return false;
        }

        // The quest text animates in; give it a moment before reading the distance.
        if ((DateTime.Now - _mainUiSince).TotalMilliseconds < 2000)
        {
            return false;
        }

        _storyPendingTeleport = false;
        StatusChanged?.Invoke("Checking the next quest step…");
        _teleport ??= new QuestTeleport(() => _capture?.Grab(), () => _running, line => Log?.Invoke(line));
        var result = _teleport.Run(_hWnd, _input!, _settings.TeleportMinDistanceMetres, clientW, clientH);
        if (result == QuestTeleport.Result.Teleported)
        {
            Skipped++;
        }

        return true;
    }

    /// <summary>
    /// On the big map, after you pick a waypoint / statue / domain: press its Teleport button
    /// (or first choose the teleport row in the pick list). Real clicks, only while the game is in front,
    /// since you're the one using the map; the mouse is put back afterwards.
    /// </summary>
    private bool TryAutoTeleport(Mat bgr, Mat grey, int clientW, int clientH)
    {
        _autoTeleport ??= new WaypointAutoTeleport();
        _mapOpen = _autoTeleport.IsMapOpen(grey);
        if (!_mapOpen || DateTime.Now < _nextAutoTeleport || !_input!.IsGameInFront)
        {
            return false;
        }

        if (_autoTeleport.Decide(bgr, grey) is not { } step)
        {
            return false;
        }

        // Let the panel / list finish sliding in, then make sure it is still there before clicking.
        Thread.Sleep(Math.Clamp(_settings.AutoTeleportDelayMs, 0, 2000));
        using (var again = GrabFrame1080())
        {
            if (again == null)
            {
                return false;
            }

            using var greyAgain = again.CvtColor(ColorConversionCodes.BGR2GRAY);
            if (_autoTeleport.Decide(again, greyAgain) is not { } confirmed || confirmed.Kind != step.Kind)
            {
                return false;
            }

            step = confirmed;
        }

        if (!_input.ForegroundClick(step.At.X * clientW / Vision.Width, step.At.Y * clientH / Vision.Height))
        {
            return false;
        }

        if (step.Kind == WaypointAutoTeleport.Kind.ClickTeleport)
        {
            _nextAutoTeleport = DateTime.Now.AddMilliseconds(1500);
            Log?.Invoke("Auto-teleport: pressed Teleport");
        }
        else
        {
            _nextAutoTeleport = DateTime.Now.AddMilliseconds(300);
            Log?.Invoke($"Auto-teleport: chose the {step.What} in the list");
        }

        return true;
    }

    private Mat? GrabFrame1080()
    {
        var raw = _capture?.Grab();
        if (raw == null || raw.Empty())
        {
            raw?.Dispose();
            return null;
        }

        if (raw.Width == Vision.Width && raw.Height == Vision.Height)
        {
            return raw;
        }

        var resized = raw.Resize(new Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        raw.Dispose();
        return resized;
    }

    private void OnDialogue(Mat bgr, Mat grey, int clientW, int clientH)
    {
        if (_settings.AdvanceDialogue)
        {
            _input!.Key(Native.VK_SPACE);
            Advanced++;
        }

        var optionHandled = _settings.PickOptions && TryPickOption(bgr, grey);
        if (!optionHandled && _settings.SkipCutscenes)
        {
            var skip = Vision.Find(grey, _hangoutSkip);
            if (skip != null && (DateTime.Now - _lastClick).TotalMilliseconds > 1000)
            {
                ClickFrame(skip.Value.X + skip.Value.Width / 2, skip.Value.Y + skip.Value.Height / 2, clientW, clientH);
                _lastClick = DateTime.Now;
                Skipped++;
                Log?.Invoke("Pressed Skip");
            }
        }
    }

    /// <summary>
    /// Picks an option with the keyboard (F selects the highlighted option, S moves down),
    /// which works in the background without touching the mouse.
    /// </summary>
    private bool TryPickOption(Mat bgr, Mat grey)
    {
        var options = Vision.FindAll(grey, _optionIcon)
            .Select(r => (Rect: r, Quest: false))
            .Concat(Vision.FindAll(grey, _exclamationIcon).Select(r => (Rect: r, Quest: true)))
            .OrderBy(o => o.Rect.Y)
            .ToList();
        // Drop near-duplicates where both icon templates hit the same row.
        for (var i = options.Count - 1; i > 0; i--)
        {
            if (Math.Abs(options[i].Rect.Y - options[i - 1].Rect.Y) < 20)
            {
                options.RemoveAt(options[i].Quest ? i - 1 : i);
            }
        }

        if (options.Count == 0)
        {
            _optionSeenSince = DateTime.MinValue;
            // Single "F" prompt in dialogue without an option bubble.
            if (Vision.Find(grey, _chatPickF) != null && (DateTime.Now - _lastOptionAction).TotalMilliseconds > 800)
            {
                _input!.Key(Native.VK_F);
                _lastOptionAction = DateTime.Now;
                return true;
            }

            return false;
        }

        if (_optionSeenSince == DateTime.MinValue)
        {
            _optionSeenSince = DateTime.Now;
        }

        if ((DateTime.Now - _optionSeenSince).TotalMilliseconds < _settings.OptionDelayMs
            || (DateTime.Now - _lastOptionAction).TotalMilliseconds < 1000)
        {
            return true;
        }

        var index = _settings.DefaultOption == OptionChoice.First ? 0 : options.Count - 1;
        var why = _settings.DefaultOption == OptionChoice.First ? "first" : "last";
        if (_settings.PreferQuestOptions)
        {
            var quest = options.FindIndex(o => o.Quest);
            if (quest < 0)
            {
                quest = options.FindIndex(o => Vision.OrangeRatio(bgr, TextArea(o.Rect)) > 0.06);
            }

            if (quest >= 0)
            {
                index = quest;
                why = "quest";
            }
        }

        if (index == options.Count - 1 && index > 0)
        {
            // W from the top wraps to the last option.
            _input!.Key(Native.VK_W);
            Thread.Sleep(100);
        }
        else
        {
            for (var i = 0; i < index; i++)
            {
                _input!.Key(Native.VK_S);
                Thread.Sleep(100);
            }
        }

        _input!.Key(Native.VK_F);
        _lastOptionAction = DateTime.Now;
        _optionSeenSince = DateTime.MinValue;
        OptionsPicked++;
        Log?.Invoke($"Picked option {index + 1} of {options.Count} ({why})");
        return true;
    }

    private static Rect TextArea(Rect icon) => new(icon.X + icon.Width + 8, icon.Y, 535, icon.Height);

    private bool HandlePostDialoguePopups(Mat bgr, Mat grey, int clientW, int clientH)
    {
        if ((DateTime.Now - _lastPopupAction).TotalMilliseconds < 1000)
        {
            return false;
        }

        var inMainUi = Vision.Find(grey, _paimonMenu) != null;
        var inMap = Vision.Find(grey, _mapScale) != null || Vision.Find(grey, _mapSettings) != null;
        if (inMainUi || inMap)
        {
            _pageCloseStreak = 0;
            return false;
        }

        // Item / tutorial pop-up with the "click to continue" triangle.
        var triangle = Vision.FindBottomTriangle(bgr);
        if (triangle != null)
        {
            ClickFrame(triangle.Value.X, triangle.Value.Y, clientW, clientH);
            _lastPopupAction = DateTime.Now;
            _lastTalk = DateTime.Now; // still part of the story
            Log?.Invoke("Closed pop-up (continue)");
            return true;
        }

        // Full-screen page with an X in the corner (BetterGI waits ~200 ms for it to settle).
        if (Vision.Find(grey, _pageClose) == null)
        {
            _pageCloseStreak = 0;
            return false;
        }

        if (++_pageCloseStreak < 2)
        {
            return false;
        }

        var userOpenedPage = Vision.Find(grey, _guidingNotes) != null
                             || Vision.Find(grey, _chatHistory) != null
                             || Vision.Find(grey, _valiantChronicles) != null;
        if (userOpenedPage)
        {
            return false;
        }

        _input!.Key(Native.VK_ESCAPE);
        _pageCloseStreak = 0;
        _lastPopupAction = DateTime.Now;
        Log?.Invoke("Closed pop-up page");
        return true;
    }

    private void ClickBlackScreen(Mat grey)
    {
        if ((DateTime.Now - _lastClick).TotalMilliseconds < 1200)
        {
            return;
        }

        // Narration: a mostly black screen with a line of white text in the middle.
        if (Vision.IsNarrationScreen(grey) is not { } rate)
        {
            return;
        }

        _input!.Click(16, 16);
        _lastClick = DateTime.Now;
        _lastTalk = DateTime.Now; // narration is part of the story; keep the window open
        Skipped++;
        Log?.Invoke($"Clicked through narration ({rate:P0} black)");
    }

    private void ClickFrame(int x, int y, int clientW, int clientH)
    {
        _input!.Click(x * clientW / Vision.Width, y * clientH / Vision.Height);
    }

    private bool EnsureGame()
    {
        if (_hWnd != 0 && Native.IsWindow(_hWnd) && _capture is { IsCapturing: true })
        {
            return true;
        }

        ReleaseGame();
        var hWnd = FindGameWindow();
        if (hWnd == 0)
        {
            StatusChanged?.Invoke("Waiting for Genshin Impact to start…");
            Thread.Sleep(1500);
            return false;
        }

        _capture = new WgcCapture();
        _capture.Start(hWnd);
        _hWnd = hWnd;
        _input = new GameInput(hWnd);
        Native.GetClientRect(hWnd, out var rc);
        Log?.Invoke($"Attached to game window ({rc.Width}x{rc.Height})");
        if (Math.Abs(rc.Width * 9 - rc.Height * 16) > rc.Width / 20)
        {
            Log?.Invoke("Note: the game window isn't 16:9; detection works best at 16:9 (e.g. 1600x900 or 1920x1080).");
        }

        return true;
    }

    private static nint FindGameWindow()
    {
        foreach (var name in new[] { "GenshinImpact", "YuanShen" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.MainWindowHandle != 0)
                    {
                        return p.MainWindowHandle;
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return 0;
    }

    private void ReleaseGame()
    {
        _capture?.Dispose();
        _capture = null;
        _hWnd = 0;
        _input = null;
    }

    /// <summary>
    /// Saves the current game frame to the Pictures folder (to report a screen it doesn't handle).
    /// </summary>
    public string? SaveFrame()
    {
        using var frame = _capture?.Grab();
        if (frame == null || frame.Empty())
        {
            return null;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "StoryPilot");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"frame_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        frame.SaveImage(path);
        return path;
    }

    public void Dispose()
    {
        Stop();
        _teleport?.Dispose();
        _autoTeleport?.Dispose();
        foreach (var t in new[] { _talkUi, _optionIcon, _exclamationIcon, _chatPickF, _hangoutSkip, _pageClose, _guidingNotes, _chatHistory, _valiantChronicles, _paimonMenu, _mapScale, _mapSettings })
        {
            t.Dispose();
        }
    }
}
