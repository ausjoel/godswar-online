using System.Collections.Frozen;

namespace Godswar.Server.Game;

/// <summary>
/// Per-template open-world monster attack presentation, loaded from the
/// embedded <c>Game/monster-attack-presentation.csv</c> (the single
/// editable source; rebuild after changing it).
/// <list type="bullet">
/// <item>skill_id: sent as the first 10046 of a hit instead of the
/// "Normal Attack" 2000, so casters show their spell and archers an arrow.
/// Visual only: damage, cooldown and mana are unchanged. Wonderland uses the
/// same [skill, 2000, 10026] sequence with captured ids.</item>
/// <item>reach melee: the monster gets melee reach (3 m or its collision
/// range) even though its attack_type would make it ranged (9 m).</item>
/// </list>
/// </summary>
internal static class MonsterAttackPresentationTable
{
    internal const string ResourceName =
        "Godswar.Server.Game.monster-attack-presentation.csv";
    internal const uint MinimumSkillId = 2001;
    internal const uint MaximumSkillId = 2999;

    internal readonly record struct Entry(
        string TemplateKey,
        uint? SkillId,
        bool MeleeReach);

    private static readonly Lazy<FrozenDictionary<string, Entry>> Table =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IReadOnlyDictionary<string, Entry> Entries => Table.Value;

    /// <summary>The skill to present for this template, or null for 2000.</summary>
    public static uint? ImpactSkillId(string templateKey) =>
        Table.Value.TryGetValue(templateKey, out var entry)
            ? entry.SkillId
            : null;

    public static bool ForcesMeleeReach(string templateKey) =>
        Table.Value.TryGetValue(templateKey, out var entry) &&
        entry.MeleeReach;

    private static FrozenDictionary<string, Entry> Load()
    {
        using var stream =
            typeof(MonsterAttackPresentationTable).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException(
                $"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static FrozenDictionary<string, Entry> Parse(string text)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var header = false;
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var fields = line.Split(',', 6);
            if (!header)
            {
                if (fields.Length < 3 ||
                    fields[0] != "template_key" ||
                    fields[1] != "skill_id" ||
                    fields[2] != "reach")
                {
                    throw Invalid(lineNumber, "header must start template_key,skill_id,reach");
                }
                header = true;
                continue;
            }
            if (fields.Length < 3)
            {
                throw Invalid(lineNumber, "expected template_key,skill_id,reach,...");
            }
            var key = fields[0].Trim();
            if (key.Length == 0)
            {
                throw Invalid(lineNumber, "empty template_key");
            }
            uint? skill = null;
            var skillText = fields[1].Trim();
            if (skillText.Length > 0)
            {
                if (!uint.TryParse(skillText, out var parsed) ||
                    parsed < MinimumSkillId ||
                    parsed > MaximumSkillId)
                {
                    throw Invalid(
                        lineNumber,
                        $"skill_id '{skillText}' must be blank or {MinimumSkillId}-{MaximumSkillId}");
                }
                skill = parsed;
            }
            var meleeReach = fields[2].Trim() switch
            {
                "melee" => true,
                "ranged" or "" => false,
                var other => throw Invalid(
                    lineNumber,
                    $"reach '{other}' must be melee or ranged")
            };
            if (!entries.TryAdd(key, new Entry(key, skill, meleeReach)))
            {
                throw Invalid(lineNumber, $"duplicate template_key {key}");
            }
        }
        if (!header)
        {
            throw Invalid(lineNumber, "missing header");
        }
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FormatException Invalid(int line, string message) =>
        new($"{ResourceName} line {line}: {message}");
}
