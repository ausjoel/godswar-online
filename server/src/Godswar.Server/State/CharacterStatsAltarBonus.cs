using System.Collections.Concurrent;
using Godswar.Server.Application.Guilds;

namespace Godswar.Server.State;

/// <summary>
/// The altar bonus a character currently has, held by character id rather than on
/// the character object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keying this by id is the whole point: the character object does not
/// survive.</b> A pet owner-Merge, an un-merge, a bag command and a login all swap
/// the session's <see cref="GameCharacter"/> for a freshly hydrated one
/// (<c>GameClientHandler.CharacterSnapshot</c> installs it, and
/// <c>GameClientHandler.DurablePets.Projection.ReloadPetProjectionAsync</c> is the
/// path that reported it). A bonus parked on that object is silently replaced by
/// the new object's default, which is how the altar contribution came to look
/// "overwritten by pet merge" and to stay down until the next recompute.
/// </para>
/// <para>
/// This is a cache of a projection, not a second source of truth: the authoritative
/// state is <c>guild_altar_worship</c> plus <c>guild_building_levels</c>, and every
/// writer of this cache derives the value from those tables. Losing an entry costs
/// one recompute and never a wrong answer that outlives it, so the cache lives in
/// the process, where <see cref="CharacterStats.FromCharacter"/> can read it on the
/// stats path that cannot await a query.
/// </para>
/// </remarks>
internal static class GuildAltarBonusCache
{
    private static readonly ConcurrentDictionary<int, GuildAltarAttributeBonus>
        Bonuses = new();

    /// <summary>The bonus this character currently has, or none when not cached.</summary>
    public static GuildAltarAttributeBonus For(int characterId) =>
        Bonuses.TryGetValue(characterId, out var bonus)
            ? bonus
            : GuildAltarAttributeBonus.None;

    /// <summary>
    /// Records the bonus a character now has.
    /// </summary>
    /// <returns>Whether it differs from what was cached.</returns>
    public static bool Set(int characterId, GuildAltarAttributeBonus bonus)
    {
        ArgumentNullException.ThrowIfNull(bonus);
        if (For(characterId) == bonus)
        {
            return false;
        }

        Bonuses[characterId] = bonus;
        return true;
    }

    /// <summary>
    /// Drops a character's entry, which the next recompute restores.
    /// </summary>
    public static void Clear(int characterId) =>
        Bonuses.TryRemove(characterId, out _);
}

/// <summary>
/// Adds the guild altar's own attributes on top of a character's calculated
/// stats.
/// </summary>
/// <remarks>
/// <para>
/// The altar's contribution is a flat attribute value that came out of the
/// client's own content, not a percentage of the character's stat, so it is
/// added after the character's own numbers are calculated. That keeps it
/// distinct from <see cref="CharacterStats.WithCoreCombatAttributeBonus"/>, which
/// scales the four core combat stats.
/// </para>
/// <para>
/// The bonus is applied every time stats are recomputed, so it follows the
/// character through login, combat and the attribute panel without any of those
/// paths having to know the altar exists.
/// </para>
/// </remarks>
internal static class CharacterStatsAltarBonus
{
    public static CharacterStats Apply(
        CharacterStats baseline,
        GuildAltarAttributeBonus bonus)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(bonus);
        if (bonus.IsEmpty)
        {
            return baseline;
        }

        var maxHp = Math.Max(1, baseline.MaxHp + bonus.MaxHp);
        var maxMp = Math.Max(0, baseline.MaxMp + bonus.MaxMp);
        return new CharacterStats
        {
            CharacterId = baseline.CharacterId,
            AccountId = baseline.AccountId,
            Name = baseline.Name,
            Profession = baseline.Profession,
            Level = baseline.Level,
            MaxHp = maxHp,
            MaxMp = maxMp,
            // The pools follow the new ceilings, exactly as the character hydrator
            // does when it clamps against them.
            CurrentHp = Math.Clamp(baseline.CurrentHp, 0, maxHp),
            CurrentMp = Math.Clamp(
                baseline.CurrentMp,
                0,
                Math.Max(1, maxMp)),
            PhysicalAttack = Add(baseline.PhysicalAttack, bonus.PhysicalAttack),
            PhysicalDefense = Add(baseline.PhysicalDefense, bonus.PhysicalDefense),
            MagicAttack = Add(baseline.MagicAttack, bonus.MagicAttack),
            MagicDefense = Add(baseline.MagicDefense, bonus.MagicDefense),
            Hit = Add(baseline.Hit, bonus.Hit),
            Dodge = Add(baseline.Dodge, bonus.Dodge),
            DamageAbsorb = Add(baseline.DamageAbsorb, bonus.DamageAbsorb),
            HpRecovery = Add(baseline.HpRecovery, bonus.HpRecovery),
            MpRecovery = Add(baseline.MpRecovery, bonus.MpRecovery),
            StatusHit = baseline.StatusHit,
            StatusResistance = baseline.StatusResistance,
            Critical = baseline.Critical,
            CriticalResistance = baseline.CriticalResistance,
            PhysicalDamageBonus = baseline.PhysicalDamageBonus,
            MagicDamageBonus = baseline.MagicDamageBonus,
            CureBonus = baseline.CureBonus,
            BeCureBonus = baseline.BeCureBonus,
            IgnorePhysicalDefense = baseline.IgnorePhysicalDefense,
            IgnoreMagicDefense = baseline.IgnoreMagicDefense,
            PhysicalAppendDamage = baseline.PhysicalAppendDamage,
            MagicAppendDamage = baseline.MagicAppendDamage,
            CriticalDamagePercent = baseline.CriticalDamagePercent,
            CriticalDamageFlat = baseline.CriticalDamageFlat,
            PhysicalDamageReduction = baseline.PhysicalDamageReduction,
            MagicDamageReduction = baseline.MagicDamageReduction,
            CriticalDamageReduction = baseline.CriticalDamageReduction,
            LifeAbsorption = baseline.LifeAbsorption,
            LifeAbsorptionFlat = baseline.LifeAbsorptionFlat,
            DamageRebound = baseline.DamageRebound,
            PhysicalFlatAbsorption = baseline.PhysicalFlatAbsorption,
            MagicFlatAbsorption = baseline.MagicFlatAbsorption,
            CriticalDamageFlatReduction = baseline.CriticalDamageFlatReduction,
            DamageReboundFlat = baseline.DamageReboundFlat,
            BasicAttackIntervalMilliseconds =
                baseline.BasicAttackIntervalMilliseconds,
            BasicAttackRange = baseline.BasicAttackRange,
            WeaponScore = baseline.WeaponScore,
            WeaponRank = baseline.WeaponRank,
            WeaponAuraEffect = baseline.WeaponAuraEffect,
            ArmorScore = baseline.ArmorScore,
            ArmorRank = baseline.ArmorRank,
            ArmorAuraEffect = baseline.ArmorAuraEffect,
            LearnedSkillCount = baseline.LearnedSkillCount
        };
    }

    private static int Add(int value, int amount) =>
        amount <= 0 ? value : (int)Math.Min(int.MaxValue, (long)value + amount);
}
