using System.Text.Json;

namespace StoryPilot.Engine;

public enum OptionChoice
{
    First,
    Last,
}

public sealed class Settings
{
    /// <summary>Press Space while dialogue is playing to advance it.</summary>
    public bool AdvanceDialogue { get; set; } = true;

    /// <summary>Choose a dialogue option automatically when options appear.</summary>
    public bool PickOptions { get; set; } = true;

    /// <summary>Which option to pick when no quest (orange / "!") option is present.</summary>
    public OptionChoice DefaultOption { get; set; } = OptionChoice.First;

    /// <summary>Prefer orange quest options and "!" options over the default choice.</summary>
    public bool PreferQuestOptions { get; set; } = true;

    /// <summary>Click through black-screen narration and skip buttons (hangouts, cutscene text).</summary>
    public bool SkipCutscenes { get; set; } = true;

    /// <summary>Close item / tutorial pop-ups that appear right after dialogue.</summary>
    public bool ClosePopups { get; set; } = true;

    /// <summary>After a story, teleport to the waypoint nearest the next quest step if it is far away.</summary>
    public bool TeleportToNextQuest { get; set; } = false;

    /// <summary>Only teleport when the quest is at least this far (metres).</summary>
    public int TeleportMinDistanceMetres { get; set; } = 150;

    /// <summary>On the big map, press Teleport as soon as you pick a waypoint / statue / domain.</summary>
    public bool AutoTeleportOnWaypoint { get; set; } = true;

    /// <summary>Wait after the Teleport button (or pick list) appears before clicking it (ms).</summary>
    public int AutoTeleportDelayMs { get; set; } = 100;

    /// <summary>Delay before choosing an option, so you can read it (ms).</summary>
    public int OptionDelayMs { get; set; } = 0;

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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
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
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
