using System.Text.Json;

namespace StoryPilot.Engine;

public enum OptionChoice
{
    First,
    Last,
    Random,
}

public sealed class Settings
{
    /// <summary>Press Space while dialogue is playing to advance it.</summary>
    public bool AdvanceDialogue { get; set; } = true;

    /// <summary>Choose a dialogue option automatically when options appear.</summary>
    public bool PickOptions { get; set; } = true;

    /// <summary>Which option to pick when no quest (orange / "!") option is present.</summary>
    public OptionChoice DefaultOption { get; set; } = OptionChoice.Random;

    /// <summary>Bumped when a default changes, so older settings files pick up the new default once.</summary>
    public int Version { get; set; }

    private const int CurrentVersion = 3;

    /// <summary>Prefer orange quest options and "!" options over the default choice.</summary>
    public bool PreferQuestOptions { get; set; } = true;

    /// <summary>Click through black-screen narration and skip buttons (hangouts, cutscene text).</summary>
    public bool SkipCutscenes { get; set; } = true;

    /// <summary>Close item / tutorial pop-ups that appear right after dialogue.</summary>
    public bool ClosePopups { get; set; } = true;

    /// <summary>
    /// After a quest part, open the map, move it to the quest (the map's quest button) and teleport to the
    /// waypoint / statue nearest the next step, unless it is close by.
    /// </summary>
    public bool TeleportToNextQuest { get; set; } = true;

    /// <summary>Only teleport when the quest is at least this far (metres).</summary>
    public int TeleportMinDistanceMetres { get; set; } = 150;

    /// <summary>On the big map, press Teleport as soon as you pick a waypoint / statue / domain.</summary>
    public bool AutoTeleportOnWaypoint { get; set; } = true;

    /// <summary>Wait after the Teleport button (or pick list) appears before clicking it (ms).</summary>
    public int AutoTeleportDelayMs { get; set; } = 100;

    /// <summary>After a story (and after Teleport to next quest), walk to the quest marker. Ctrl+Alt+W always works.</summary>
    public bool WalkToQuestMarker { get; set; } = false;

    /// <summary>Hold Shift the whole time while walking to the quest marker.</summary>
    public bool WalkSprint { get; set; } = false;

    /// <summary>Sprint in random short bursts while walking to the quest (ignored when <see cref="WalkSprint"/> is on).</summary>
    public bool WalkSprintBursts { get; set; } = true;

    /// <summary>Delay before choosing an option, so you can read it (ms).</summary>
    public int OptionDelayMs { get; set; } = 0;

    /// <summary>In domains, fight with <see cref="DomainRotation"/> instead of waiting for you.</summary>
    public bool DomainAutoFight { get; set; } = true;

    /// <summary>Domain fight rotation, one "slot: actions" step per line (see DomainCombat).</summary>
    public string DomainRotation { get; set; } = DomainCombat.DefaultRotation;

    /// <summary>In domains, use original resin once condensed resin is gone (otherwise leave the domain then).</summary>
    public bool DomainUseOriginalResin { get; set; } = true;

    /// <summary>How often the game is checked (ms). Higher = lighter on the laptop.</summary>
    public int TickMs { get; set; } = 250;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StoryPilot", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (loaded.Version < 2)
                {
                    // Version 2: "Random" became the default option choice.
                    loaded.DefaultOption = OptionChoice.Random;
                }

                if (loaded.Version < 3)
                {
                    // Version 3: travelling to the next quest part is on by default.
                    loaded.TeleportToNextQuest = true;
                }

                loaded.Version = CurrentVersion;

                if (DomainCombat.OldDefaults.Contains(loaded.DomainRotation.Replace("\r", "")))
                {
                    // Xilonen needs three normal attacks after her E to start her buffs.
                    loaded.DomainRotation = DomainCombat.DefaultRotation;
                }

                return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupt file: fall back to defaults.
        }

        return new Settings();
    }

    public void Save()
    {
        Version = CurrentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
