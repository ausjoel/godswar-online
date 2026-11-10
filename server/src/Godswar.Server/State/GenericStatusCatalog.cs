// ROUND3: immutable operator catalog generated from the checked-in client audit.
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Godswar.Server.State;

internal readonly record struct GenericStatusEffect(int Code, decimal Value);

internal sealed record GenericStatusDefinition(
    int StatusId,
    string Name,
    int? Kind,
    int? Priority,
    bool Beneficial,
    decimal? DurationSeconds,
    int IntervalSeconds,
    int? RideId,
    bool ServerOnly,
    ImmutableArray<GenericStatusEffect> Effects,
    ImmutableArray<int> SkillIds);

internal static partial class GenericStatusCatalog
{
    // ROUND3: operator rides have an explicit bounded lifetime; normal player Ride remains persistent.
    internal const int OperatorMountDefaultSeconds = 600;

    private static bool IsNativeMount(GenericStatusDefinition row) =>
        row.RideId is >= 0 && row.Kind == MountCatalog.RuntimeStatusKind && row.Priority == 1 &&
        row.DurationSeconds == -1 && row.IntervalSeconds == 0 &&
        row.Effects is [{ Code: 33, Value: 1 }];
    private static readonly FrozenDictionary<int, GenericStatusDefinition> ByStatusId =
        CreateRows().ToFrozenDictionary(row => row.StatusId);

    internal static IEnumerable<GenericStatusDefinition> All => ByStatusId.Values;

    internal static bool TryGet(int statusId, out GenericStatusDefinition definition) =>
        ByStatusId.TryGetValue(statusId, out definition!);

    // A row is applicable only when every component has an authoritative implementation.
    // Do not advertise an icon-only or partially computed effect as a working buff.
    internal static string? UnsupportedReason(GenericStatusDefinition row)
    {
        if (row.ServerOnly) return "Requires its durable entitlement/progression source.";
        // ROUND3: stock Leatherback 1200 has no equipped native item; mapped Leatherbacks start at 1201.
        if (row.StatusId == 1200) return "No equipped native mount item maps to this status.";
        // ROUND3: the audited riding flag is fulfilled by native equipment/model/speed authority.
        if (row.RideId is not null)
            return IsNativeMount(row) ? null : "Mount status has no complete native activation contract.";
        if (row.Kind is null or < 1 || row.Priority is null or < 0 || row.Effects.IsEmpty)
            return "No complete runtime status definition.";
        if (row.DurationSeconds is null or <= 0)
            return "Source-owned/permanent status requires an explicit lifecycle implementation.";
        foreach (var effect in row.Effects)
        {
            if (effect.Code is 0 or 2 or 3 or 4 or 5 or 6)
            {
                if (effect.Value != 1) return "Control flags require the audited enabled value.";
            }
            else if (effect.Code is 11 or 13)
            {
                // ROUND3: negative audited mitigation values enter the existing typed amplification path.
                var encoded = effect.Value * 10_000m;
                if (encoded != decimal.Truncate(encoded) || encoded > 10_000m)
                    return "Incoming damage modifier cannot be represented exactly in basis points.";
                if (encoded < -Application.Combat.IncomingDamageAmplificationPolicy.MaximumBasisPoints)
                    return "Incoming damage amplification exceeds the reviewed combat bound.";
            }
            else if (effect.Code is 12 or 14 or 15 or 32 or 34 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25)
            {
                // Flat values and outgoing damage percentages already have authoritative paths.
                var encoded = effect.Code is 12 or 14 or 15 or 32 or 34 ? effect.Value * 10_000m : effect.Value;
                if (encoded != decimal.Truncate(encoded) || encoded is < int.MinValue or > int.MaxValue)
                    return "Effect cannot be represented exactly by the authoritative integer field.";
            }
            else if (effect.Code is 26 or 28)
            {
                if (row.IntervalSeconds != 1 || effect.Value < 0)
                    return "Periodic recovery requires an authored one-second positive pulse.";
            }
            // ROUND3: zero-interval HP loss needs its own instantaneous/source contract.
            else if (effect.Code == 27)
            {
                if (row.IntervalSeconds is < 1 or > 3 || effect.Value <= 0 ||
                    effect.Value != decimal.Truncate(effect.Value) || effect.Value > int.MaxValue)
                    return "Periodic HP loss requires an exact positive magnitude and authored positive interval.";
            }
            else return $"Effect {effect.Code} has no complete generic runtime implementation.";
        }
        // ROUND3: a multi-component row must also fit the bound as a whole, without silent truncation.
        if (row.Effects.Where(effect => effect.Code == 27).Sum(effect => effect.Value) > int.MaxValue)
            return "Periodic HP loss cannot be represented exactly."; // ROUND3
        foreach (var code in new[] { 11, 13 })
        {
            if (row.Effects.Where(effect => effect.Code == code && effect.Value < 0).Sum(effect => -effect.Value) * 10_000m >
                Application.Combat.IncomingDamageAmplificationPolicy.MaximumBasisPoints)
                return "Incoming damage amplification exceeds the reviewed combat bound.";
        }
        return null;
    }

    internal static bool TryResolve(int statusId, out SkillStatusEffectDefinition definition, out string error)
    {
        definition = default;
        if (!TryGet(statusId, out var row))
        {
            error = "statusId must identify an audited status";
            return false;
        }
        if (UnsupportedReason(row) is { } unsupported)
        {
            error = unsupported;
            return false;
        }
        // ROUND3: do not fabricate a speed bonus from effect 33 (it is the riding flag).
        if (IsNativeMount(row))
        {
            definition = new(0, (uint)row.StatusId, MountCatalog.RuntimeStatusKind, 1, false,
                TimeSpan.FromSeconds(OperatorMountDefaultSeconds), MountCatalog.RideCooldown, 0, 0)
            { RequiresEquippedMount = true };
            error = string.Empty;
            return true;
        }
        int Flat(int code) => checked((int)row.Effects.Where(effect => effect.Code == code).Sum(effect => effect.Value));
        decimal Fraction(int code) => row.Effects.Where(effect => effect.Code == code).Sum(effect => effect.Value);
        // ROUND3: mitigation and amplification compose independently, as authored hostile skills already do.
        decimal Reduction(int code) => row.Effects.Where(effect => effect.Code == code && effect.Value > 0).Sum(effect => effect.Value);
        int Increase(int code) => checked((int)(row.Effects.Where(effect => effect.Code == code && effect.Value < 0)
            .Sum(effect => -effect.Value) * 10_000m));
        definition = new SkillStatusEffectDefinition(
            SkillId: 0, StatusId: checked((uint)row.StatusId), Kind: row.Kind!.Value,
            Priority: row.Priority!.Value, row.Beneficial, TimeSpan.FromSeconds((double)row.DurationSeconds!.Value),
            TimeSpan.Zero, HitBonus: Flat(18), CriticalAppendBonus: Flat(22),
            PhysicalDamageReduction: Reduction(13), MagicDamageReduction: Reduction(11), // ROUND3
            PhysicalDefenseBonus: Flat(20), MagicDefenseBonus: Flat(25),
            DodgeBonus: Flat(19), CriticalResistanceBonus: Flat(23))
        {
            // ROUND3: only reviewed values are accepted; request payloads cannot invent damage modifiers.
            PhysicalDamageTakenIncreaseBasisPoints = Increase(13), MagicDamageTakenIncreaseBasisPoints = Increase(11),
            PhysicalAttackBonus = Flat(21), MagicAttackBonus = Flat(24),
            PhysicalDamageBonusBasisPoints = checked((int)(Fraction(14) * 10_000m)),
            MagicDamageBonusBasisPoints = checked((int)(Fraction(12) * 10_000m)),
            HpPerSecond = Flat(26), MpPerSecond = Flat(28),
            HpDamagePerPulse = Flat(27), DamageIntervalSeconds = Flat(27) > 0 ? row.IntervalSeconds : 0, // ROUND3
            Control = row.Effects.Aggregate(HostileStatusControlFlags.None, (flags, effect) => flags | (effect.Code switch
            {
                0 => HostileStatusControlFlags.HaltIntonate, 2 => HostileStatusControlFlags.NonMoving,
                3 => HostileStatusControlFlags.NonMagicUsing, 4 => HostileStatusControlFlags.NonTechniqueUsing,
                5 => HostileStatusControlFlags.NonAttackUsing, 6 => HostileStatusControlFlags.NonItemUsing,
                _ => HostileStatusControlFlags.None
            })),
            ExperienceBonuses = row.Effects.Any(effect => effect.Code is 15 or 32 or 34)
                ? new ExperienceBoostBonuses(checked((int)(Fraction(15) * 10_000m)),
                    checked((int)(Fraction(32) * 10_000m)), checked((int)(Fraction(34) * 10_000m)))
                : null
        };
        error = string.Empty;
        return true;
    }
}
