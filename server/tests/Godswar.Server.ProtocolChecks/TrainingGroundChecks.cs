using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// TRAINING: Training Ground (Trials of Heracles, map 210) rules - entry route,
/// destination, daily allowance, 300-point scoring (elite 10, normal 1), the
/// reward tiers, respawn (elites once per run, normals ordinary), the
/// cross-map template list, and that no other map or instance changed.
/// </summary>
internal static class TrainingGroundChecks
{
    public const string CheckName =
        "Training Ground: page 231 entry, 300 points, rewards, respawn, map 210 instance";

    public static Task RunAsync()
    {
        CheckEntry();
        CheckScoring();
        CheckRewards();
        CheckContent();
        CheckIsolation();
        return Task.CompletedTask;
    }

    private static int[] ArgumentsFor(int first)
    {
        var arguments = Enumerable.Repeat(-1, InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = first;
        return arguments;
    }

    private static void CheckEntry()
    {
        Check.True(
            InstanceCallerProtocol.TryResolveHeraclesTrialEntry(
                InstanceCallerProtocol.DialogIndex,
                ArgumentsFor(InstanceCallerProtocol.HeraclesTrialPageSubId),
                out _, out _, out _) &&
            !InstanceCallerProtocol.TryResolveHeraclesTrialEntry(
                InstanceCallerProtocol.DialogIndex,
                ArgumentsFor(InstanceCallerProtocol.HarborAttackPageSubId),
                out _, out _, out _),
            "page 231 (and only 231) is the Training Ground entry");

        var destination = InstanceCallerProtocol.TrainingGroundDestination;
        Check.True(
            destination.Kind == InstanceCallerEntryKind.TrainingGround &&
            (int)destination.Kind == 10 &&
            destination.TargetMapId == 210 &&
            destination.ClientSceneId == 256 &&
            destination.TargetX == 128f && destination.TargetZ == -133f &&
            destination.MinimumLevel == 60 && destination.MaximumLevel == 200 && // FINALISE2 (the admin F-16)
            destination.RequiredPartySize is null && destination.MinimumPartySize == 1,
            "destination: kind 10, map 210, scene 256, arrival (128,-133), level 75-130, party 1-5");
        Check.True(
            InstanceCallerProtocol.UsesPerMemberEntryWindow(InstanceCallerEntryKind.TrainingGround) &&
            InstanceCallerProtocol.TryResolveInvitedInstance(256, out var invited) &&
            invited.Kind == InstanceCallerEntryKind.TrainingGround,
            "the party's shared Enter window and the invitation resolve scene 256");
        Check.True(
            LegacyInstanceDailyEntryPolicy.GetDefaultFreeEntryLimit(InstanceCallerEntryKind.TrainingGround) == 1 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultPaidRetryLimit(InstanceCallerEntryKind.TrainingGround) == 0 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(InstanceCallerEntryKind.TrainingGround) == 1,
            "one entry a day, no paid retry (IGG acid=166)");
        Check.True(
            ServerRatesSnapshot.Default.Absolute(ServerRateKeys.TrainingGroundEntry) == 0 &&
            InstanceCallerProtocol.ResolveClosedInstance(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.HeraclesTrialRootSubId,
                ArgumentsFor(InstanceCallerProtocol.HeraclesTrialPageSubId),
                harborAttackOpen: true,
                trainingGroundOpen: false) == InstanceCallerProtocol.ClosedInstance.TrainingGround,
            "the switch still defaults closed and a closed switch still refuses page 231");

        var ddl = PostgresTrainingGroundStore.SchemaSql;
        Check.True(
            ddl.Contains("CREATE TABLE IF NOT EXISTS public.training_ground_rewards", StringComparison.Ordinal) &&
            ddl.Contains("PRIMARY KEY (run_id, character_id)", StringComparison.Ordinal) &&
            ddl.Contains("OR instance_kind = 10", StringComparison.Ordinal) &&
            ddl.Contains("strpos(", StringComparison.Ordinal) &&
            ddl.Contains("VALUES (10, 1, 0)", StringComparison.Ordinal) &&
            ddl.Contains("ON CONFLICT DO NOTHING", StringComparison.Ordinal) &&
            !ddl.Contains("REFERENCES", StringComparison.OrdinalIgnoreCase) &&
            !PostgresSchemaMigrationCatalog.All.Any(static m =>
                m.Sql.Contains("training_ground_rewards", StringComparison.Ordinal)),
            "first-use DDL: reward table keyed (run, character), kind checks only widened, no registered migration");
        // Ratchet: the first-use DDL widens the two instance-kind checks. A later
        // registered migration that re-creates them (e.g. BETWEEN 1 AND 3) would
        // fail on live once kind-10 rows exist, so none may touch them.
        Check.True(
            PostgresSchemaMigrationCatalog.All
                .Where(static m => string.CompareOrdinal(m.Id, "20260929_216_harbor_attack_daily_entry") > 0)
                .All(static m =>
                    !m.Sql.Contains("ck_legacy_instance_settings_instance_kind", StringComparison.Ordinal) &&
                    !m.Sql.Contains("ck_legacy_instance_daily_entries_instance_kind", StringComparison.Ordinal)),
            "no registered migration after 216 re-creates the instance-kind checks the Training Ground widens");
    }

    private static void CheckScoring()
    {
        var at = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var progress = new TrainingGroundRunProgress();
        Check.Equal((int)TrainingGroundKillOutcome.Scored,
            (int)progress.Record(46001, 1, "A_normal_spider_006", at), "a normal scores");
        Check.Equal((int)TrainingGroundKillOutcome.Ignored,
            (int)progress.Record(46001, 1, "A_normal_spider_006", at), "a replayed kill is ignored");
        Check.Equal((int)TrainingGroundKillOutcome.Scored,
            (int)progress.Record(46001, 2, "A_normal_spider_006", at), "a respawned normal scores again");
        Check.Equal((int)TrainingGroundKillOutcome.Ignored,
            (int)progress.Record(46900, 1, "B_normalSA_robber_001", at), "a template not on the list scores nothing");
        Check.Equal(2, progress.Score, "two normals = 2 points");
        for (var elite = 0; elite < 9; elite++)
        {
            progress.Record(46067u + (uint)elite, 1, "A_elite_spider_006", at);
        }
        Check.Equal(92, progress.Score, "nine elites add 90 points");
        TrainingGroundKillOutcome last = default;
        for (var kill = 0; kill < 208; kill++)
        {
            last = progress.Record(46002u + (uint)(kill % 60), 10u + (uint)kill, "A_normal_scorpion_008", at.AddMinutes(20));
        }
        Check.Equal((int)TrainingGroundKillOutcome.Completed, (int)last, "the 300th point completes the run");
        Check.Equal(300, progress.Score, "score is exactly 300 at completion");
        Check.True(progress.CompletedAt == at.AddMinutes(20) && progress.Elites == 9 && progress.Normals == 210,
            "completion time and counts recorded");
        Check.Equal((int)TrainingGroundKillOutcome.Ignored,
            (int)progress.Record(46075, 1, "A_elite_scorpion_008", at.AddMinutes(21)), "nothing scores after the clear");
        Check.Equal(300, progress.Score, "score unchanged after completion");
    }

    private static void CheckRewards()
    {
        static bool Is(TrainingGroundReward reward, int exp, int tp) =>
            reward.Experience == exp && reward.TalentPoints == tp;
        Check.True(Is(TrainingGroundPolicy.RewardFor(0, null), 60_000, 2), "0 points: 60,000 EXP / 2 TP");
        Check.True(Is(TrainingGroundPolicy.RewardFor(119, null), 60_000, 2), "119 points: lowest tier");
        Check.True(Is(TrainingGroundPolicy.RewardFor(120, null), 240_000, 3), "120 points: 240,000 / 3");
        Check.True(Is(TrainingGroundPolicy.RewardFor(180, null), 288_000, 6), "180 points: 288,000 / 6");
        Check.True(Is(TrainingGroundPolicy.RewardFor(299, null), 312_000, 9), "240-299 points: 312,000 / 9");
        Check.True(Is(TrainingGroundPolicy.RewardFor(300, TimeSpan.FromMinutes(29)), 360_000, 12), "clear in 30 min: 360,000 / 12");
        Check.True(Is(TrainingGroundPolicy.RewardFor(300, TimeSpan.FromMinutes(22.5)), 384_000, 16), "clear in 22.5 min: 384,000 / 16");
        Check.True(Is(TrainingGroundPolicy.RewardFor(300, null), 312_000, 9),
            "300 points with no recorded clear time (cannot happen) never pays the clear tier");
    }

    private static void CheckContent()
    {
        var templates = TrainingGroundPolicy.Templates;
        Check.Equal(11, templates.Count, "11 cross-map templates");
        Check.Equal(4, templates.Values.Count(static t => t.IsElite), "4 elite templates");
        Check.True(templates.Values.All(static t =>
                t.IsElite == t.TemplateKey.Contains("_elite", StringComparison.Ordinal) &&
                !t.TemplateKey.Contains("boss", StringComparison.OrdinalIgnoreCase) &&
                t.SourceMapId != 210),
            "elite keys are elite-rank client keys, no boss key, none from map 210");
        Check.True(TrainingGroundPolicy.TargetScore == 300 &&
            TrainingGroundPolicy.ElitePoints == 10 && TrainingGroundPolicy.NormalPoints == 1 &&
            TrainingGroundPolicy.ResolveTimeLimit(null) == TimeSpan.FromMinutes(30) &&
            TrainingGroundPolicy.ResolveTimeLimit("5") == TimeSpan.FromMinutes(30) &&
            TrainingGroundPolicy.ResolveTimeLimit("120") == TimeSpan.FromMinutes(2),
            "the admin's numbers: 300 points, elite 10, normal 1, 30 minutes");

        Check.True(
            GameSessionRegistry.MonsterRespawnPolicyForMap(210) == MonsterRespawnPolicy.Timed,
            "map 210 keeps the timed respawn (normals come back)");
        var ordinary = TimeSpan.FromSeconds(10);
        foreach (var template in templates.Values)
        {
            var interval = MonsterRespawnPolicyRules.ResolveRespawnInterval(
                WorldBossCatalog.Empty, 210, template.TemplateKey, ordinary);
            Check.True(template.IsElite ? interval == TimeSpan.FromHours(1) : interval == ordinary,
                $"{template.TemplateKey} on 210 respawns after {(template.IsElite ? "1 h (once per run)" : "the ordinary delay")}");
            Check.True(MonsterRespawnPolicyRules.ResolveRespawnInterval(
                    WorldBossCatalog.Empty, template.SourceMapId, template.TemplateKey, ordinary) == ordinary,
                $"{template.TemplateKey} on its own map {template.SourceMapId} is unchanged");
        }
    }

    private static void CheckIsolation()
    {
        Check.True(DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(210) &&
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(210) &&
            // WARRIOR (F-51, intended change): the Test Fields 201-203/206 are now the
            // Warriors' Training Grounds and run as exact instances too.
            DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(201) &&
            DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(206) &&
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(201),
            "210 is an exact-instance map; the harbor set is unchanged");
        Check.True(
            InstanceCallerProtocol.TryResolveInvitedInstance(229, out var harbor) &&
            harbor.Kind == InstanceCallerEntryKind.HarborAttack &&
            InstanceCallerProtocol.TryResolveInvitedInstance(224, out var atlantis) &&
            atlantis.Kind == InstanceCallerEntryKind.Atlantis &&
            LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(InstanceCallerEntryKind.HarborAttack) == 1 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(InstanceCallerEntryKind.Atlantis) == 4,
            "harbor and Atlantis entries and allowances are unchanged");
        Check.True(!TrainingGroundPolicy.IsTrainingGroundMap(208) && TrainingGroundPolicy.ScoreFor("B_bossSA_mage_018") == 0,
            "training scoring never applies to Bay Under Attack templates");
    }
}
