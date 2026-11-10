namespace Godswar.Server.State;

/// <summary>
/// Projects every passive skill family the one summoned pet has learned.
/// Values come from the installed client's Pet_Skill.xml through the pinned
/// learned-skill publication; the effect code selects the owner stat channel
/// and its scale. Fraction-valued effects are stored in basis points, and the
/// authored Vampiric family reuses effect 34 as a fraction where the stock
/// flat families use it as a fixed on-hit amount.
/// </summary>
internal static class PostgresCharacterPetLearnedSkillProjectionSql
{
    public const string CommonTableExpression =
        """
        pet_learned_skill_stat_values AS (
            SELECT projected.user_id, projected.stat_name, projected.stat_value
            FROM (
                SELECT
                    pet.user_id,
                    CASE
                        WHEN curve.effect = 0 THEN 'max_hp'
                        WHEN curve.effect = 1 THEN 'max_mp'
                        WHEN curve.effect = 2 THEN 'hit'
                        WHEN curve.effect = 3 THEN 'dodge'
                        WHEN curve.effect = 4 THEN 'physical_attack'
                        WHEN curve.effect = 5 THEN 'physical_defense'
                        WHEN curve.effect = 6 THEN 'magic_attack'
                        WHEN curve.effect = 7 THEN 'magic_defense'
                        WHEN curve.effect = 8 THEN 'critical'
                        WHEN curve.effect = 9 THEN 'critical_resistance'
                        WHEN curve.effect = 10 THEN 'damage_absorb'
                        WHEN curve.effect = 13 THEN 'hp_recovery'
                        WHEN curve.effect = 14 THEN 'mp_recovery'
                        WHEN curve.effect = 15 THEN 'status_hit'
                        WHEN curve.effect = 16 THEN 'status_resistance'
                        WHEN curve.effect = 19 THEN 'ignore_physical_defense'
                        WHEN curve.effect = 20 THEN 'ignore_magic_defense'
                        WHEN curve.effect = 21 THEN 'physical_damage_bonus'
                        WHEN curve.effect = 22 THEN 'magic_damage_bonus'
                        WHEN curve.effect = 23 THEN 'physical_append_damage'
                        WHEN curve.effect = 24 THEN 'magic_append_damage'
                        WHEN curve.effect = 25 THEN 'critical_damage_percent'
                        WHEN curve.effect = 26 THEN 'critical_damage_flat'
                        WHEN curve.effect = 29 THEN 'physical_flat_absorption'
                        WHEN curve.effect = 30 THEN 'magic_flat_absorption'
                        WHEN curve.effect = 32 THEN 'critical_damage_flat_reduction'
                        WHEN curve.effect = 34 AND curve.family_type = 428
                            THEN 'life_absorption'
                        WHEN curve.effect = 34 THEN 'life_absorption_flat'
                        WHEN curve.effect = 37 THEN 'damage_rebound'
                        WHEN curve.effect = 38 THEN 'damage_rebound_flat'
                    END AS stat_name,
                    CASE
                        WHEN curve.effect IN (19, 20, 21, 22, 25, 37)
                            THEN step.absolute_value * 10000
                        WHEN curve.effect = 34 AND curve.family_type = 428
                            THEN step.absolute_value * 10000
                        ELSE step.absolute_value
                    END AS stat_value
            FROM public.character_pets pet
            JOIN public.character_pet_skills skill
              ON skill.pet_id = pet.id
             AND skill.is_active
            JOIN public.pet_skill_curve_definitions curve
              ON curve.revision = COALESCE(
                  @petLearnedSkillRevision,
                  (
                      SELECT publication.revision
                      FROM public.pet_skill_content_publication publication
                      WHERE publication.singleton
                  ))
             AND curve.first_runtime_skill_id = skill.skill_id
             AND curve.priority = skill.skill_rank
            JOIN LATERAL (
                SELECT candidate.absolute_value
                FROM public.pet_skill_curve_steps candidate
                WHERE candidate.revision = curve.revision
                  AND candidate.family_type = curve.family_type
                  AND candidate.priority = curve.priority
                  AND candidate.minimum_pet_rank::numeric <= pet.rank
                ORDER BY candidate.minimum_pet_rank DESC
                LIMIT 1
            ) step ON true
            WHERE pet.user_id = @characterId
              AND pet.activity_state = 'owned'
              AND pet.is_summoned
            ) projected
            WHERE projected.stat_name IS NOT NULL
        ),
        """;
}
