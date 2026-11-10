using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>Calculated session vitals, never the base-only character database maxima.</summary>
internal readonly record struct PotionVitalsSnapshot(
    int Hp, int Mp, int MaximumHp, int MaximumMp, long Revision)
{
    public bool IsValid => MaximumHp > 0 && MaximumMp >= 0 &&
        Hp >= 0 && Hp <= MaximumHp && Mp >= 0 && Mp <= MaximumMp && Revision >= 0;
}

internal static class PotionUsePolicy
{
    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId,
        out BagConsumableEffect effect)
    {
        effect = default;
        return BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) &&
            BagConsumableEffectCatalog.TryResolve(skill, out effect) && IsReviewedPotion(effect);
    }

    internal static bool IsReviewedPotion(BagConsumableEffect effect) =>
        effect.IsValid && (effect.Kind is BagConsumableEffectKind.RestoreHitPoints or
            BagConsumableEffectKind.RestoreManaPoints) &&
        BagConsumableEffectCatalog.TryResolve(effect.SkillId, out var reviewed) && reviewed == effect;

    public static bool TryPlan(PotionVitalsSnapshot before, BagConsumableEffect effect,
        out PotionUsePlan? plan)
    {
        plan = null;
        // Reserve one revision for the DB settlement and another for live reconciliation.
        // The owning handler must install ReservedRevision under VitalsSync before any await.
        if (!before.IsValid || before.Hp == 0 || before.Revision > long.MaxValue - 2 ||
            !IsReviewedPotion(effect)) return false;
        var maximum = effect.Kind == BagConsumableEffectKind.RestoreHitPoints
            ? before.MaximumHp : before.MaximumMp;
        var missing = effect.Kind == BagConsumableEffectKind.RestoreHitPoints
            ? before.MaximumHp - before.Hp : before.MaximumMp - before.Mp;
        var amount = Math.Min(missing, ResolveRestoreCap(effect, maximum));
        if (amount <= 0) return false;
        plan = new(before, effect, amount);
        return true;
    }

    /// <summary>
    /// POTIONS (Polish patch): the most one use may restore. A percent potion
    /// restores that share of the calculated maximum (gear and buffs count),
    /// rounded down, never more than its flat Amount; others restore Amount.
    /// </summary>
    internal static int ResolveRestoreCap(BagConsumableEffect effect, int maximum)
    {
        if (effect.MaxPercentBasisPoints <= 0)
        {
            return effect.Amount;
        }
        var share = (long)Math.Max(maximum, 0) * effect.MaxPercentBasisPoints / 10_000;
        return (int)Math.Min(share, effect.Amount);
    }
}

/// <summary>
/// A per-use coordination boundary, not an enabled potion handler. All methods must run
/// under the owning character's VitalsSync. Persistence must first flush Before exactly,
/// then CAS its HP/MP/revision and owner fence while consuming the item in one transaction.
/// </summary>
internal sealed class PotionUsePlan
{
    internal PotionUsePlan(PotionVitalsSnapshot before, BagConsumableEffect effect, int appliedAmount)
    {
        Before = before;
        Effect = effect;
        AppliedAmount = appliedAmount;
    }

    public PotionVitalsSnapshot Before { get; }
    public BagConsumableEffect Effect { get; }
    public int AppliedAmount { get; }
    public long ReservedRevision => Before.Revision + 1;
    public PotionVitalsSnapshot PersistedVitals => Before with
    {
        Hp = Effect.Kind == BagConsumableEffectKind.RestoreHitPoints ? Before.Hp + AppliedAmount : Before.Hp,
        Mp = Effect.Kind == BagConsumableEffectKind.RestoreManaPoints ? Before.Mp + AppliedAmount : Before.Mp,
        Revision = ReservedRevision
    };

    // Even a cancelled or failed await can have committed. Checkpoints must remain held
    // until the command inbox proves commit or non-commit; cancellation is not an abort.
    public bool HoldsCheckpoints { get; private set; } = true;
    public bool IsIndeterminate { get; private set; }
    public bool IsSettled => !HoldsCheckpoints;

    public void MarkIndeterminate()
    {
        if (HoldsCheckpoints) IsIndeterminate = true;
    }

    public bool TrySettleCommitted(PotionVitalsSnapshot current, out PotionVitalsSnapshot settled) =>
        TrySettle(current, committed: true, out settled);

    /// <summary>Call only after authoritative proof that the DB transaction did not commit.</summary>
    public bool TrySettleNotCommitted(PotionVitalsSnapshot current, out PotionVitalsSnapshot settled) =>
        TrySettle(current, committed: false, out settled);

    private bool TrySettle(PotionVitalsSnapshot current, bool committed, out PotionVitalsSnapshot settled)
    {
        settled = current;
        if (!HoldsCheckpoints || !current.IsValid || current.Revision < ReservedRevision ||
            current.Revision == long.MaxValue) return false;
        // An equal revision cannot legally contain unmarked damage, healing or a stat change.
        if (current.Revision == ReservedRevision && current != (Before with { Revision = ReservedRevision }))
            return false;
        if (committed && current.Hp > 0)
        {
            // Apply the actually committed delta to current health. Never replay historical
            // absolute health, undo intervening damage, or resurrect a player killed meanwhile.
            if (Effect.Kind == BagConsumableEffectKind.RestoreHitPoints)
                settled = current with { Hp = current.Hp + Math.Min(AppliedAmount, current.MaximumHp - current.Hp) };
            else
                settled = current with { Mp = current.Mp + Math.Min(AppliedAmount, current.MaximumMp - current.Mp) };
        }
        // Always advance beyond the DB's reserved revision, including death and abort paths.
        settled = settled with { Revision = current.Revision + 1 };
        HoldsCheckpoints = false;
        IsIndeterminate = false;
        return true;
    }
}
