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
