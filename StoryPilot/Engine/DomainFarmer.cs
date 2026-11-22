using OpenCvSharp;
using Rect = OpenCvSharp.Rect;

namespace StoryPilot.Engine;

/// <summary>
/// Domain farming, step 1 ("assisted"): StoryPilot enters the domain, walks to the key and starts it,
/// you fight, then it finds the reward tree, walks to it, opens the reward pop-up and presses
/// Continue Challenge, round after round. Same order as BetterGI's AutoDomainTask, without OCR.
///
/// Real input (SendInput), so the game must stay the window in front. While StoryPilot is moving,
/// touching the keyboard or mouse stops it. During the fight (and while you pick the resin, until the
/// resin pop-up is automated) your input is expected and ignored.
/// </summary>
public sealed class DomainFarmer : IDisposable
{
    private const int Middle = Vision.Width / 2;

    /// <summary>Close enough to the screen centre to walk straight at the tree (frame px).</summary>
    private const int TreeCentredPx = 60;

    private static readonly TimeSpan WalkToKeyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FightTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FindTreeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WalkToTreeTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ResinPickTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<Mat?> _grab;
    private readonly Func<bool> _keepGoing;
    private readonly Action<string> _log;
    private readonly Action<string> _status;

    /// <summary>Yellow-circle button icon: Single Player Challenge, Start Challenge, Continue Challenge (BetterGI AutoFight/confirm.png).</summary>
    private readonly Template _confirm = new("domain_confirm.png", new Rect(960, 540, 960, 540), 0.8, useAlpha: true);

    /// <summary>Blue-cross button icon: Exit Domain (BetterGI AutoFight/exit.png).</summary>
    private readonly Template _confirmAnywhere = new("domain_confirm.png", new Rect(0, 0, 1920, 1080), 0.8, useAlpha: true);

    private readonly Template _exit = new("domain_exit.png", new Rect(0, 540, 960, 540), 0.8, useAlpha: true);

    /// <summary>Top-left Leave Domain icon shown inside a domain instead of the Paimon menu (cut from your frame).</summary>
    private readonly Template _inDomain = new("in_domain.png", new Rect(0, 0, 480, 270), 0.75);

    /// <summary>"Click anywhere to close" under the Ley Line Disorder note shown on arrival (from your screenshot).</summary>
    private readonly Template _clickToClose = new("domain_click_to_close.png", new Rect(700, 620, 520, 160), 0.8);

    private readonly Template _pickF = new("chat_pick_f.png", new Rect(1090, 330, 60, 420));

    /// <summary>"Challenge Completed" banner, made from your screenshot; until then you press Ctrl+Alt+D after the fight.</summary>
    // The challenge has started: the "Defeat N opponent(s) within ... second(s)" banner, or the stopwatch over the timer at the top.
    private readonly Template? _defeatBanner = Optional("domain_defeat.png", new Rect(300, 180, 1320, 170), 0.8);
    private readonly Template? _timer = Optional("domain_timer.png", new Rect(720, 0, 480, 135), 0.7, useAlpha: true);
    private readonly Template? _completed = Optional("domain_challenge_completed.png", new Rect(480, 135, 960, 405), 0.75);

    /// <summary>Resin icons on the left of the Petrified Tree pop-up rows (from your frame).</summary>
    private readonly Template _condensedRow = new("resin_condensed_row.png", new Rect(540, 420, 200, 420), 0.85);

    private readonly Template _originalRow = new("resin_original_row.png", new Rect(540, 420, 200, 420), 0.85);

    /// <summary>Centre of the Use buttons in that pop-up.</summary>
    private const int UseButtonX = 1266;

    /// <summary>The swap button left of the original resin row's Use button (40 / 20 original resin).</summary>
    private const int SwapButtonX = 1152;

    private readonly HashSet<int> _held = [];
    private TreeDetector? _tree;
    private bool _treeLoadFailed;
    private nint _hWnd;
    private GameInput? _input;
    private uint _inputBaseline;
    private volatile bool _fightDone;
    private List<CombatStep>? _rotation;
    private bool _useOriginal = true;

    /// <summary>Condensed resin icon in the top bar of the tree pop-up and the reward screen (from your frames).</summary>
    private readonly Template _condensedTop = new("resin_condensed_top.png", new Rect(900, 0, 900, 100), 0.85);

    // "/ 200" after the original resin count on the reward screen's top bar. Fragile Resin (the first
    // moon) is never read or used: it is kept for later.
    private readonly Template _originalMax = new("resin_original_max.png", new Rect(900, 0, 900, 100), 0.85);

    /// <summary>Count digits next to it. Condensed resin goes up to 5; 3 has no sample yet (reads as unknown).</summary>
    private readonly (int Digit, Template T)[] _digits = new[] { 0, 1, 2, 4, 5 }
        .Select(d => (d, new Template($"resin_digit_{d}.png", new Rect(0, 0, 1, 1), 0.85))).ToArray();

    public enum Phase
    {
        Idle,
        Entering,
        WalkingToKey,
        Fighting,
        FindingTree,
        Claiming,
    }

    public volatile Phase Current = Phase.Idle;

    public int Rounds { get; private set; }

    public DomainFarmer(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log, Action<string> status)
    {
        _grab = grab;
        _keepGoing = keepGoing;
        _log = log;
        _status = status;
    }

    /// <summary>Ctrl+Alt+D during the fight: the challenge is complete, go to the tree.</summary>
    public void FightDone() => _fightDone = true;

    /// <summary>
    /// Start at the domain door (F prompt showing) or anywhere inside the domain before the key.
    /// Runs until stopped, until something can't be found, or until you take over.
    /// </summary>
    public void Run(nint hWnd, GameInput input, bool autoFight, string rotation, bool useOriginalResin)
    {
        _useOriginal = useOriginalResin;
        _hWnd = hWnd;
        _input = input;
        _rotation = null;
        if (autoFight)
        {
            try
            {
                _rotation = DomainCombat.Parse(rotation);
            }
            catch (FormatException e)
            {
                _log($"Domain: {e.Message}");
                return;
            }
        }

        Rounds = 0;
        if (!input.IsGameInFront)
        {
            _log("Domain: the game must be the window in front");
            return;
        }

        WaitForHotkeyRelease();
        try
        {
            var ready = Has(_clickToClose) ? ArriveInDomain() : InsideDomain() || Enter();
            if (!ready)
            {
                return;
            }

            while (_keepGoing())
            {
                if (!WalkToKey() || !Fight() || !FindTree() || !WalkToTree())
                {
                    return;
                }

                var next = ClaimAndContinue();
                if (!next)
                {
                    return;
                }
            }
        }
        finally
        {
            ReleaseAll();
            Current = Phase.Idle;
        }
    }

    // ----- 1. Enter -----

    private bool Enter()
    {
        Current = Phase.Entering;
        _status("Domain: entering");
        if (!WaitFor(() => Has(_pickF), TimeSpan.FromSeconds(3)))
        {
            _log("Domain: stand at the domain door (F prompt showing) or inside the domain, then press Ctrl+Alt+D");
            return false;
        }

        // Snezhnaya doors show a short list of choices on F; if the domain screen doesn't open, try the next one.
        var opened = false;
        for (var attempt = 0; attempt < 4 && !opened; attempt++)
        {
            Tap(Native.VK_F);
            opened = WaitFor(() => Has(_confirm), TimeSpan.FromSeconds(attempt == 0 ? 8 : 5));
            if (!opened && Has(_pickF))
            {
                _log("Domain: the door has several choices; trying the next one");
                Native.SendWheel(-1);
                Rebaseline();
                Sleep(300);
            }
        }

        if (!opened)
        {
            _log("Domain: the domain screen didn't open");
            return false;
        }

        // Single Player Challenge, then Start Challenge on the party screen. Both carry the yellow-circle icon.
        if (!ClickConfirm("Single Player Challenge") || !ClickConfirm("Start Challenge"))
        {
            return false;
        }

        _status("Domain: loading");
        if (!ArriveInDomain())
        {
            _log("Domain: didn't see the domain after loading");
            return false;
        }

        return true;
    }

    /// <summary>
    /// After loading: close the Ley Line Disorder note ("Click anywhere to close") if it shows,
    /// then wait for the normal in-domain view.
    /// </summary>
    private bool ArriveInDomain()
    {
        var until = DateTime.Now.AddSeconds(90); // slow loads happen
        var closed = false;
        while (DateTime.Now < until)
        {
            if (!_keepGoing() || UserTookOver())
            {
                return false;
            }

            using var frame = Grab1080();
            using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
            if (grey != null)
            {
                if (Vision.Find(grey, _clickToClose) != null)
                {
                    _log("Domain: closing the Ley Line Disorder note");
                    ClickFrame(Middle, 900);
                    closed = true;
                    Sleep(800);
                    continue;
                }

                if (Vision.Find(grey, _inDomain) != null && Vision.Find(grey, _confirm) == null)
                {
                    // The note can open a moment after the view appears; give it a second before walking.
                    if (closed || !Sleep(1200))
                    {
                        return _keepGoing();
                    }

                    using var again = Grab1080();
                    using var greyAgain = again?.CvtColor(ColorConversionCodes.BGR2GRAY);
                    if (greyAgain != null && Vision.Find(greyAgain, _clickToClose) == null)
                    {
                        return true;
                    }

                    continue;
                }
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>Wait for the yellow-circle button, click it, and wait for that screen to go away.</summary>
    private bool ClickConfirm(string what)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            // Screens can take a while to load; wait longer the first time.
            Rect? button = null;
            if (!WaitFor(() => (button = Find(_confirm)) != null, TimeSpan.FromSeconds(attempt == 1 ? 20 : 5)))
            {
                if (attempt == 1)
                {
                    _log($"Domain: no {what} button");
                    return false;
                }

                // It went away after all: the last click worked.
                return Sleep(500);
            }

            Sleep(Vary(300));
            ClickFrame(button!.Value.X + button.Value.Width / 2, button.Value.Y + button.Value.Height / 2);
            _log(attempt == 1 ? $"Domain: {what}" : $"Domain: {what} (try {attempt})");
            // Done once the button disappears while the next screen loads; otherwise click again.
            if (WaitFor(() => !Has(_confirm), TimeSpan.FromSeconds(4)))
            {
                return Sleep(500);
            }
        }

        _log($"Domain: {what} didn't respond after 4 clicks");
        return false;
    }

    // ----- 2. Walk to the key -----

    /// <summary>
    /// The key is straight ahead of where every domain puts you. Sprint the first stretch only, then walk
    /// the rest: a sprint or a dash carries the character past the key and swallows the F press.
    /// </summary>
    private bool WalkToKey()
    {
        Current = Phase.WalkingToKey;
        _status($"Domain round {Rounds + 1}: walking to the key{(_resinCounts == null ? "" : $" ({_resinCounts})")}");
        Hold(Native.VK_W, true);
        var sprint = StartSprint();
        var sprintUntil = DateTime.Now.AddMilliseconds(Rng.Next(1200, 2000));
        var sprinting = true;
        var found = WaitFor(() =>
        {
            if (Has(_pickF))
            {
                return true;
            }

            if (sprinting && DateTime.Now >= sprintUntil)
            {
                sprinting = false;
                Hold(Native.VK_SHIFT, false);
            }

            if (sprinting)
            {
                sprint();
            }

            return false;
        }, WalkToKeyTimeout);
        Hold(Native.VK_SHIFT, false);
        Hold(Native.VK_W, false);
        if (!found)
        {
            _log("Domain: didn't reach the key within 60 s");
            return false;
        }

        if (!Interact("the key", KeyStarted, settle: sprinting ? 450 : 80))
        {
            return false;
        }

        _log($"Domain round {Rounds + 1}: started");
        return true;
    }

    /// <summary>
    /// After F at the key: the challenge started once the "Defeat ..." banner or the timer shows up. If neither
    /// shows, the F prompt being gone isn't enough (the run may just have carried past the key): step back,
    /// and if the prompt is there again, the key wasn't used.
    /// </summary>
    private bool KeyStarted()
    {
        if (WaitFor(ChallengeRunning, TimeSpan.FromSeconds(4)))
        {
            return true;
        }

        if (Has(_pickF))
        {
            return false;
        }

        if (_defeatBanner == null && _timer == null)
        {
            return true; // no templates: the prompt staying gone is all there is to go on
        }

        // Neither banner nor timer, prompt gone: check whether the key is just behind us.
        return !StepBackToPrompt();
    }

    private bool ChallengeRunning()
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null
            && ((_defeatBanner != null && Vision.Find(grey, _defeatBanner) != null)
                || (_timer != null && Vision.Find(grey, _timer) != null));
    }

    /// <summary>
    /// Sprint one of two ways, picked at random per run: hold Shift, or keep dashing with right clicks
    /// while holding W. Returns a tick to call in the walk loop (it does the dashes).
    /// </summary>
    private Action StartSprint()
    {
        Sleep(Rng.Next(80, 300));
        if (Rng.Next(2) == 0)
        {
            Hold(Native.VK_SHIFT, true);
            return () => { };
        }

        var nextDash = DateTime.Now;
        return () =>
        {
            if (DateTime.Now < nextDash)
            {
                return;
            }

            Native.SendRightClick(Rng.Next(35, 80));
            Rebaseline();
            nextDash = DateTime.Now.AddMilliseconds(Rng.Next(650, 1100));
        };
    }

    // ----- 3. Fight (yours, for now) -----

    private bool Fight()
    {
        Current = Phase.Fighting;
        _fightDone = false;
        var auto = _rotation != null;
        _status(auto
            ? "Domain: fighting until Challenge Completed (Ctrl+Alt+D ends the fight early)"
            : _completed == null
                ? "Domain: your turn to fight. Press Ctrl+Alt+D when the challenge is complete."
                : "Domain: your turn to fight. StoryPilot carries on after \"Challenge Completed\" (or Ctrl+Alt+D).");
        var started = DateTime.Now;
        var timedOut = true;
        var step = 0;
        _bannerSeen = false;
        while (DateTime.Now - started < FightTimeout)
        {
            if (!_keepGoing())
            {
                return false;
            }

            if (_fightDone || BannerSeen())
            {
                timedOut = false;
                break;
            }

            if (!auto)
            {
                Thread.Sleep(500);
                continue;
            }

            if (!WaitUntilGameInFront())
            {
                return false;
            }

            // Still standing next to an unused key (the start didn't take): press F before fighting on.
            if (Has(_pickF))
            {
                _log("Domain: the key is still waiting; pressing F");
                Tap(Native.VK_F);
                Sleep(Vary(600));
            }

            RunStep(_rotation![step]);
            if (Rng.NextDouble() < 0.3)
            {
                // A little camera drift now and then, as a hand on the mouse would.
                Native.MoveMouse(Rng.Next(-60, 61), Rng.Next(-8, 9));
                Rebaseline();
            }

            step = (step + 1) % _rotation.Count;
        }

        ReleaseCombatInput();
        if (timedOut)
        {
            _log("Domain: fight took over 10 minutes; stopping");
            return false;
        }

        _log($"Domain: fight over after {(DateTime.Now - started).TotalSeconds:F0} s");
        if (!WaitForHotkeyRelease())
        {
            return false;
        }

        // Let the banner and drops settle and the tree grow.
        return Sleep(2500);
    }

    /// <summary>Stops the fight when it is over, the app is stopped, or you pressed Ctrl+Alt+D.</summary>
    private bool FightInterrupted() => _fightDone || !_keepGoing() || Native.GetForegroundWindow() != _hWnd || BannerSeen();

    private DateTime _lastBannerCheck = DateTime.MinValue;
    private bool _bannerSeen;

    /// <summary>"Challenge Completed" on screen (checked at most twice a second, also mid-skill).</summary>
    private bool BannerSeen()
    {
        if (_completed == null || _bannerSeen)
        {
            return _bannerSeen;
        }

        if ((DateTime.Now - _lastBannerCheck).TotalMilliseconds < 500)
        {
            return false;
        }

        _lastBannerCheck = DateTime.Now;
        _bannerSeen = Has(_completed);
        if (_bannerSeen)
        {
            _log("Domain: Challenge Completed");
        }

        return _bannerSeen;
    }

    /// <summary>Real input goes to whichever window is in front, so pause while the game isn't.</summary>
    private bool WaitUntilGameInFront()
    {
        var warned = false;
        while (Native.GetForegroundWindow() != _hWnd)
        {
            if (!_keepGoing() || _fightDone)
            {
                return _keepGoing();
            }

            if (!warned)
            {
                _status("Domain: paused, bring the game to the front to keep fighting");
                warned = true;
            }

            Thread.Sleep(200);
        }

        if (warned)
        {
            _status("Domain: fighting until Challenge Completed (Ctrl+Alt+D ends the fight early)");
        }

        return true;
    }

    private void RunStep(CombatStep step)
    {
        if (!SwitchTo(step.Slot))
        {
            return;
        }

        foreach (var a in step.Actions)
        {
            if (FightInterrupted())
            {
                return;
            }

            RunAction(a);
        }
    }

    /// <summary>
    /// Press the slot's number key until that character is the active one. The game ignores a switch for
    /// about a second after the last one, so a single press can be lost after a quick skill.
    /// The active slot's number box on the right turns grey.
    /// </summary>
    private bool SwitchTo(int slot)
    {
        if (ActiveSlot() == slot)
        {
            return true;
        }

        var until = DateTime.Now.AddSeconds(3);
        while (DateTime.Now < until)
        {
            Tap(0x30 + slot);
            for (var i = 0; i < 5; i++)
            {
                if (!CombatSleep(Vary(100)))
                {
                    return false;
                }

                if (ActiveSlot() == slot)
                {
                    return CombatSleep(Vary(150));
                }
            }
        }

        _log($"Domain: couldn't switch to slot {slot}");
        return !FightInterrupted();
    }

    /// <summary>Slot (1-4) whose number box is grey, or 0 when it can't tell.</summary>
    private int ActiveSlot()
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey == null ? 0 : ActiveSlotOf(grey);
    }

    internal static int ActiveSlotOf(Mat grey)
    {
        int[] ys = [276, 367, 458, 549];
        var means = new double[ys.Length];
        for (var i = 0; i < ys.Length; i++)
        {
            using var box = new Mat(grey, new Rect(1860, ys[i] - 12, 24, 24));
            means[i] = Cv2.Mean(box).Val0;
        }

        var min = means.Min();
        var index = Array.IndexOf(means, min);
        var others = means.Where((_, i) => i != index).Min();
        return others - min >= 12 ? index + 1 : 0;
    }

    private void RunAction(CombatAction a)
    {
        var ms = (int)(a.Seconds * 1000);
        switch (a.Name)
        {
            case "e" when a.Hold:
                Native.SendKey(0x45, false);
                CombatSleep(Vary(ms > 0 ? ms : 900));
                Native.SendKey(0x45, true);
                CombatSleep(Vary(300));
                break;
            case "e":
                Tap(0x45);
                CombatSleep(Vary(550));
                break;
            case "q":
                // A burst pressed while the skill animation is still playing is dropped (Bennett E then Q),
                // so press again shortly after. A second press after the burst went off does nothing.
                Tap(0x51);
                CombatSleep(Vary(300));
                Tap(0x51);
                CombatSleep(Vary(1100)); // burst animation
                break;
            case "attack" when a.Seconds <= 0:
                Native.SendLeftButton(true);
                Thread.Sleep(Rng.Next(35, 75));
                Native.SendLeftButton(false);
                CombatSleep(Vary(250));
                break;
            case "attack":
            {
                var until = DateTime.Now.AddMilliseconds(Math.Max(ms, 200));
                while (DateTime.Now < until && !FightInterrupted())
                {
                    Native.SendLeftButton(true);
                    Thread.Sleep(Rng.Next(35, 75));
                    Native.SendLeftButton(false);
                    Thread.Sleep(Rng.Next(110, 200));
                }

                break;
            }

            case "charge":
                Native.SendLeftButton(true);
                CombatSleep(Vary(ms > 0 ? ms : 1000));
                Native.SendLeftButton(false);
                CombatSleep(Vary(200));
                break;
            case "wait":
                CombatSleep(Vary(ms));
                break;
            case "dash":
                Tap(Native.VK_SHIFT, 80);
                CombatSleep(Vary(300));
                break;
            case "jump":
                Tap(Native.VK_SPACE);
                CombatSleep(Vary(500));
                break;
            case "w" or "a" or "s" or "d":
            {
                var vk = a.Name[0] - 'a' + 0x41;
                Native.SendKey(vk, false);
                CombatSleep(Vary(ms > 0 ? ms : 300));
                Native.SendKey(vk, true);
                break;
            }
        }

        Rebaseline();
    }

    private static readonly Random Rng = Random.Shared;

    /// <summary>A delay with human spread: 85-125 % of the base plus up to 60 ms.</summary>
    internal static int Vary(int ms) => ms <= 0 ? 0 : (int)(ms * (0.85 + Rng.NextDouble() * 0.4)) + Rng.Next(0, 61);

    private bool CombatSleep(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (FightInterrupted())
            {
                return false;
            }

            Thread.Sleep(20);
        }

        return true;
    }

    /// <summary>Make sure nothing stays pressed after the fight.</summary>
    private void ReleaseCombatInput()
    {
        Native.SendLeftButton(false);
        foreach (var vk in new[] { 0x45, Native.VK_W, Native.VK_A, Native.VK_S, Native.VK_D, Native.VK_SHIFT })
        {
            Native.SendKey(vk, true);
        }

        Rebaseline();
    }

    // ----- 4. Find the tree -----

    /// <summary>
    /// Get off Mavuika's bike (switch to a random other character), then turn the camera until the tree model sees the tree in the middle of the screen. Turning instead of
    /// strafing: side-steps overshoot on the bike and the character ends up swaying left and right.
    /// </summary>
    private bool FindTree(bool dismount = true)
    {
        Current = Phase.FindingTree;
        _status("Domain: looking for the reward tree");
        var tree = LoadTree();
        if (tree == null)
        {
            return false;
        }

        if (dismount)
        {
            Dismount();
        }

        // Look straight ahead first: the tree is often already in view, and the camera stays where it is.
        // Only when it isn't seen for a moment does StoryPilot start turning to look for it.
        var started = DateTime.Now;
        var scans = 0;
        var centred = 0;
        while (DateTime.Now - started < FindTreeTimeout)
        {
            if (!_keepGoing() || UserTookOver())
            {
                _log("Domain: stopped while looking for the tree");
                return false;
            }

            // Already standing at the tree (the fight often ends right next to it): nothing to look for.
            if (Has(_pickF))
            {
                _log("Domain: already at the tree");
                return true;
            }

            var offset = TreeOffset(tree);
            if (offset == null && DateTime.Now - started < TimeSpan.FromSeconds(1.5))
            {
                Sleep(100);
                continue;
            }

            if (offset == null)
            {
                centred = 0;
                if (++scans > 24)
                {
                    break;
                }

                // Look around: about 30 degrees per step, a full turn in a dozen steps.
                Native.MoveMouse(ScanTurnPx + Rng.Next(-40, 41), Rng.Next(-3, 4));
                Rebaseline();
                Sleep(300);
                continue;
            }

            if (Math.Abs(offset.Value) <= TreeCentredPx)
            {
                if (++centred >= 2)
                {
                    _log("Domain: tree ahead");
                    return true;
                }

                Sleep(100);
                continue;
            }

            centred = 0;
            Native.MoveMouse(Math.Clamp((int)(offset.Value * TurnGain), -300, 300), 0);
            Rebaseline();
            Sleep(200);
        }

        _log("Domain: couldn't find the reward tree");
        return false;
    }

    /// <summary>Mouse px per scan step while looking for the tree.</summary>
    private const int ScanTurnPx = 250;

    /// <summary>Mouse px per screen px of tree offset (turns a bit less than needed, then re-checks).</summary>
    private const double TurnGain = 0.5;

    /// <summary>Tree centre minus screen centre (frame px), or null when no tree is visible.</summary>
    private int? TreeOffset(TreeDetector tree)
    {
        var box = TreeBox(tree);
        return box == null ? null : box.Value.X + box.Value.Width / 2 - Middle;
    }

    /// <summary>The tree's box in frame px, or null when no tree is visible.</summary>
    private OpenCvSharp.Rect? TreeBox(TreeDetector tree)
    {
        using var frame = Grab1080();
        return frame == null ? null : tree.Detect(frame);
    }

    /// <summary>
    /// Switch to another character, which also ends special states like Mavuika's Flamestrider.
    /// Waits out the switch cooldown first and retries until the switch shows.
    /// </summary>
    private void Dismount()
    {
        // Any other character, picked at random (Mavuika too, when she isn't the one on the bike).
        var active = ActiveSlot();
        var choices = Enumerable.Range(1, 4).Where(slot => slot != active).ToArray();
        var target = choices[Rng.Next(choices.Length)];
        Sleep(Vary(1000));
        for (var i = 0; i < 4 && ActiveSlot() != target; i++)
        {
            Tap(0x30 + target);
            Sleep(Vary(600));
        }
    }

    private TreeDetector? LoadTree()
    {
        if (_tree != null || _treeLoadFailed)
        {
            return _tree;
        }

        try
        {
            _tree = TreeDetector.TryLoad();
            if (_tree == null)
            {
                _log($"Domain: the tree model is missing ({TreeDetector.ModelPath})");
            }
        }
        catch (Exception e)
        {
            _log($"Domain: couldn't load the tree model: {e.Message}");
        }

        _treeLoadFailed = _tree == null;
        return _tree;
    }

    // ----- 5. Walk to the tree -----

    /// <summary>
    /// Walk straight at the tree until its F prompt shows. Sprints with Shift only (a dash can't be steered),
    /// nudges the camera while the tree is only a little off, and stops to re-aim when it drifts further,
    /// so the path doesn't curve off the edge of the platform. Losing the tree while it is still far away
    /// stops the run and looks for it again.
    /// </summary>
    private bool WalkToTree()
    {
        _status("Domain: walking to the tree");
        var tree = LoadTree();
        var started = DateTime.Now;
        var found = false;
        var walking = false;
        var sprinting = Rng.Next(3) != 0; // mostly sprint, sometimes just run
        var lastSeen = DateTime.Now;
        var lastHeight = 0;
        var reaims = 0;

        void Go()
        {
            if (walking)
            {
                return;
            }

            Hold(Native.VK_W, true);
            if (sprinting)
            {
                Sleep(Rng.Next(60, 200));
                Hold(Native.VK_SHIFT, true);
            }

            walking = true;
        }

        void Stop()
        {
            Hold(Native.VK_SHIFT, false);
            Hold(Native.VK_W, false);
            walking = false;
        }

        try
        {
            Go();
            while (DateTime.Now - started < WalkToTreeTimeout)
            {
                if (!_keepGoing() || UserTookOver())
                {
                    break;
                }

                if (Has(_pickF))
                {
                    found = true;
                    break;
                }

                // Close to the tree it fills the screen or drops out of view: stop sprinting and walk on.
                var box = tree == null ? null : TreeBox(tree);
                if (box is { } b)
                {
                    lastSeen = DateTime.Now;
                    lastHeight = b.Height;
                    if (b.Height > CloseTreeHeight && sprinting)
                    {
                        sprinting = false;
                        Hold(Native.VK_SHIFT, false);
                    }

                    var offset = b.X + b.Width / 2 - Middle;
                    if (Math.Abs(offset) > ReaimPx && reaims < 6 && b.Height < CloseTreeHeight)
                    {
                        // Too far off to fix on the move: stand still and turn until it is centred again.
                        reaims++;
                        Stop();
                        _log($"Domain: drifting off the tree ({offset:+#;-#} px), re-aiming");
                        if (!AimAtTree(tree!))
                        {
                            return false;
                        }

                        Go();
                        continue;
                    }

                    if (Math.Abs(offset) > SteerDeadPx)
                    {
                        Native.MoveMouse(Math.Clamp((int)(offset * TurnGain), -200, 200), 0);
                        Rebaseline();
                    }
                }
                else if (tree != null && lastHeight < CloseTreeHeight && DateTime.Now - lastSeen > TimeSpan.FromSeconds(3))
                {
                    // Lost it while still far away: the run is going somewhere else. Stop and look again.
                    Stop();
                    _log("Domain: lost sight of the tree on the way, looking again");
                    if (reaims++ >= 6 || !FindTree(dismount: false))
                    {
                        return false;
                    }

                    lastSeen = DateTime.Now;
                    Go();
                    continue;
                }

                Thread.Sleep(40);
            }
        }
        finally
        {
            Stop();
        }

        if (!found)
        {
            _log("Domain: didn't reach the tree");
            return false;
        }

        return Interact("the tree", () => WaitFor(() => Has(_condensedRow) || Has(_originalRow), TimeSpan.FromSeconds(3)), settle: 150);
    }

    /// <summary>Tree box taller than this (frame px) means it is close: walk, and don't worry if it leaves the view.</summary>
    private const int CloseTreeHeight = 520;

    /// <summary>Tree offset (frame px) that stops the run to re-aim.</summary>
    private const int ReaimPx = 160;

    /// <summary>Tree offset (frame px) below which the run isn't steered at all.</summary>
    private const int SteerDeadPx = 25;

    /// <summary>Standing still, turn until the tree is centred (twice in a row). False if it can't be seen.</summary>
    private bool AimAtTree(TreeDetector tree)
    {
        Sleep(Vary(250)); // come to a stop first
        var centred = 0;
        var missed = 0;
        for (var i = 0; i < 20 && _keepGoing(); i++)
        {
            if (Has(_pickF))
            {
                return true;
            }

            var offset = TreeOffset(tree);
            if (offset == null)
            {
                if (++missed >= 4)
                {
                    return FindTree(dismount: false);
                }

                Sleep(100);
                continue;
            }

            if (Math.Abs(offset.Value) <= TreeCentredPx / 2)
            {
                if (++centred >= 2)
                {
                    return true;
                }

                Sleep(80);
                continue;
            }

            centred = 0;
            Native.MoveMouse(Math.Clamp((int)(offset.Value * TurnGain), -300, 300), 0);
            Rebaseline();
            Sleep(Vary(180));
        }

        return false;
    }

    /// <summary>
    /// Press F at the key or the tree and check it worked, up to 4 times. Sprinting or dashing often
    /// carries the character past the spot, so the F prompt is gone by the time F is pressed:
    /// then step back (and forward) in short taps until the prompt shows again.
    /// </summary>
    private bool Interact(string what, Func<bool> worked, int settle = 250)
    {
        Sleep(Vary(settle)); // let the run come to a stop (short after walking, longer after a sprint or dash)
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            if (!_keepGoing())
            {
                return false;
            }

            if (!Has(_pickF) && !StepBackToPrompt())
            {
                _log($"Domain: lost the F prompt at {what}");
                continue;
            }

            Tap(Native.VK_F);
            if (worked())
            {
                return true;
            }

            _log($"Domain: {what} didn't react, trying again");
        }

        _log($"Domain: couldn't use {what} after 4 tries; stopping");
        return false;
    }

    /// <summary>Short steps back, then forward, until the F prompt is on screen again.</summary>
    private bool StepBackToPrompt()
    {
        foreach (var vk in new[] { Native.VK_S, Native.VK_S, Native.VK_S, Native.VK_S, Native.VK_W, Native.VK_W, Native.VK_W, Native.VK_W, Native.VK_W, Native.VK_W })
        {
            Tap(vk, Rng.Next(120, 200));
            if (!Sleep(Vary(150)))
            {
                return false;
            }

            if (Has(_pickF))
            {
                return true;
            }
        }

        return false;
    }

    // ----- 6. Claim and continue -----

    /// <summary>
    /// Waits while you choose the resin in the pop-up (this part gets automated once the pop-up
    /// template exists), then presses Continue Challenge on the result screen.
    /// </summary>
    private enum ResinPick
    {
        Used,
        OutOfResin,
        NoPopup,
    }

    /// <summary>
    /// The Petrified Tree pop-up lists one row per way to pay, each with its icon on the left and a Use
    /// button on the right. Use condensed resin first, then original resin. The primogem row is never touched.
    /// A row that doesn't work (none left) leaves the pop-up open, so the next row is tried.
    /// </summary>
    private ResinPick PickResin()
    {
        if (!WaitFor(() => Has(_condensedRow) || Has(_originalRow), TimeSpan.FromSeconds(5)))
        {
            return ResinPick.NoPopup;
        }

        var rows = _useOriginal
            ? new[] { (_condensedRow, "condensed"), (_originalRow, "original") }
            : new[] { (_condensedRow, "condensed") };
        foreach (var (row, name) in rows)
        {
            if (!_keepGoing())
            {
                return ResinPick.NoPopup;
            }

            var icon = Find(row);
            if (icon == null)
            {
                continue;
            }

            var y = icon.Value.Y + icon.Value.Height / 2;
            ClickFrame(UseButtonX, y);
            _log($"Domain: using {name} resin");
            // Claimed: the pop-up closes and the rewards open. Not enough resin: it stays (maybe with a prompt on top).
            if (WaitFor(() => !Has(_condensedRow) && !Has(_originalRow), TimeSpan.FromSeconds(3)))
            {
                return ResinPick.Used;
            }

            if (row == _originalRow)
            {
                // The original row spends 40 (double rewards) or 20; the swap button next to Use switches
                // between them. With 20-39 left the 40 option fails, so switch to 20 and try once more.
                Tap(Native.VK_ESCAPE); // close a "not enough resin" prompt, if one opened
                Sleep(Vary(500));
                if (!Has(_originalRow))
                {
                    // There was no prompt, so Esc closed the pop-up: open it again.
                    Tap(Native.VK_F);
                    WaitFor(() => Has(_originalRow), TimeSpan.FromSeconds(3));
                }

                if (Find(_originalRow) is { } again)
                {
                    y = again.Y + again.Height / 2;
                    ClickFrame(SwapButtonX, y);
                    Sleep(Vary(600));
                    ClickFrame(UseButtonX, y);
                    _log("Domain: switched the original resin amount (40/20) and tried again");
                    if (WaitFor(() => !Has(_condensedRow) && !Has(_originalRow), TimeSpan.FromSeconds(3)))
                    {
                        return ResinPick.Used;
                    }
                }
            }

            Tap(Native.VK_ESCAPE);
            Sleep(600);
            if (!Has(_condensedRow) && !Has(_originalRow))
            {
                // Esc closed the pop-up itself; open it again.
                Tap(Native.VK_F);
                if (!WaitFor(() => Has(_condensedRow) || Has(_originalRow), TimeSpan.FromSeconds(3)))
                {
                    return ResinPick.NoPopup;
                }
            }
        }

        ClickFrame(1374, 276); // close the pop-up
        return ResinPick.OutOfResin;
    }

    /// <summary>
    /// Condensed resin left, from the reward screen's top bar: 0 when its icon isn't shown there
    /// (the bar only lists condensed resin while you have some), null when the number can't be read.
    /// </summary>
    private int? ReadCondensed(Mat grey)
    {
        var icon = Vision.Find(grey, _condensedTop);
        if (icon == null)
        {
            return 0;
        }

        return ReadNumber(grey, _digits, new Rect(icon.Value.X + icon.Value.Width, 26, 70, 46), fromRight: false);
    }

    /// <summary>Original resin left (the number before "/ 200"), or null when it can't be read.</summary>
    private int? ReadOriginal(Mat grey)
    {
        var max = Vision.Find(grey, _originalMax);
        if (max == null)
        {
            return null;
        }

        return ReadNumber(grey, _digits, new Rect(max.Value.X - 70, max.Value.Y - 4, 68, max.Value.Height + 8), fromRight: true);
    }

    /// <summary>
    /// Reads the white number nearest one end of the area, digit by digit. Null when any part of it isn't
    /// one of the known digits (so a digit without a template never turns into a wrong number).
    /// </summary>
    internal static int? ReadNumber(Mat grey, IReadOnlyList<(int Digit, Template T)> digits, Rect area, bool fromRight)
    {
        area &= new Rect(0, 0, grey.Width, grey.Height);
        if (area.Width < 20 || area.Height < 24)
        {
            return null;
        }

        using var roi = new Mat(grey, area);

        // Best match of each digit at each x, then keep the strongest that don't overlap.
        var hits = new List<(int X, float Score, int Digit, int Width)>();
        foreach (var (digit, t) in digits)
        {
            if (t.Image.Width > roi.Width || t.Image.Height > roi.Height)
            {
                continue;
            }

            using var result = new Mat();
            Cv2.MatchTemplate(roi, t.Image, result, TemplateMatchModes.CCoeffNormed);
            using var columns = new Mat();
            Cv2.Reduce(result, columns, ReduceDimension.Row, ReduceTypes.Max, MatType.CV_32F);
            columns.GetArray<float>(out var col);
            for (var x = 0; x < col.Length; x++)
            {
                if (col[x] < 0.85f)
                {
                    continue;
                }

                var peak = true;
                for (var k = Math.Max(0, x - 3); k <= Math.Min(col.Length - 1, x + 3) && peak; k++)
                {
                    peak = col[k] <= col[x];
                }

                if (peak)
                {
                    hits.Add((x, col[x], digit, t.Image.Width));
                }
            }
        }

        var kept = new List<(int X, float Score, int Digit, int Width)>();
        foreach (var h in hits.OrderByDescending(h => h.Score))
        {
            if (kept.All(k => Math.Abs(k.X - h.X) >= 8))
            {
                kept.Add(h);
            }
        }

        // The number: bright columns starting at one end, up to the first wide dark gap (an icon or "/").
        using var bright = new Mat();
        Cv2.Threshold(roi, bright, 200, 255, ThresholdTypes.Binary);
        using var anyBright = new Mat();
        Cv2.Reduce(bright, anyBright, ReduceDimension.Row, ReduceTypes.Max, MatType.CV_8U);
        anyBright.GetArray<byte>(out var lit);
        var group = new List<int>();
        var gap = 0;
        for (var i = 0; i < lit.Length; i++)
        {
            var c = fromRight ? lit.Length - 1 - i : i;
            if (lit[c] > 0)
            {
                group.Add(c);
                gap = 0;
            }
            else if (group.Count > 0 && ++gap > 10)
            {
                break;
            }
        }

        if (group.Count == 0)
        {
            return null;
        }

        int lo = group.Min(), hi = group.Max();
        var inNumber = kept.Where(k => k.X >= lo - 3 && k.X <= hi).OrderBy(k => k.X).ToList();
        if (inNumber.Count == 0 || inNumber.Count > 4)
        {
            return null;
        }

        // Every lit column of the number must be inside a recognised digit.
        if (group.Any(c => !inNumber.Any(k => c >= k.X - 2 && c <= k.X + k.Width + 2)))
        {
            return null;
        }

        return inNumber.Aggregate(0, (n, k) => n * 10 + k.Digit);
    }

    /// <summary>Close any pop-up, then leave with the top-left Leave Domain button and confirm.</summary>
    private void LeaveDomain()
    {
        Sleep(Vary(800));
        ClickFrame(57, 44);
        Rect? confirm = null;
        if (WaitFor(() => (confirm = Find(_confirmAnywhere)) != null, TimeSpan.FromSeconds(3)))
        {
            Sleep(Vary(400));
            ClickFrame(confirm!.Value.X + confirm.Value.Width / 2, confirm.Value.Y + confirm.Value.Height / 2);
            _log("Domain: left the domain");
        }
        else
        {
            _log("Domain: couldn't find the Leave Domain confirmation; you're still in the domain");
        }
    }

    /// <summary>The resin counts read after the last round, shown in the status line.</summary>
    private string? _resinCounts;

    /// <summary>Original resin one domain round costs.</summary>
    private const int OriginalPerRound = 20;

    private bool ClaimAndContinue()
    {
        Current = Phase.Claiming;
        switch (PickResin())
        {
            case ResinPick.OutOfResin:
                _log(_useOriginal ? "Domain: no condensed or original resin left" : "Domain: no condensed resin left");
                LeaveDomain();
                return false;
            case ResinPick.NoPopup:
                _status("Domain: choose the resin in the pop-up (condensed first)");
                break;
        }

        var started = DateTime.Now;
        Rect? continueButton = null;
        while (DateTime.Now - started < ResinPickTimeout)
        {
            if (!_keepGoing())
            {
                return false;
            }

            using var frame = Grab1080();
            using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
            if (grey != null && Vision.Find(grey, _exit) != null)
            {
                continueButton = Vision.Find(grey, _confirm);
                if (continueButton != null)
                {
                    break;
                }
            }

            Thread.Sleep(300);
        }

        if (continueButton == null)
        {
            _log("Domain: no result screen after 2 minutes; stopping");
            return false;
        }

        Rounds++;
        _log($"Domain: round {Rounds} claimed");
        int? condensedLeft, originalLeft;
        using (var frame = Grab1080())
        using (var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY))
        {
            condensedLeft = grey == null ? null : ReadCondensed(grey);
            originalLeft = grey == null ? null : ReadOriginal(grey);
        }

        var counts = $"condensed resin left: {condensedLeft?.ToString() ?? "?"}, original resin left: {originalLeft?.ToString() ?? "?"}";
        _log($"Domain: round {Rounds}: {counts}");
        _resinCounts = counts;
        _status($"Domain: round {Rounds} done. {counts}");

        // Out of resin: no condensed left, and (when original is used too) less original than one round costs.
        var outOfResin = condensedLeft == 0 && (!_useOriginal || originalLeft < OriginalPerRound);
        if (outOfResin)
        {
            _log(_useOriginal
                ? $"Domain: no condensed resin and only {originalLeft} original left (a round costs {OriginalPerRound}); leaving the domain"
                : "Domain: that was the last condensed resin; leaving the domain");
            _status($"Domain: out of resin after {Rounds} rounds ({counts}); leaving");
            Sleep(Rng.Next(700, 2500));
            using (var frame = Grab1080())
            using (var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY))
            {
                var leave = grey == null ? null : Vision.Find(grey, _exit);
                if (leave is { } l)
                {
                    ClickFrame(l.X + l.Width / 2 + 160, l.Y + l.Height / 2); // the Leave Domain button
                }
            }

            return false;
        }

        Sleep(800);
        Rebaseline();
        // A short, different pause before each next round, like reading the rewards.
        Sleep(Rng.Next(700, 3500));
        var b = continueButton.Value;
        var left = false;
        for (var attempt = 1; attempt <= 4 && !left; attempt++)
        {
            ClickFrame(b.X + b.Width / 2, b.Y + b.Height / 2);
            // The reward screen closes once Continue registers.
            left = WaitFor(() => !Has(_exit), TimeSpan.FromSeconds(4));
            if (!left)
            {
                _log("Domain: Continue Challenge didn't register, clicking again");
            }
        }

        if (!left)
        {
            _log("Domain: stuck on the reward screen; stopping");
            return false;
        }

        _status("Domain: next round, loading");
        Sleep(2000);
        if (!ArriveInDomain())
        {
            _log("Domain: didn't get back into the domain within 90 s");
            return false;
        }

        return Sleep(Vary(1500));
    }

    // ----- helpers -----

    private bool InsideDomain()
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey != null && Vision.Find(grey, _inDomain) != null && Vision.Find(grey, _confirm) == null;
    }

    private bool Has(Template t) => Find(t) != null;

    private Rect? Find(Template t)
    {
        using var frame = Grab1080();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey == null ? null : Vision.Find(grey, t);
    }

    /// <summary>Polls until the condition holds. False on timeout, stop, or when you take over.</summary>
    private bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            if (!_keepGoing() || UserTookOver())
            {
                return false;
            }

            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private bool Sleep(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (!_keepGoing())
            {
                return false;
            }

            Thread.Sleep(30);
        }

        return true;
    }

    private bool WaitForHotkeyRelease()
    {
        var until = DateTime.Now.AddSeconds(3);
        while (Native.AnyKeyDown(0x11, 0x12, Native.VK_D) && DateTime.Now < until)
        {
            Thread.Sleep(30);
        }

        Thread.Sleep(150);
        Rebaseline();
        return _keepGoing();
    }

    private void ClickFrame(int x, int y)
    {
        (x, y) = OptionPicker.Wobble(x, y, 5, Rng);
        Native.GetClientRect(_hWnd, out var rc);
        _input!.ForegroundClick(x * rc.Width / Vision.Width, y * rc.Height / Vision.Height);
        Rebaseline();
    }

    private void Hold(int vk, bool down)
    {
        if (down == _held.Contains(vk))
        {
            return;
        }

        Native.SendKey(vk, !down);
        if (down)
        {
            _held.Add(vk);
        }
        else
        {
            _held.Remove(vk);
        }

        Rebaseline();
    }

    private void Tap(int vk, int holdMs = 40)
    {
        Native.SendKey(vk, false);
        Thread.Sleep(holdMs + Rng.Next(0, 45));
        Native.SendKey(vk, true);
        Rebaseline();
    }

    private void ReleaseAll()
    {
        foreach (var vk in _held.ToList())
        {
            Native.SendKey(vk, true);
        }

        _held.Clear();
        Rebaseline();
    }

    /// <summary>Our own SendInput also counts as "last input"; only input after this point is yours.</summary>
    private void Rebaseline() => _inputBaseline = Native.LastInputTick();

    private bool UserTookOver() => Native.GetForegroundWindow() != _hWnd || Native.LastInputTick() != _inputBaseline;

    private Mat? Grab1080()
    {
        var raw = _grab();
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

    private static Template? Optional(string file, Rect roi, double threshold, bool useAlpha = false)
    {
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", file)) ? new Template(file, roi, threshold, useAlpha: useAlpha) : null;
    }

    public void Dispose()
    {
        _confirm.Dispose();
        _exit.Dispose();
        _inDomain.Dispose();
        _pickF.Dispose();
        _clickToClose.Dispose();
        _condensedRow.Dispose();
        _defeatBanner?.Dispose();
        _timer?.Dispose();
        _condensedTop.Dispose();
        _originalMax.Dispose();
        _confirmAnywhere.Dispose();
        foreach (var (_, t) in _digits)
        {
            t.Dispose();
        }

        _originalRow.Dispose();
        _completed?.Dispose();
        _tree?.Dispose();
    }
}
