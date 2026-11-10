namespace Godswar.Server.State;

internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    private const string DonatorMaximumHealthSelects =
        """
            donator_stats.max_hp AS max_hp,
            GREATEST(0, ROUND(cb."MaxMP" + COALESCE(stats.max_mp, 0)))::integer AS max_mp,
            LEAST(GREATEST(cb."curHP", 0), donator_stats.max_hp) AS current_hp,
            LEAST(GREATEST(cb."curMP", 0), GREATEST(0, ROUND(cb."MaxMP" + COALESCE(stats.max_mp, 0)))::integer) AS current_mp,
        """;

    private const string DonatorPhysicalAttackSelect =
        "donator_stats.physical_attack AS physical_attack,";

    private const string DonatorMagicAttackSelect =
        "donator_stats.magic_attack AS magic_attack,";

    // A getter avoids cross-partial static-field initialization ordering:
    // CalculatedStatsForCharacter evaluates this fragment during type init.
    private static string DonatorBenefitJoins =>
        $$"""
        JOIN public.accounts account
          ON account.id = cb.account_id
        LEFT JOIN (
            VALUES
                ({{(short)DonatorTier.KijinPatron}}, {{DonatorBenefits.MaximumHitPointsBonusBasisPoints(DonatorTier.KijinPatron)}}, {{DonatorBenefits.AttackBonusBasisPoints(DonatorTier.KijinPatron)}}),
                ({{(short)DonatorTier.OniPatron}}, {{DonatorBenefits.MaximumHitPointsBonusBasisPoints(DonatorTier.OniPatron)}}, {{DonatorBenefits.AttackBonusBasisPoints(DonatorTier.OniPatron)}}),
                ({{(short)DonatorTier.DemonLordSeed}}, {{DonatorBenefits.MaximumHitPointsBonusBasisPoints(DonatorTier.DemonLordSeed)}}, {{DonatorBenefits.AttackBonusBasisPoints(DonatorTier.DemonLordSeed)}}),
                ({{(short)DonatorTier.TrueDemonLord}}, {{DonatorBenefits.MaximumHitPointsBonusBasisPoints(DonatorTier.TrueDemonLord)}}, {{DonatorBenefits.AttackBonusBasisPoints(DonatorTier.TrueDemonLord)}}),
                ({{(short)DonatorTier.OctagramPatron}}, {{DonatorBenefits.MaximumHitPointsBonusBasisPoints(DonatorTier.OctagramPatron)}}, {{DonatorBenefits.AttackBonusBasisPoints(DonatorTier.OctagramPatron)}})
        ) donator_benefit(
            tier,
            maximum_health_bonus_basis_points,
            attack_bonus_basis_points)
          ON donator_benefit.tier = account.donator_tier
         AND (
             account.donator_expires_at IS NULL
             OR account.donator_expires_at > now()
         )
        CROSS JOIN LATERAL (
            SELECT
                LEAST(
                    2147483647::numeric,
                    GREATEST(
                        1::numeric,
                        ROUND(
                            (cb."MaxHP" + COALESCE(stats.max_hp, 0)) *
                            (10000 + COALESCE(
                                donator_benefit.maximum_health_bonus_basis_points,
                                0)) / 10000
                        )
                    )
                )::integer AS max_hp,
                LEAST(
                    2147483647::numeric,
                    GREATEST(
                        -2147483648::numeric,
                        ROUND(
                            COALESCE(stats.physical_attack, 0) *
                            (10000 + COALESCE(
                                donator_benefit.attack_bonus_basis_points,
                                0)) / 10000
                        )
                    )
                )::integer AS physical_attack,
                LEAST(
                    2147483647::numeric,
                    GREATEST(
                        -2147483648::numeric,
                        ROUND(
                            COALESCE(stats.magic_attack, 0) *
                            (10000 + COALESCE(
                                donator_benefit.attack_bonus_basis_points,
                                0)) / 10000
                        )
                    )
                )::integer AS magic_attack
        ) donator_stats
        """;
}
