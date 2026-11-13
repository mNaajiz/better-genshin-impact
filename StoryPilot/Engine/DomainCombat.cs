using System.Globalization;
using System.Text.RegularExpressions;

namespace StoryPilot.Engine;

/// <summary>One action in a rotation step, e.g. <c>e</c>, <c>e(hold)</c>, <c>attack(8)</c>, <c>wait(0.3)</c>.</summary>
public sealed record CombatAction(string Name, double Seconds, bool Hold);

/// <summary>Switch to the character in <paramref name="Slot"/> (1-4) and run its actions.</summary>
public sealed record CombatStep(int Slot, IReadOnlyList<CombatAction> Actions);

/// <summary>
/// The fight rotation, written one step per line as <c>slot: action, action, ...</c>, the same action
/// names as BetterGI's combat scripts. Example (your Mavuika team, slots as in your party):
/// <code>
/// 1: e
/// 2: e
/// 3: e, attack, attack, attack
/// 4: e, q
/// 1: q, attack(8)
/// </code>
/// </summary>
public static class DomainCombat
{
    public const string DefaultRotation = "1: e\n2: e\n3: e, attack, attack, attack\n4: e, q\n1: q, attack(8)";

    /// <summary>Earlier defaults, replaced by <see cref="DefaultRotation"/> if you never edited them.</summary>
    public static readonly string[] OldDefaults =
    [
        "1: e\n2: e\n3: e, attack(0.3)\n4: q\n1: q, attack(8)",
        "1: e\n2: e\n3: e, attack, attack, attack\n4: q\n1: q, attack(8)",
    ];

    private static readonly HashSet<string> Known = ["e", "q", "attack", "charge", "wait", "dash", "jump", "w", "a", "s", "d"];

    private static readonly Regex ActionRx = new(@"^(?<name>[a-z]+)\s*(\((?<arg>[^)]*)\))?$", RegexOptions.Compiled);

    /// <summary>Parses the rotation; throws <see cref="FormatException"/> naming the bad line.</summary>
    public static List<CombatStep> Parse(string text)
    {
        var steps = new List<CombatStep>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0 || !int.TryParse(line[..colon].Trim(), out var slot) || slot is < 1 or > 4)
            {
                throw new FormatException($"Rotation line {n + 1}: start with the slot number 1-4 and a colon, like \"1: e\"");
            }

            var actions = new List<CombatAction>();
            foreach (var part in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = ActionRx.Match(part.ToLowerInvariant());
                if (!m.Success || !Known.Contains(m.Groups["name"].Value))
                {
                    throw new FormatException($"Rotation line {n + 1}: unknown action \"{part}\"");
                }

                var name = m.Groups["name"].Value;
                var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value.Trim() : "";
                var hold = arg == "hold";
                var seconds = 0.0;
                if (arg.Length > 0 && !hold && !double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                {
                    throw new FormatException($"Rotation line {n + 1}: \"{part}\" needs a number of seconds or \"hold\"");
                }

                actions.Add(new CombatAction(name, seconds, hold));
            }

            if (actions.Count == 0)
            {
                throw new FormatException($"Rotation line {n + 1}: no actions after the colon");
            }

            steps.Add(new CombatStep(slot, actions));
        }

        if (steps.Count == 0)
        {
            throw new FormatException("The rotation is empty");
        }

        return steps;
    }
}
