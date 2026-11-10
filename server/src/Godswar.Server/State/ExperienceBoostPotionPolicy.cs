using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// One reviewed enduring experience potion: the bag item, the skill that
/// carries the grant, and the client status the player sees.
/// </summary>
internal readonly record struct ExperienceBoostPotionDefinition(
    uint ItemId,
    int SkillId,
    int StatusId,
    int BonusBasisPoints,
    long OnlineTicks);

/// <summary>
/// The enduring experience-potion family plus the short 60-minute grants that
/// share its channel (items 4500-4503, 4506, 4534, 4535, 4539; skills
/// 4800/4807/4808/4809/4747/4759/4760/4764).
/// </summary>
/// <remarks>
/// <para>
/// Unlike the pet-experience potions, this family raises the character's own
/// monster-kill experience and the pet's at the same time, so it lives in
/// <see cref="ExperienceBoostKinds.PersistentExperiencePotion"/> rather than in
/// the fighter <c>Consumable</c> channel or the pet <c>Pet</c> channel. A
/// channel of its own is what makes the grant stack with a running
/// pet-experience potion and what gives it an independently tracked duration:
/// <c>character_experience_modifiers</c> is keyed by
/// <c>(character_id, kind)</c>, so one row holds the character-and-pet grant
/// while another holds the pet-only grant.
/// </para>
/// <para>
/// Both durations share this channel deliberately. The 60-minute grants
/// (4500-4503) and the eight-hour grants (4506/4534/4535/4539) then resolve
/// through one tier rule, so reusing the same tier extends the timer while a
/// higher tier replaces it - which is the documented behaviour of the family.
/// Keeping the short grants out of the fighter <c>Consumable</c> channel also
/// stops them competing with the mooncake and Passion Rose consumables, which
/// share that channel through the same primary key.
/// </para>
/// <para>
/// Tiers and durations come from the client's own
/// <c>Text/EquipDescription.dat</c> lines and each granted status comes from
/// <c>Magic.ini</c>'s <c>Status=</c>.
/// </para>
/// <para>
/// Duration is online time, not wall-clock time: the grant writes
/// <c>character_experience_modifiers.remaining_online_ticks</c> and is spent by
/// the ordinary online progression interval settlement, so logging out stops
/// the clock. This mirrors <see cref="PetExperienceBoostPolicy"/> exactly.
/// </para>
/// </remarks>
internal static class ExperienceBoostPotionPolicy
{
    /// <summary>
    /// Same-tier reuse extends the timer but never beyond this much online
    /// time. A higher-tier potion replaces the active grant outright instead,
    /// so this ceiling never truncates an upgrade.
    /// </summary>
    public const long MaximumOnlineTicks = 24 * TimeSpan.TicksPerHour;

    /// <summary>
    /// The channel every grant in this family writes to.
    /// </summary>
    public const int Kind = ExperienceBoostKinds.PersistentExperiencePotion;

    private const long EightHours = 8 * TimeSpan.TicksPerHour;
    private const long OneHour = TimeSpan.TicksPerHour;
    private const long FifteenMinutes = TimeSpan.TicksPerMinute * 15;
    private const int MinimumBasisPoints = 2_500;

    private static readonly ExperienceBoostPotionDefinition[] Reviewed =
    [
        // 60-minute grants.
        new(4500, 4800, 504, 2_500, FifteenMinutes),
        new(4501, 4807, 505, 5_000, OneHour),
        new(4502, 4808, 506, 10_000, OneHour),
        new(4503, 4809, 507, 30_000, OneHour),

        // Eight-hour grants.
        new(4534, 4759, 585, 5_000, EightHours),
        new(4506, 4747, 508, 10_000, EightHours),
        new(4535, 4760, 586, 30_000, EightHours),
        new(4539, 4764, 590, 40_000, EightHours)
    ];

    private static readonly FrozenDictionary<uint, ExperienceBoostPotionDefinition>
        ByItemId = Reviewed.ToFrozenDictionary(
            static definition => definition.ItemId);

    private static readonly FrozenDictionary<int, ExperienceBoostPotionDefinition>
        BySkillId = Reviewed.ToFrozenDictionary(
            static definition => definition.SkillId);

    public static IReadOnlyList<ExperienceBoostPotionDefinition> All =>
        Reviewed;

    /// <summary>
    /// Whether an item ID is one of the reviewed potions. This reads the bare
    /// ID set without the pinned template, so a routing classifier can call it
    /// on any item and it never throws.
    /// </summary>
    public static bool IsReviewedItem(uint itemId) =>
        ByItemId.ContainsKey(itemId);

    /// <summary>
    /// The reviewed grant of one item skill, if that skill is one of the three
    /// enduring experience potions.
    /// </summary>
    public static bool TryResolveSkill(
        int skillId,
        out ExperienceBoostPotionDefinition definition) =>
        BySkillId.TryGetValue(skillId, out definition);

    /// <summary>
    /// Resolves the grant of a locked bag item. The skill and the item's own
    /// classification stay owned by the published item revision; this policy
    /// only accepts the exact reviewed pairings.
    /// </summary>
    public static bool TryResolveItem(
        IItemTemplateCatalog templates,
        uint itemId,
        out ExperienceBoostPotionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(templates);
        definition = default;
        if (!ByItemId.TryGetValue(itemId, out var reviewed))
        {
            return false;
        }
        if (!templates.TryGet(itemId, out var template))
        {
            throw new InvalidDataException(
                $"Enduring experience potion {itemId} is absent from official content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var stats = document.RootElement;
        if (template.Id != itemId ||
            !string.Equals(
                template.Kind,
                "consume item",
                StringComparison.Ordinal) ||
            !ReadRequired(stats, "ID").Equals(
                itemId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            ReadRequired(stats, "Use") != "1" ||
            ReadRequired(stats, "Skill") !=
                reviewed.SkillId.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException(
                $"Enduring experience potion {itemId} has invalid official metadata.");
        }

        definition = reviewed;
        return true;
    }

    /// <summary>
    /// Applies the tier rule to an incoming grant against whatever is already
    /// active on this channel: a strictly higher tier replaces the active grant
    /// and drops its remaining time, the same tier only extends the timer up to
    /// <see cref="MaximumOnlineTicks"/>, and a lower tier changes nothing.
    /// </summary>
    public static ExperienceBoostPotionGrant ResolveGrant(
        int activeBonusBasisPoints,
        long activeOnlineTicks,
        in ExperienceBoostPotionDefinition incoming)
    {
        if (incoming.BonusBasisPoints < MinimumBasisPoints ||
            incoming.OnlineTicks <= 0 ||
            incoming.OnlineTicks > MaximumOnlineTicks)
        {
            throw new InvalidDataException(
                "The resolved experience-potion grant is outside its reviewed range.");
        }

        return ExperienceBoostTierRule.Resolve(
            activeBonusBasisPoints,
            activeOnlineTicks,
            incoming.StatusId,
            incoming.SkillId,
            incoming.BonusBasisPoints,
            incoming.OnlineTicks,
            MaximumOnlineTicks);
    }

    private static string ReadRequired(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(
                $"Enduring experience potion content is missing {name}.");
}

/// <summary>
/// The outcome of applying one timed experience potion to whatever was active
/// on its channel. It carries the status and skill of the winning grant so the
/// executor can persist them without re-resolving the item. Both potion
/// families resolve to this one shape because their tier rules are identical.
/// </summary>
internal readonly record struct ExperienceBoostPotionGrant(
    bool ReplacesActive,
    int StatusId,
    int SkillId,
    int BonusBasisPoints,
    long OnlineTicks)
{
    /// <summary>
    /// The client's own ordering rank for this grant. Tiers are the bonus
    /// percentages themselves, so the rank has to follow the basis points.
    /// </summary>
    public int Priority => BonusBasisPoints / 1_000;
}
