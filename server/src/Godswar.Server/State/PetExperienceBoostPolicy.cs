using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// One reviewed pet-experience potion: the bag item, the skill that carries
/// the grant, and the client status the player sees.
/// </summary>
internal readonly record struct PetExperienceBoostDefinition(
    uint ItemId,
    int SkillId,
    int StatusId,
    int BonusBasisPoints,
    long OnlineTicks);

/// <summary>
/// The pet-experience potion family (items 4529-4533 / skills 4752-4756).
/// </summary>
/// <remarks>
/// <para>
/// The tier and duration of every potion come from the client's own
/// <c>Text/EquipDescription.dat</c> lines, and the granted status comes from
/// <c>Magic.ini</c>'s <c>Status=</c>. The five reviewed pairs are
/// <c>+50%/60min → 513</c>, <c>+100%/60min → 514</c>, <c>+300%/60min → 515</c>,
/// <c>+100%/8h → 516</c>, and <c>+300%/8h → 517</c>. Every one of them uses
/// <see cref="ExperienceBoostKinds.Pet"/>, which is pet-only and already
/// participates in <c>ExperienceBoostState.ApplyToPet</c>.
/// </para>
/// <para>
/// Duration is online time, not wall-clock time: the grant writes
/// <c>character_experience_modifiers.remaining_online_ticks</c> and is spent by
/// the ordinary online progression interval settlement, so logging out stops
/// the clock.
/// </para>
/// <para>
/// This policy backs the live <c>10051</c> item-use branch directly. It
/// deliberately does not extend <c>BagConsumableEvidence</c>: that record and
/// <c>BagConsumableEffectCatalog.ActivationEnabled</c> belong to the parked
/// HP/MP/silver path, which is still off, and the pet potions do not need a
/// capture-shaped frame sequence because they persist state and republish the
/// composed <c>10167</c> snapshot instead.
/// </para>
/// </remarks>
internal static class PetExperienceBoostPolicy
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
    public const int Kind = ExperienceBoostKinds.Pet;

    private const int MinimumBasisPoints = 5_000;

    private static readonly PetExperienceBoostDefinition[] Reviewed =
    [
        new(4529, 4752, 513, 5_000, TimeSpan.TicksPerHour),
        new(4530, 4753, 514, 10_000, TimeSpan.TicksPerHour),
        new(4531, 4754, 515, 30_000, TimeSpan.TicksPerHour),
        new(4532, 4755, 516, 10_000, 8 * TimeSpan.TicksPerHour),
        new(4533, 4756, 517, 30_000, 8 * TimeSpan.TicksPerHour),
        new(4540, 4765, 591, 5_000, 8 * TimeSpan.TicksPerHour)
    ];

    private static readonly FrozenDictionary<uint, PetExperienceBoostDefinition>
        ByItemId = Reviewed.ToFrozenDictionary(
            static definition => definition.ItemId);

    private static readonly FrozenDictionary<int, PetExperienceBoostDefinition>
        BySkillId = Reviewed.ToFrozenDictionary(
            static definition => definition.SkillId);

    public static IReadOnlyList<PetExperienceBoostDefinition> All =>
        Reviewed;

    /// <summary>
    /// The reviewed grant of one item skill, if that skill is one of the five
    /// pet-experience potions.
    /// </summary>
    public static bool TryResolveSkill(
        int skillId,
        out PetExperienceBoostDefinition definition) =>
        BySkillId.TryGetValue(skillId, out definition);

    /// <summary>
    /// Resolves the grant of a locked bag item. The skill and the item's own
    /// classification stay owned by the published item revision; this policy
    /// only accepts the exact reviewed pairings.
    /// </summary>
    public static bool TryResolveItem(
        IItemTemplateCatalog templates,
        uint itemId,
        out PetExperienceBoostDefinition definition)
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
                $"Pet-experience potion {itemId} is absent from official content.");
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
                $"Pet-experience potion {itemId} has invalid official metadata.");
        }

        definition = reviewed;
        return true;
    }

    /// <summary>
    /// Applies the tier rule to an incoming grant against whatever is already
    /// active: a strictly higher tier replaces the active grant and drops its
    /// remaining time, while the same tier only extends the timer up to
    /// <see cref="MaximumOnlineTicks"/>, and a lower tier changes nothing.
    /// </summary>
    public static ExperienceBoostPotionGrant ResolveGrant(
        int activeBonusBasisPoints,
        long activeOnlineTicks,
        in PetExperienceBoostDefinition incoming)
    {
        if (incoming.BonusBasisPoints < MinimumBasisPoints ||
            incoming.OnlineTicks <= 0 ||
            incoming.OnlineTicks > MaximumOnlineTicks)
        {
            throw new InvalidDataException(
                "The resolved pet-experience grant is outside its reviewed range.");
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
                $"Pet-experience potion content is missing {name}.");
}
