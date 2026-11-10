using Godswar.Server.State;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// The committed result of using one bag consumable (item <c>Use=1</c> with a
/// reviewed <c>Magic.ini</c> effect). It carries the resolved skill, the effect
/// family, the transcribed amount, and the authoritative balances after the
/// commit so a repair path can prove what was applied.
/// </summary>
/// <remarks>
/// The two source files behind <see cref="BagConsumableEffectCatalog"/> are
/// byte-identical to the installed client's copies; their SHA-256 pins live on
/// that catalog. Nothing here is captured: the reference capture contains no
/// potion use, so the HP/MP numbers rest on Magic.ini alone.
/// </remarks>
internal sealed record BagConsumableEvidence(
    long ItemInstanceId,
    int ItemTemplateId,
    int KitBagSlot,
    int SkillId,
    BagConsumableEffectKind EffectKind,
    int Amount,
    int StatusId,
    int StatusKind,
    int StatusDurationSeconds,
    int CurrentHp,
    int CurrentMp,
    long Silver)
{
    public bool IsValid =>
        ItemInstanceId > 0 &&
        ItemTemplateId > 0 &&
        KitBagSlot >= 0 &&
        SkillId > 0 &&
        Enum.IsDefined(EffectKind) &&
        Amount > 0 &&
        StatusId >= 0 &&
        StatusKind >= 0 &&
        StatusDurationSeconds >= 0 &&
        CurrentHp >= 0 &&
        CurrentMp >= 0 &&
        Silver >= 0 &&
        (EffectKind != BagConsumableEffectKind.GrantTimedExperienceBoost ||
            StatusId > 0 && StatusKind > 0 && StatusDurationSeconds > 0) &&
        (EffectKind == BagConsumableEffectKind.RestoreHitPoints ||
            CurrentHp > 0) &&
        (EffectKind == BagConsumableEffectKind.RestoreManaPoints ||
            CurrentMp > 0) &&
        (EffectKind == BagConsumableEffectKind.GrantSilver || Silver > 0);
}
