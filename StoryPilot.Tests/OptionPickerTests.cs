using StoryPilot.Engine;
using Xunit;

namespace StoryPilot.Tests;

public class OptionPickerTests
{
    [Fact]
    public void QuestOptionWinsWhenPreferred()
    {
        var rng = new Random(1);
        foreach (var choice in Enum.GetValues<OptionChoice>())
        {
            Assert.Equal((2, "quest"), OptionPicker.Choose(4, 2, choice, preferQuest: true, rng));
        }

        // Not preferred: the quest option is just another option.
        Assert.Equal((0, "first"), OptionPicker.Choose(4, 2, OptionChoice.First, preferQuest: false, rng));
        Assert.Equal((3, "last"), OptionPicker.Choose(4, 2, OptionChoice.Last, preferQuest: false, rng));
    }

    [Fact]
    public void RandomCoversEveryOption()
    {
        var rng = new Random(42);
        var seen = new int[3];
        for (var i = 0; i < 300; i++)
        {
            var (index, why) = OptionPicker.Choose(3, null, OptionChoice.Random, preferQuest: true, rng);
            Assert.Equal("random", why);
            seen[index]++;
        }

        // Roughly even (each about 100 of 300).
        Assert.All(seen, n => Assert.InRange(n, 60, 140));
    }

    [Fact]
    public void RandomIsTheDefault()
    {
        Assert.Equal(OptionChoice.Random, new Settings().DefaultOption);
    }

    [Fact]
    public void JitterStaysInRange()
    {
        var rng = new Random(7);
        var values = Enumerable.Range(0, 500).Select(_ => OptionPicker.Jitter(1000, OptionPicker.OptionJitterMs, rng)).ToList();
        Assert.All(values, v => Assert.InRange(v, 1000, 1000 + OptionPicker.OptionJitterMs));
        Assert.True(values.Distinct().Count() > 50, "jitter should vary");

        var advance = Enumerable.Range(0, 200).Select(_ => OptionPicker.Jitter(0, OptionPicker.AdvanceJitterMs, rng)).ToList();
        Assert.All(advance, v => Assert.InRange(v, 0, OptionPicker.AdvanceJitterMs));
    }

    [Fact]
    public void ClickPositionsWobbleAroundTarget()
    {
        var rng = new Random(3);
        var points = Enumerable.Range(0, 200).Select(_ => OptionPicker.Wobble(100, 200, 3, rng)).ToList();
        Assert.All(points, p =>
        {
            Assert.InRange(p.X, 97, 103);
            Assert.InRange(p.Y, 197, 203);
        });
        Assert.True(points.Distinct().Count() > 10, "clicks should not all hit the same pixel");
    }

    [Fact]
    public void SameSeedSameChoices()
    {
        var a = new Random(99);
        var b = new Random(99);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(
                OptionPicker.Choose(5, null, OptionChoice.Random, true, a),
                OptionPicker.Choose(5, null, OptionChoice.Random, true, b));
        }
    }
}
