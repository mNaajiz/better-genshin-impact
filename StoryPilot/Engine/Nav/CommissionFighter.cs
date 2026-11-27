using OpenCvSharp;

namespace StoryPilot.Engine.Nav;

/// <summary>
/// Open-world fighting for combat commissions: your domain rotation on repeat, and every few seconds
/// BetterGI's "is the fight over" check (press L: outside combat the party screen opens and its top bar
/// shows white + yellow at fixed pixels; in combat the game refuses). Game must be in front.
/// </summary>
public sealed class CommissionFighter(Func<Mat?> grab, Func<bool> keepGoing, Action<string> log)
{
    private const int VK_L = 0x4C;
    private const int VK_X = 0x58;

    /// <summary>Fight until the check says it's over, or <paramref name="maxSeconds"/>.</summary>
    public bool Fight(IReadOnlyList<CombatStep> rotation, int maxSeconds = 120)
    {
        var start = DateTime.Now;
        var lastCheck = DateTime.Now;
        while (keepGoing())
        {
            foreach (var step in rotation)
            {
                if (!keepGoing())
                {
                    return false;
                }

                RunStep(step);
                if ((DateTime.Now - lastCheck).TotalSeconds >= 6)
                {
                    lastCheck = DateTime.Now;
                    if ((DateTime.Now - start).TotalSeconds > 5 && FightOver())
                    {
                        log("Fight over");
                        return true;
                    }
                }

                if ((DateTime.Now - start).TotalSeconds > maxSeconds)
                {
                    log($"Still fighting after {maxSeconds} s, moving on");
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>BetterGI AutoFightTask.CheckFightFinish (without the enemy health-bar pre-check).</summary>
    public bool FightOver()
    {
        Native.SendLeftButton(false);
        Thread.Sleep(1500);
        Tap(VK_L);
        Thread.Sleep(450);
        bool over;
        using (var frame = Grab())
        {
            over = frame != null && IsPartyScreenBar(frame);
        }

        Tap(VK_X);
        if (over)
        {
            Tap(VK_L); // cancel the party switch
            Thread.Sleep(800);
            Native.SendKey(Native.VK_ESCAPE, false);
            Thread.Sleep(40);
            Native.SendKey(Native.VK_ESCAPE, true);
            Thread.Sleep(800);
        }

        return over;
    }

    /// <summary>White tile at (768, 50) and yellow progress bar at (790, 50) on a 1920x1080 frame.</summary>
    public static bool IsPartyScreenBar(Mat frame)
    {
        var bar = frame.At<Vec3b>(50, 790);
        var tile = frame.At<Vec3b>(50, 768);
        var yellow = bar.Item2 >= 200 && bar.Item1 >= 200 && bar.Item0 <= 100;
        var white = tile.Item2 >= 240 && tile.Item1 >= 240 && tile.Item0 >= 240;
        return yellow && white;
    }

    private Mat? Grab()
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

        var r = raw.Resize(new OpenCvSharp.Size(Vision.Width, Vision.Height), 0, 0, InterpolationFlags.Area);
        raw.Dispose();
        return r;
    }

    private void RunStep(CombatStep step)
    {
        SwitchTo(step.Slot);
        foreach (var a in step.Actions)
        {
            if (!keepGoing())
            {
                return;
            }

            RunAction(a);
        }
    }

    private void SwitchTo(int slot)
    {
        if (ActiveSlot() == slot)
        {
            return;
        }

        var until = DateTime.Now.AddSeconds(3);
        while (DateTime.Now < until && keepGoing())
        {
            Tap(0x30 + slot);
            for (var i = 0; i < 5; i++)
            {
                Thread.Sleep(DomainFarmer.Vary(100));
                if (ActiveSlot() == slot)
                {
                    Thread.Sleep(DomainFarmer.Vary(150));
                    return;
                }
            }
        }
    }

    private int ActiveSlot()
    {
        using var frame = Grab();
        using var grey = frame?.CvtColor(ColorConversionCodes.BGR2GRAY);
        return grey == null ? 0 : DomainFarmer.ActiveSlotOf(grey);
    }

    // Same actions as DomainFarmer.RunAction.
    private void RunAction(CombatAction a)
    {
        var ms = (int)(a.Seconds * 1000);
        switch (a.Name)
        {
            case "e" when a.Hold:
                Native.SendKey(0x45, false);
                Thread.Sleep(DomainFarmer.Vary(ms > 0 ? ms : 900));
                Native.SendKey(0x45, true);
                Thread.Sleep(DomainFarmer.Vary(300));
                break;
            case "e":
                Tap(0x45);
                Thread.Sleep(DomainFarmer.Vary(550));
                break;
            case "q":
                Tap(0x51);
                Thread.Sleep(DomainFarmer.Vary(300));
                Tap(0x51);
                Thread.Sleep(DomainFarmer.Vary(1100));
                break;
            case "attack" when a.Seconds <= 0:
                Click();
                Thread.Sleep(DomainFarmer.Vary(250));
                break;
            case "attack":
            {
                var until = DateTime.Now.AddMilliseconds(Math.Max(ms, 200));
                while (DateTime.Now < until && keepGoing())
                {
                    Click();
                    Thread.Sleep(Random.Shared.Next(110, 200));
                }

                break;
            }

            case "charge":
                Native.SendLeftButton(true);
                Thread.Sleep(DomainFarmer.Vary(ms > 0 ? ms : 1000));
                Native.SendLeftButton(false);
                Thread.Sleep(DomainFarmer.Vary(200));
                break;
            case "wait":
                Thread.Sleep(DomainFarmer.Vary(ms));
                break;
            case "dash":
                Tap(Native.VK_SHIFT, 80);
                Thread.Sleep(DomainFarmer.Vary(300));
                break;
            case "jump":
                Tap(Native.VK_SPACE);
                Thread.Sleep(DomainFarmer.Vary(500));
                break;
            case "w" or "a" or "s" or "d":
            {
                var vk = a.Name[0] - 'a' + 0x41;
                Native.SendKey(vk, false);
                Thread.Sleep(DomainFarmer.Vary(ms > 0 ? ms : 300));
                Native.SendKey(vk, true);
                break;
            }
        }
    }

    private static void Click()
    {
        Native.SendLeftButton(true);
        Thread.Sleep(Random.Shared.Next(35, 75));
        Native.SendLeftButton(false);
    }

    private static void Tap(int vk, int holdMs = 40)
    {
        Native.SendKey(vk, false);
        Thread.Sleep(holdMs);
        Native.SendKey(vk, true);
    }
}
