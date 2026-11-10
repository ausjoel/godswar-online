using System.Collections.Frozen;
using System.Globalization;
using Godswar.Server.Game;

namespace Godswar.Server.State;

/// <summary>
/// Every buff the editor's Buffs page can apply to an online character, keyed by Status.ini status id.
/// This list is only for the operator tools: gameplay still resolves casts through
/// <see cref="SkillStatusEffectCatalog"/> and item use through <see cref="StatusItemPolicy"/>, so adding rows here
/// cannot change what any skill or item does.
/// </summary>
/// <remarks>
/// Three sources, in priority order when a status id appears twice:
/// <list type="number">
/// <item>class skills (<see cref="SkillStatusEffectCatalog"/>): the original 25;</item>
/// <item>item buffs (<see cref="StatusItemPolicy.OperatorDefinitions"/>): heals over time, attack / damage / guard
/// buffs; the peace statuses are left out because their map limits are enforced when the item is used;</item>
/// <item>status data (<c>OperatorBuffCatalog.Generated.cs</c>): Status.ini rows whose every effect is one the status
/// composer already computes, with units proved against the rows above by gen_operator_buffs.py.</item>
/// </list>
/// </remarks>
internal static partial class OperatorBuffCatalog
{
    internal const string SourceClassSkill = "class-skill";
    internal const string SourceItem = "item";
    internal const string SourceStatusData = "status-data";
    // FINALISE2 (F-102/F-103): every other Status.ini status the server can carry (groups G, E and C of the scan).
    internal const string SourceStatusExtra = "status-extra";   // beneficial, some effects not applied (see Note)
    internal const string SourceHostile = "hostile";             // debuffs, controls, damage over time

    // FINALISE2: Name = the client's Status.ini name; Note = what the server really applies and what it does not.
    internal readonly record struct Entry(SkillStatusEffectDefinition Definition, string Source)
    {
        public string Name { get; init; } = "";
        public string Note { get; init; } = "";
        public bool Hostile => !Definition.Beneficial;
    }

    private static readonly FrozenDictionary<uint, Entry> ByStatus = Build();

    private static FrozenDictionary<uint, Entry> Build()
    {
        var rows = new Dictionary<uint, Entry>();
        foreach (var definition in SkillStatusEffectCatalog.All)
            rows.TryAdd(definition.StatusId, new(definition, SourceClassSkill));
        foreach (var definition in StatusItemPolicy.OperatorDefinitions)
            rows.TryAdd(definition.StatusId, new(definition, SourceItem));
        foreach (var definition in StatusDataRows())
            rows.TryAdd(definition.StatusId, new(definition, SourceStatusData));
        // FINALISE2 (F-102/F-103): groups G, E (hostile) and C, generated from Status.ini.
        foreach (var definition in AllStatusRows())
            rows.TryAdd(definition.StatusId, new(definition, definition.Beneficial ? SourceStatusExtra : SourceHostile));
        var names = GeneratedNames();
        var notApplied = GeneratedNotApplied();
        foreach (var (id, entry) in rows.ToArray())
        {
            // A runtime status of the mount kind makes the composer report the player as riding.
            if (entry.Definition.Kind == MountCatalog.RuntimeStatusKind)
                throw new InvalidOperationException($"operator buff {id} uses the mount status kind");
            if (entry.Definition.Duration <= TimeSpan.Zero)
                throw new InvalidOperationException($"operator buff {id} has no positive duration");
            rows[id] = entry with { Name = names.GetValueOrDefault(id, $"status {id}"), Note = Describe(entry.Definition, notApplied.GetValueOrDefault(id)) };
        }
        return rows.ToFrozenDictionary();
    }

    /// <summary>FINALISE2: "Applies: ...; server enforces: ...; not applied (icon only): ..." for one row.</summary>
    internal static string Describe(SkillStatusEffectDefinition d, string? raw)
    {
        var applied = new List<string>();
        void Flat(string name, int value) { if (value != 0) applied.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {value:+#;-#}")); }
        void Percent(string name, decimal fraction) { if (fraction != 0) applied.Add(string.Create(CultureInfo.InvariantCulture, $"{name} -{fraction * 100:0.#}%")); }
        void Basis(string name, int bp) { if (bp != 0) applied.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {bp / 100m:+0.#;-0.#}%")); }
        Flat("Hit", d.HitBonus);
        Flat("Critical", d.CriticalAppendBonus);
        Percent("Physical damage taken", d.PhysicalDamageReduction);
        Percent("Spell damage taken", d.MagicDamageReduction);
        Flat("Defence", d.PhysicalDefenseBonus);
        Flat("Spell defence", d.MagicDefenseBonus);
        Flat("Dodge", d.DodgeBonus);
        Flat("Critical resistance", d.CriticalResistanceBonus);
        Flat("Attack", d.PhysicalAttackBonus);
        Flat("Spell attack", d.MagicAttackBonus);
        Basis("Physical damage dealt", d.PhysicalDamageBonusBasisPoints);
        Basis("Spell damage dealt", d.MagicDamageBonusBasisPoints);
        if (d.HpPerSecond > 0) applied.Add(string.Create(CultureInfo.InvariantCulture, $"HP +{d.HpPerSecond} per second"));
        if (d.MpPerSecond > 0) applied.Add(string.Create(CultureInfo.InvariantCulture, $"MP +{d.MpPerSecond} per second"));

        // Controls are enforced by status id (PlayerSkillCastControlCatalog), not by these fields.
        var enforced = new List<string>();
        var block = PlayerSkillCastControlCatalog.ResolveActiveBlock(d.StatusId);
        if (block == PlayerSkillCastControl.Stunned) enforced.Add("no skill casts (stunned)");
        if (block == PlayerSkillCastControl.Silenced) enforced.Add("no skill casts (silenced)");
        var movement = PlayerSkillCastControlCatalog.BlocksMovement(d.StatusId);
        if (movement) enforced.Add("cannot move");
        if (PlayerSkillCastControlCatalog.ResolveAppliedInterruption(d.StatusId) is not null) enforced.Add("interrupts casting");

        var missing = raw is not null
            ? raw.Split("; ").Where(part =>
                !(block != PlayerSkillCastControl.None && part is "cannot cast spells" or "cannot use combat skills") &&
                !(movement && part == "cannot move") &&
                !(enforced.Contains("interrupts casting") && part == "interrupts casting")).ToList()
            : [];
        var permanent = missing.Remove("permanent in Status.ini; a GM apply lasts 24 h unless minutes are given") ||
            missing.Remove("permanent in Status.ini");
        if (missing.Remove("a GM apply lasts 24 h unless minutes are given")) permanent = true;

        var parts = new List<string> { applied.Count > 0 ? "Applies: " + string.Join(", ", applied) : "Applies: no stat change" };
        if (enforced.Count > 0) parts.Add("Server enforces: " + string.Join(", ", enforced));
        if (missing.Count > 0) parts.Add("Icon only, not applied: " + string.Join(", ", missing));
        if (permanent) parts.Add("Permanent in Status.ini: a GM apply lasts 24 h unless minutes are given");
        return string.Join(". ", parts) + ".";
    }

    public static IEnumerable<Entry> All => ByStatus.Values;

    public static int Count => ByStatus.Count;

    public static bool TryGetByStatus(uint statusId, out Entry entry) => ByStatus.TryGetValue(statusId, out entry);

    /// <summary>FINALISE2: every row in /buff list order: helpful first, then hostile, each by status id.</summary>
    public static IReadOnlyList<Entry> Listed { get; } = ByStatus.Values
        .OrderBy(static entry => entry.Hostile).ThenBy(static entry => entry.Definition.StatusId).ToArray();

    /// <summary>The original skill-id lookup: class-skill rows only, so older callers keep their exact meaning.</summary>
    public static bool TryGetBySkill(int skillId, out Entry entry)
    {
        entry = default;
        return SkillStatusEffectCatalog.TryGet(skillId, out var definition) &&
            ByStatus.TryGetValue(definition.StatusId, out entry) && entry.Source == SourceClassSkill;
    }
}
