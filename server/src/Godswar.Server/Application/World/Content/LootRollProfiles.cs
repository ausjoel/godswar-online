using System.Collections.Frozen;
using System.Security.Cryptography;

using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.Application.World.Content;

// ATTR: random attributes, quality and grade per drop (GodsWar-2.51/Polish).
//
// A loot rule may name a roll profile (monster_loot_rules.roll_profile). When
// it does, and the profile exists and the dropped item has an attribute pool,
// the drop gets a quality, a grade and 0-4 attributes rolled at death time.
// Every other rule keeps its fixed columns, so a database without profiles
// drops exactly what the stock server drops.

/// <summary>
/// One editable roll profile. Weights are relative; a step is the weight of a
/// level relative to the level below it in basis points (10,000 = every level
/// in the range is equally likely, 5,000 = each level is half as likely as the
/// one below).
/// </summary>
internal sealed record LootRollProfile(
    string ProfileKey,
    int AttributeWeight0,
    int AttributeWeight1,
    int AttributeWeight2,
    int AttributeWeight3,
    int AttributeWeight4,
    short QualityMinimum,
    short QualityMaximum,
    int QualityStepBasisPoints,
    short GradeMinimum,
    short GradeMaximum,
    int GradeStepBasisPoints)
{
    /// <summary>The client's item data covers quality 1-10.</summary>
    public const short MaximumQuality = 10;

    /// <summary>The client's item data covers grade 1-12.</summary>
    public const short MaximumGrade = 12;

    /// <summary>Slot 5 stays free for an Attribute Stone.</summary>
    public const int MaximumAttributes = 4;

    public const int MaximumWeight = 1_000_000;

    public const int MaximumStepBasisPoints = 100_000;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ProfileKey) &&
        AttributeWeight0 is >= 0 and <= MaximumWeight &&
        AttributeWeight1 is >= 0 and <= MaximumWeight &&
        AttributeWeight2 is >= 0 and <= MaximumWeight &&
        AttributeWeight3 is >= 0 and <= MaximumWeight &&
        AttributeWeight4 is >= 0 and <= MaximumWeight &&
        (long)AttributeWeight0 + AttributeWeight1 + AttributeWeight2 +
            AttributeWeight3 + AttributeWeight4 > 0 &&
        QualityMinimum is >= 1 and <= MaximumQuality &&
        QualityMaximum >= QualityMinimum && QualityMaximum <= MaximumQuality &&
        QualityStepBasisPoints is >= 0 and <= MaximumStepBasisPoints &&
        GradeMinimum is >= 1 and <= MaximumGrade &&
        GradeMaximum >= GradeMinimum && GradeMaximum <= MaximumGrade &&
        GradeStepBasisPoints is >= 0 and <= MaximumStepBasisPoints;
}

/// <summary>One attribute an item may carry, from <c>item_allowed_attributes</c>.</summary>
internal readonly record struct LootAttributeOption(short AttributeId, short StatType);

/// <summary>
/// The profiles and the per-item attribute pools a loot snapshot rolls with.
/// Pools are cleaned on construction: class (200-231) and elemental (480-500)
/// ids are dropped because <c>character_items</c> rejects them in slots 1-5,
/// and duplicate ids collapse to one.
/// </summary>
internal sealed class LootRollContent
{
    public static LootRollContent Empty { get; } =
        new([], new Dictionary<uint, IReadOnlyList<LootAttributeOption>>());

    private readonly FrozenDictionary<string, LootRollProfile> _profiles;
    private readonly FrozenDictionary<uint, LootAttributeGroup[]> _pools;

    public LootRollContent(
        IReadOnlyList<LootRollProfile> profiles,
        IReadOnlyDictionary<uint, IReadOnlyList<LootAttributeOption>> pools)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(pools);
        var byKey = new Dictionary<string, LootRollProfile>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!profile.IsValid || !byKey.TryAdd(profile.ProfileKey, profile))
            {
                throw new InvalidDataException(
                    $"Loot roll profile {profile.ProfileKey} is invalid.");
            }
        }

        _profiles = byKey.ToFrozenDictionary(StringComparer.Ordinal);
        _pools = pools
            .Select(static pair => KeyValuePair.Create(pair.Key, Group(pair.Value)))
            .Where(static pair => pair.Value.Length > 0)
            .ToFrozenDictionary();
    }

    public int ProfileCount => _profiles.Count;

    public int PoolCount => _pools.Count;

    public bool TryGetProfile(string key, out LootRollProfile profile) =>
        _profiles.TryGetValue(key, out profile!);

    /// <summary>Distinct stat types the item can roll (0 when it has no pool).</summary>
    public int StatTypeCount(uint itemId) =>
        _pools.TryGetValue(itemId, out var groups) ? groups.Length : 0;

    public static bool IsSlotAttribute(short attributeId) =>
        attributeId >= 0 &&
        attributeId is not (>= 200 and <= 231) &&
        attributeId is not (>= 480 and <= 500);

    /// <summary>
    /// Rolls one drop, or returns <c>false</c> when the rule's profile or the
    /// item's pool is missing (the caller then keeps the rule's fixed columns).
    /// The roll is its own SHA-256 of the death event, the loot index and a
    /// salt, so the stock chance and quantity bytes are never reused.
    /// </summary>
    public bool TryRoll(
        string? profileKey,
        uint itemId,
        Guid deathEventId,
        int lootIndex,
        ServerRatesSnapshot rates,
        out ItemGrantAttributes attributes)
    {
        attributes = default;
        if (profileKey is null ||
            !_profiles.TryGetValue(profileKey, out var profile) ||
            !_pools.TryGetValue(itemId, out var groups))
        {
            return false;
        }

        Span<byte> input = stackalloc byte[21];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        deathEventId.TryWriteBytes(input[..16]);
        BitConverter.TryWriteBytes(input[16..20], lootIndex);
        input[20] = Salt;
        SHA256.HashData(input, hash);
        attributes = Roll(profile, groups, hash, rates);
        return true;
    }

    /// <summary>The salt byte that separates this roll from the stock roll.</summary>
    internal const byte Salt = 0xA7;

    private static ItemGrantAttributes Roll(
        LootRollProfile profile,
        LootAttributeGroup[] groups,
        ReadOnlySpan<byte> hash,
        ServerRatesSnapshot rates)
    {
        // Bytes 0-3 count, 4-7 quality, 8-11 grade, 12-27 four picks.
        var rare = rates.Get(ServerRateKeys.DropRareAttributes);
        Span<long> weights = stackalloc long[LootRollProfile.MaximumAttributes + 1];
        weights[0] = profile.AttributeWeight0;
        weights[1] = profile.AttributeWeight1;
        weights[2] = profile.AttributeWeight2;
        weights[3] = Scale(profile.AttributeWeight3, rare);
        weights[4] = Scale(profile.AttributeWeight4, rare);
        var count = Pick(weights, Read(hash, 0));
        count = Math.Min(count, groups.Length);

        var quality = PickLevel(
            profile.QualityMinimum,
            profile.QualityMaximum,
            Step(profile.QualityStepBasisPoints, rates.Get(ServerRateKeys.DropQualityUp)),
            Read(hash, 4));
        var grade = PickLevel(
            profile.GradeMinimum,
            profile.GradeMaximum,
            Step(profile.GradeStepBasisPoints, rates.Get(ServerRateKeys.DropGradeUp)),
            Read(hash, 8));

        // Partial Fisher-Yates over stat types: one attribute per stat type.
        Span<int> order = stackalloc int[groups.Length];
        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        Span<short> chosen = stackalloc short[LootRollProfile.MaximumAttributes];
        for (var slot = 0; slot < count; slot++)
        {
            var value = Read(hash, 12 + slot * 4);
            var remaining = (uint)(order.Length - slot);
            var swap = slot + (int)(value % remaining);
            (order[slot], order[swap]) = (order[swap], order[slot]);
            var ids = groups[order[slot]].AttributeIds;
            chosen[slot] = ids[(int)(value / remaining % (uint)ids.Length)];
        }

        short? a1 = count > 0 ? chosen[0] : null;
        short? a2 = count > 1 ? chosen[1] : null;
        short? a3 = count > 2 ? chosen[2] : null;
        short? a4 = count > 3 ? chosen[3] : null;
        return new ItemGrantAttributes(
            quality,
            grade,
            a1, null,
            a2, null,
            a3, null,
            a4, null,
            null, null);
    }

    private static uint Read(ReadOnlySpan<byte> hash, int offset) =>
        BitConverter.ToUInt32(hash[offset..(offset + 4)]);

    private static long Scale(int weight, decimal rate) =>
        rate == 1m ? weight : (long)decimal.Truncate(weight * rate);

    private static long Step(int stepBasisPoints, decimal rate) =>
        rate == 1m
            ? stepBasisPoints
            : (long)Math.Min(
                LootRollProfile.MaximumStepBasisPoints,
                decimal.Truncate(stepBasisPoints * rate));

    private static int Pick(ReadOnlySpan<long> weights, uint value)
    {
        long total = 0;
        foreach (var weight in weights)
        {
            total += weight;
        }

        if (total <= 0)
        {
            return 0;
        }

        // Multiply-shift maps the 32-bit value onto 0..total-1 evenly, also
        // when total exceeds 2^32 (level weights are fixed-point).
        var target = (long)(((UInt128)value * (ulong)total) >> 32);
        for (var index = 0; index < weights.Length; index++)
        {
            if (target < weights[index])
            {
                return index;
            }

            target -= weights[index];
        }

        return weights.Length - 1;
    }

    /// <summary>
    /// Level <c>minimum + k</c> weighs <c>step^k</c> (fixed point). A step of 0
    /// always gives the minimum.
    /// </summary>
    private static short PickLevel(short minimum, short maximum, long stepBasisPoints, uint value)
    {
        Span<long> weights = stackalloc long[maximum - minimum + 1];
        long weight = 1_000_000_000L;
        for (var index = 0; index < weights.Length; index++)
        {
            weights[index] = weight;
            weight = weight * stepBasisPoints / 10_000;
            weight = Math.Min(weight, 1_000_000_000_000L);
        }

        return checked((short)(minimum + Pick(weights, value)));
    }

    private static LootAttributeGroup[] Group(IReadOnlyList<LootAttributeOption> options) =>
        options
            .Where(static option => IsSlotAttribute(option.AttributeId))
            .DistinctBy(static option => option.AttributeId)
            .GroupBy(static option => option.StatType)
            .OrderBy(static group => group.Key)
            .Select(static group => new LootAttributeGroup(
                group.Key,
                group.Select(static option => option.AttributeId)
                    .Order()
                    .ToArray()))
            .ToArray();

    private sealed record LootAttributeGroup(short StatType, short[] AttributeIds);
}
