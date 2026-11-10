using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal enum CharacterHealthProjectionMode
{
    PreserveAbsolute,
    PreservePercentage
}

internal static class CharacterCalculatedStatsProjectionApplier
{
    public static void Apply(
        GameCharacter liveCharacter,
        CharacterStats persistedStats,
        CharacterHealthProjectionMode healthMode)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedStats);

        lock (liveCharacter.VitalsSync)
        {
            var previousMaximumHealth = Math.Max(1, liveCharacter.MaxHp);
            var previousHealth = Math.Clamp(
                liveCharacter.CurrentHp,
                0,
                previousMaximumHealth);
            var baseMaximumHealth = Math.Max(1, persistedStats.MaxHp);
            var effectiveMaximumHealth = ResolveEffectiveMaximumHealth(
                liveCharacter,
                persistedStats);
            // The base is the character's own value: never read back the live
            // ceiling as if it were the base, or each refresh adds the bonus again.
            var baseMaximumMana = Math.Max(0, persistedStats.MaxMp);
            var maximumMana = Math.Max(
                0,
                baseMaximumMana + GuildAltarBonusCache.For(liveCharacter.Id).MaxMp);
            var currentHealth = healthMode switch
            {
                CharacterHealthProjectionMode.PreservePercentage =>
                    ScaleHealth(
                        previousHealth,
                        previousMaximumHealth,
                        effectiveMaximumHealth),
                _ => Math.Clamp(
                    previousHealth,
                    0,
                    effectiveMaximumHealth)
            };
            var currentMana = Math.Clamp(
                liveCharacter.CurrentMp,
                0,
                maximumMana);
            var projectedStats = new CharacterStats
            {
                CharacterId = persistedStats.CharacterId,
                AccountId = persistedStats.AccountId,
                Name = persistedStats.Name,
                Profession = persistedStats.Profession,
                Level = persistedStats.Level,
                // Keep the reusable calculated projection passive-free so a
                // later refresh cannot compound its effective maximum.
                MaxHp = baseMaximumHealth,
                MaxMp = baseMaximumMana,
                CurrentHp = currentHealth,
                CurrentMp = currentMana,
                PhysicalAttack = persistedStats.PhysicalAttack,
                PhysicalDefense = persistedStats.PhysicalDefense,
                MagicAttack = persistedStats.MagicAttack,
                MagicDefense = persistedStats.MagicDefense,
                Hit = persistedStats.Hit,
                Dodge = persistedStats.Dodge,
                StatusHit = persistedStats.StatusHit,
                StatusResistance = persistedStats.StatusResistance,
                Critical = persistedStats.Critical,
                CriticalResistance = persistedStats.CriticalResistance,
                DamageAbsorb = persistedStats.DamageAbsorb,
                PhysicalDamageBonus = persistedStats.PhysicalDamageBonus,
                MagicDamageBonus = persistedStats.MagicDamageBonus,
                CureBonus = persistedStats.CureBonus,
                BeCureBonus = persistedStats.BeCureBonus,
                HpRecovery = persistedStats.HpRecovery,
                MpRecovery = persistedStats.MpRecovery,
                IgnorePhysicalDefense =
                    persistedStats.IgnorePhysicalDefense,
                IgnoreMagicDefense = persistedStats.IgnoreMagicDefense,
                PhysicalAppendDamage =
                    persistedStats.PhysicalAppendDamage,
                MagicAppendDamage = persistedStats.MagicAppendDamage,
                CriticalDamagePercent =
                    persistedStats.CriticalDamagePercent,
                CriticalDamageFlat = persistedStats.CriticalDamageFlat,
                PhysicalDamageReduction =
                    persistedStats.PhysicalDamageReduction,
                MagicDamageReduction =
                    persistedStats.MagicDamageReduction,
                CriticalDamageReduction =
                    persistedStats.CriticalDamageReduction,
                LifeAbsorption = persistedStats.LifeAbsorption,
                DamageRebound = persistedStats.DamageRebound,
                PhysicalFlatAbsorption =
                    persistedStats.PhysicalFlatAbsorption,
                MagicFlatAbsorption = persistedStats.MagicFlatAbsorption,
                CriticalDamageFlatReduction =
                    persistedStats.CriticalDamageFlatReduction,
                DamageReboundFlat = persistedStats.DamageReboundFlat,
                LifeAbsorptionFlat = persistedStats.LifeAbsorptionFlat,
                BasicAttackIntervalMilliseconds =
                    persistedStats.BasicAttackIntervalMilliseconds,
                BasicAttackRange = persistedStats.BasicAttackRange,
                WeaponScore = persistedStats.WeaponScore,
                WeaponRank = persistedStats.WeaponRank,
                WeaponAuraEffect = persistedStats.WeaponAuraEffect,
                ArmorScore = persistedStats.ArmorScore,
                ArmorRank = persistedStats.ArmorRank,
                ArmorAuraEffect = persistedStats.ArmorAuraEffect,
                LearnedSkillCount = persistedStats.LearnedSkillCount
            };

            liveCharacter.MaxHp = effectiveMaximumHealth;
            liveCharacter.MaxMp = maximumMana;
            liveCharacter.CurrentHp = currentHealth;
            liveCharacter.CurrentMp = currentMana;
            liveCharacter.WeaponRank = projectedStats.WeaponRank;
            liveCharacter.WeaponAuraEffect =
                projectedStats.WeaponAuraEffect;
            liveCharacter.ArmorRank = projectedStats.ArmorRank;
            liveCharacter.ArmorAuraEffect = projectedStats.ArmorAuraEffect;
            // Cache the clean base ceilings/attributes with the CURRENT live pools.
            // Reusing the persisted pools makes a stat refresh report stale health.
            // Both base maxima above exclude altar bonuses, so repeated refreshes
            // cannot compound those bonuses. Keep identity when nothing changed.
            liveCharacter.CalculatedStats = persistedStats.CurrentHp == currentHealth &&
                persistedStats.CurrentMp == currentMana && persistedStats.MaxHp == baseMaximumHealth &&
                persistedStats.MaxMp == baseMaximumMana ? persistedStats : projectedStats;
            liveCharacter.MarkVitalsChanged();
        }
    }

    /// <summary>
    /// The character's live maximum health: its own value, the altar's ceiling
    /// bonus, and the elemental passive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The altar's ceiling bonus is added here, and only here.</b> The live
    /// <see cref="GameCharacter.MaxHp"/> is what the recovery tick, healing, the
    /// party health bars and the vitals clamps all read as "the maximum", so a
    /// bonus that only reached the calculated stats would raise the number the
    /// player sees while every one of those systems kept stopping at the old
    /// ceiling - a maximum that can never be healed into.
    /// </para>
    /// <para>
    /// It is read from the character's own projection rather than from
    /// <paramref name="baseStats"/> because that parameter is the clean base the
    /// altar bonus is deliberately kept out of; adding it to a value that already
    /// carried it is what produced a doubled ceiling.
    /// </para>
    /// </remarks>
    public static int ResolveEffectiveMaximumHealth(
        GameCharacter character,
        CharacterStats baseStats)
    {
        var withAltar = Math.Max(
            1,
            Math.Max(1, baseStats.MaxHp)
                + GuildAltarBonusCache.For(character.Id).MaxHp);
        var passive = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            character.ElementalEquipment,
            withAltar,
            movementSpeed: 0);
        return Math.Max(
            1,
            checked((int)Math.Min(
                passive.MaximumHealth,
                int.MaxValue)));
    }

    private static int ScaleHealth(
        int previousHealth,
        int previousMaximumHealth,
        int newMaximumHealth)
    {
        if (previousHealth <= 0)
        {
            return 0;
        }

        var scaled =
            ((long)previousHealth * newMaximumHealth +
             (previousMaximumHealth / 2L)) /
            previousMaximumHealth;
        return (int)Math.Clamp(scaled, 0L, newMaximumHealth);
    }
}
