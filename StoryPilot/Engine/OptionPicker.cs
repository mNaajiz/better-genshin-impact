namespace StoryPilot.Engine;

/// <summary>
/// Which dialogue option to pick, and small random variations in timing and click position
/// so the input looks less mechanical.
/// </summary>
public static class OptionPicker
{
    /// <summary>Up to this much extra wait before choosing an option (ms).</summary>
    public const int OptionJitterMs = 600;

    /// <summary>Up to this much extra wait between dialogue advances (ms).</summary>
    public const int AdvanceJitterMs = 300;

    /// <summary>
    /// Index of the option to choose. A quest option (orange text or "!") wins when <paramref name="preferQuest"/>
    /// is on; otherwise the first, last or a random one.
    /// </summary>
    public static (int Index, string Why) Choose(int count, int? questIndex, OptionChoice choice, bool preferQuest, Random rng)
    {
        if (preferQuest && questIndex is { } q && q >= 0 && q < count)
        {
            return (q, "quest");
        }

        return choice switch
        {
            OptionChoice.First => (0, "first"),
            OptionChoice.Last => (count - 1, "last"),
            _ => (rng.Next(count), "random"),
        };
    }

    /// <summary><paramref name="baseMs"/> plus 0..<paramref name="maxExtraMs"/>.</summary>
    public static int Jitter(int baseMs, int maxExtraMs, Random rng) => baseMs + rng.Next(maxExtraMs + 1);

    /// <summary>A point a few pixels off <paramref name="x"/>,<paramref name="y"/>, so clicks don't hit the exact same pixel.</summary>
    public static (int X, int Y) Wobble(int x, int y, int radius, Random rng) =>
        (x + rng.Next(-radius, radius + 1), y + rng.Next(-radius, radius + 1));
}
