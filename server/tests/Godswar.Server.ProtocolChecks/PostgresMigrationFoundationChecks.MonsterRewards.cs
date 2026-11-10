using Godswar.Server.Application.Rewards;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckMonsterRewardPolicyMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260831_127_monster_reward_policy");
        var normalizedSql = string.Join(
            ' ',
            migration.Sql.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        Check.True(
            normalizedSql.Contains(
                "setting_id smallint PRIMARY KEY DEFAULT 1 CHECK (setting_id = 1)",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                "maximum_lower_level_gap smallint NOT NULL CHECK (maximum_lower_level_gap BETWEEN 0 AND 199)",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                "VALUES (1, 20, 0, 'migration-127')",
                StringComparison.Ordinal),
            "monster rewards seed one bounded singleton with the reviewed 20-level default");
        Check.True(
            normalizedSql.Contains(
                "NEW.revision := OLD.revision + 1",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                "NEW.updated_at := now()",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                "BEFORE UPDATE ON public.monster_reward_settings",
                StringComparison.Ordinal),
            "monster-reward management updates advance their durable revision and timestamp");

        var globalMigration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260901_129_global_experience_multiplier");
        var normalizedGlobalSql = string.Join(
            ' ',
            globalMigration.Sql.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        Check.True(
            normalizedGlobalSql.Contains(
                "ADD COLUMN global_experience_multiplier_basis_points integer NOT NULL DEFAULT 10000",
                StringComparison.Ordinal) &&
            normalizedGlobalSql.Contains(
                "global_experience_multiplier_basis_points BETWEEN 10000 AND 50000",
                StringComparison.Ordinal),
            "global monster-kill EXP is a separately stored, bounded 1x-5x multiplier");

        var defaultPolicy = MonsterRewardPolicySnapshot.Default;
        defaultPolicy.Validate();
        Check.Equal(
            20,
            defaultPolicy.MaximumLowerLevelGap,
            "compiled monster-reward fallback matches the migration seed");
        Check.Equal(
            10_000,
            defaultPolicy.GlobalExperienceMultiplierBasisPoints,
            "compiled global EXP fallback is the migration's neutral 1x value");
        Check.True(
            defaultPolicy.IsEligible(playerLevel: 140, monsterLevel: 120) &&
            !defaultPolicy.IsEligible(playerLevel: 140, monsterLevel: 119) &&
            defaultPolicy.IsEligible(playerLevel: 140, monsterLevel: 141),
            "default monster-reward gap is inclusive and never rejects higher-level monsters");

        new MonsterRewardPolicySnapshot(
            MaximumLowerLevelGap:
                MonsterRewardPolicySnapshot.MaximumSupportedLowerLevelGap,
            GlobalExperienceMultiplierBasisPoints:
                MonsterRewardPolicySnapshot
                    .MaximumGlobalExperienceMultiplierBasisPoints,
            Revision: 1,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedBy: "boundary-check").Validate();
        Check.Throws<InvalidDataException>(
            () => new MonsterRewardPolicySnapshot(
                MaximumLowerLevelGap: -1,
                GlobalExperienceMultiplierBasisPoints: 10_000,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UnixEpoch,
                UpdatedBy: "boundary-check").Validate(),
            "negative monster-reward gaps are rejected");
        Check.Throws<InvalidDataException>(
            () => new MonsterRewardPolicySnapshot(
                MaximumLowerLevelGap:
                    MonsterRewardPolicySnapshot
                        .MaximumSupportedLowerLevelGap + 1,
                GlobalExperienceMultiplierBasisPoints: 10_000,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UnixEpoch,
                UpdatedBy: "boundary-check").Validate(),
            "monster-reward gaps above the supported level domain are rejected");
        Check.Throws<InvalidDataException>(
            () => new MonsterRewardPolicySnapshot(
                MaximumLowerLevelGap: 20,
                GlobalExperienceMultiplierBasisPoints: 9_999,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UnixEpoch,
                UpdatedBy: "boundary-check").Validate(),
            "global EXP multipliers below 1x are rejected");
        Check.Throws<InvalidDataException>(
            () => new MonsterRewardPolicySnapshot(
                MaximumLowerLevelGap: 20,
                GlobalExperienceMultiplierBasisPoints: 50_001,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UnixEpoch,
                UpdatedBy: "boundary-check").Validate(),
            "global EXP multipliers above 5x are rejected");
        Check.True(
            defaultPolicy.CoordinationRevision() !=
                (defaultPolicy with
                {
                    GlobalExperienceMultiplierBasisPoints = 20_000
                }).CoordinationRevision(),
            "global EXP changes participate in the cross-worker content fingerprint");
        Check.Throws<ArgumentOutOfRangeException>(
            () => defaultPolicy.IsEligible(
                playerLevel: 140,
                monsterLevel: 0),
            "monster-reward eligibility rejects an invalid monster level");
    }
}
