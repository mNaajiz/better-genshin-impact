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
    private readonly Template _exit = new("domain_exit.png", new Rect(0, 540, 960, 540), 0.8, useAlpha: true);

    /// <summary>Top-left Leave Domain icon shown inside a domain instead of the Paimon menu (cut from your frame).</summary>
    private readonly Template _inDomain = new("in_domain.png", new Rect(0, 0, 480, 270), 0.75);

    /// <summary>"Click anywhere to close" under the Ley Line Disorder note shown on arrival (from your screenshot).</summary>
    private readonly Template _clickToClose = new("domain_click_to_close.png", new Rect(700, 620, 520, 160), 0.8);

    private readonly Template _pickF = new("chat_pick_f.png", new Rect(1090, 330, 60, 420));

    /// <summary>"Challenge Completed" banner, made from your screenshot; until then you press Ctrl+Alt+D after the fight.</summary>
    private readonly Template? _completed = Optional("domain_challenge_completed.png", new Rect(480, 135, 960, 405), 0.75);

    /// <summary>Resin icons on the left of the Petrified Tree pop-up rows (from your frame).</summary>
    private readonly Template _condensedRow = new("resin_condensed_row.png", new Rect(540, 420, 200, 420), 0.85);

    private readonly Template _originalRow = new("resin_original_row.png", new Rect(540, 420, 200, 420), 0.85);

    /// <summary>Centre of the Use buttons in that pop-up.</summary>
    private const int UseButtonX = 1266;

    private readonly HashSet<int> _held = [];
    private TreeDetector? _tree;
    private bool _treeLoadFailed;
    private nint _hWnd;
    private GameInput? _input;
    private uint _inputBaseline;
    private volatile bool _fightDone;
    private List<CombatStep>? _rotation;

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
    public void Run(nint hWnd, GameInput input, bool autoFight, string rotation)
    {
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
            opened = WaitFor(() => Has(_confirm), TimeSpan.FromSeconds(3));
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
        var until = DateTime.Now.AddSeconds(45);
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
        Rect? button = null;
        if (!WaitFor(() => (button = Find(_confirm)) != null, TimeSpan.FromSeconds(10)))
        {
            _log($"Domain: no {what} button");
            return false;
        }

        Sleep(300);
        ClickFrame(button!.Value.X + button.Value.Width / 2, button.Value.Y + button.Value.Height / 2);
        _log($"Domain: {what}");
        // The button disappears while the next screen loads.
        WaitFor(() => !Has(_confirm), TimeSpan.FromSeconds(4));
        Sleep(500);
        return true;
    }

    // ----- 2. Walk to the key -----

    /// <summary>The key is straight ahead of where every domain puts you. Hold W until its F prompt shows.</summary>
    private bool WalkToKey()
    {
        Current = Phase.WalkingToKey;
        _status($"Domain round {Rounds + 1}: walking to the key");
        Hold(Native.VK_W, true);
        var found = WaitFor(() => Has(_pickF), WalkToKeyTimeout);
        Hold(Native.VK_W, false);
        if (!found)
        {
            _log("Domain: didn't reach the key within 60 s");
            return false;
        }

        Sleep(150);
        Tap(Native.VK_F);
        _log($"Domain round {Rounds + 1}: started");
        return true;
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

            RunStep(_rotation![step]);
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
                if (!CombatSleep(100))
                {
                    return false;
                }

                if (ActiveSlot() == slot)
                {
                    return CombatSleep(150);
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
                CombatSleep(ms > 0 ? ms : 900);
                Native.SendKey(0x45, true);
                CombatSleep(300);
                break;
            case "e":
                Tap(0x45);
                CombatSleep(400);
                break;
            case "q":
                Tap(0x51);
                CombatSleep(1300); // burst animation
                break;
            case "attack" when a.Seconds <= 0:
                Native.SendLeftButton(true);
                Thread.Sleep(50);
                Native.SendLeftButton(false);
                CombatSleep(250);
                break;
            case "attack":
            {
                var until = DateTime.Now.AddMilliseconds(Math.Max(ms, 200));
                while (DateTime.Now < until && !FightInterrupted())
                {
                    Native.SendLeftButton(true);
                    Thread.Sleep(50);
                    Native.SendLeftButton(false);
                    Thread.Sleep(130);
                }

                break;
            }

            case "charge":
                Native.SendLeftButton(true);
                CombatSleep(ms > 0 ? ms : 1000);
                Native.SendLeftButton(false);
                CombatSleep(200);
                break;
            case "wait":
                CombatSleep(ms);
                break;
            case "dash":
                Tap(Native.VK_SHIFT, 80);
                CombatSleep(300);
                break;
            case "jump":
                Tap(Native.VK_SPACE);
                CombatSleep(500);
                break;
            case "w" or "a" or "s" or "d":
            {
                var vk = a.Name[0] - 'a' + 0x41;
                Native.SendKey(vk, false);
                CombatSleep(ms > 0 ? ms : 300);
                Native.SendKey(vk, true);
                break;
            }
        }

        Rebaseline();
    }

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
        foreach (var vk in new[] { 0x45, Native.VK_W, Native.VK_A, Native.VK_S, Native.VK_D })
        {
            Native.SendKey(vk, true);
        }

        Rebaseline();
    }

    // ----- 4. Find the tree -----

    /// <summary>
    /// Get off Mavuika's bike (switch to another character), reset the camera behind the character, then
    /// turn the camera until the tree model sees the tree in the middle of the screen. Turning instead of
    /// strafing: side-steps overshoot on the bike and the character ends up swaying left and right.
    /// </summary>
    private bool FindTree()
    {
        Current = Phase.FindingTree;
        _status("Domain: looking for the reward tree");
        var tree = LoadTree();
        if (tree == null)
        {
            return false;
        }

        Dismount();
        Native.SendMiddleClick();
        Rebaseline();
        if (!Sleep(900))
        {
            return false;
        }

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

            var offset = TreeOffset(tree);
            if (offset == null)
            {
                centred = 0;
                if (++scans > 24)
                {
                    break;
                }

                // Look around: about 30 degrees per step, a full turn in a dozen steps.
                Native.MoveMouse(ScanTurnPx, 0);
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
        using var frame = Grab1080();
        if (frame == null)
        {
            return null;
        }

        var box = tree.Detect(frame);
        return box == null ? null : box.Value.X + box.Value.Width / 2 - Middle;
    }

    /// <summary>
    /// Switch to another character, which also ends special states like Mavuika's Flamestrider.
    /// Waits out the switch cooldown first and retries until the switch shows.
    /// </summary>
    private void Dismount()
    {
        var active = ActiveSlot();
        var target = active == 2 ? 3 : 2;
        Sleep(1000);
        for (var i = 0; i < 4 && ActiveSlot() != target; i++)
        {
            Tap(0x30 + target);
            Sleep(600);
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

    /// <summary>Walk at the tree, nudging the camera to keep it in the middle, until its F prompt shows.</summary>
    private bool WalkToTree()
    {
        _status("Domain: walking to the tree");
        var tree = LoadTree();
        var started = DateTime.Now;
        var found = false;
        Hold(Native.VK_W, true);
        try
        {
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

                // Close up the tree fills the screen or drops out of view; then just keep walking.
                var offset = tree == null ? null : TreeOffset(tree);
                if (offset is { } o && Math.Abs(o) > TreeCentredPx)
                {
                    Native.MoveMouse(Math.Clamp((int)(o * TurnGain * 0.6), -120, 120), 0);
                    Rebaseline();
                }

                Thread.Sleep(60);
            }
        }
        finally
        {
            Hold(Native.VK_W, false);
        }

        if (!found)
        {
            _log("Domain: didn't reach the tree");
            return false;
        }

        Sleep(150);
        Tap(Native.VK_F);
        return true;
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

        foreach (var (row, name) in new[] { (_condensedRow, "condensed"), (_originalRow, "original") })
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

    private bool ClaimAndContinue()
    {
        Current = Phase.Claiming;
        switch (PickResin())
        {
            case ResinPick.OutOfResin:
                _log("Domain: no condensed or original resin left; stopping (you're still in the domain)");
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
        Sleep(800);
        Rebaseline();
        var b = continueButton.Value;
        ClickFrame(b.X + b.Width / 2, b.Y + b.Height / 2);
        _status("Domain: next round, loading");
        Sleep(2000);
        if (!ArriveInDomain())
        {
            _log("Domain: didn't get back into the domain (out of resin?)");
            return false;
        }

        return Sleep(1500);
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
        Thread.Sleep(holdMs);
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

    private static Template? Optional(string file, Rect roi, double threshold)
    {
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", file)) ? new Template(file, roi, threshold) : null;
    }

    public void Dispose()
    {
        _confirm.Dispose();
        _exit.Dispose();
        _inDomain.Dispose();
        _pickF.Dispose();
        _clickToClose.Dispose();
        _condensedRow.Dispose();
        _originalRow.Dispose();
        _completed?.Dispose();
        _tree?.Dispose();
    }
}
