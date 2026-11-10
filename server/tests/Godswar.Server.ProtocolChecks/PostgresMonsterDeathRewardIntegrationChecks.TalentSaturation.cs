using Godswar.Server.Application.Rewards;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMonsterDeathRewardIntegrationChecks
{
    private static async Task AssertTalentSaturationAsync(string connectionString)
    {
        await AssertSaturatedAtlantisWaveCommitsAsync(connectionString);
        await AssertPartialTalentSaturationAsync(connectionString);
        await AssertLegacyTalentSaturationAsync(connectionString);
    }

    private static async Task AssertSaturatedAtlantisWaveCommitsAsync(string connectionString)
    {
        var fixture = await CreateSealedFixtureAsync(connectionString, "talent_wave", 0,
            talentExperience: 99, talentPoints: int.MaxValue, fighterLevel: 160);
        var now = DateTimeOffset.UtcNow;
        var content = AtlantisLiveWaveChecks.Content();
        var map = new MapInstance(WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(205), InstanceKind.Dungeon, 5, now));
        Check.True(map.TryConfigureAtlantisWaves(content, [(fixture.CharacterId, 160)], now) &&
            map.TryStartAtlantisEncounter(now, out _) && map.TrySpawnPendingAtlantisWave(now, out _),
            "a level160 character with capped Talent progression starts its real Atlantis wave");
        Check.True(map.TryGetAtlantisWaveSnapshot(out var firstWave),
            "the saturated progression fixture exposes exact scored-wave membership");
        var firstIds = firstWave.BoundMonsters.Select(monster => monster.ObjectId).ToHashSet();
        var first = map.SnapshotMonsters().Where(monster => monster.IsAlive && firstIds.Contains(monster.ObjectId)).ToArray();
        Check.Equal(12, first.Length, "the saturated progression fixture owns one complete scored group");
        await using var source = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var observed = 0;
        foreach (var monster in first)
        {
            now = now.AddMilliseconds(1);
            Check.True(map.TryApplyMonsterDamage(monster.ObjectId, monster.MaximumHealth, now, out var death) &&
                death.Killed, "the current group monster has an authoritative lethal health mutation");
            var captured = AtlantisMonsterKillScoring.Capture(map, content, map.WorldInstanceId, death);
            Check.True(captured is not null, "the live death captures its exact wave identity and rank");
            Check.True(MonsterDeathRewardCommandEnvelope.TryCreateCommand(death.Monster.RuntimeInstanceId, 205,
                death.ObjectId, death.Monster.SpawnGeneration, death.Monster.HealthRevision,
                120, 250, out var command), "the durable command pins that same real death");
            var envelope = CreateEnvelope(fixture, command);
            void Observe(MonsterDeathRewardExecutionReceipt receipt)
            {
                Check.Equal(command.DeathEventId, receipt.DeathEventId, "the exact death commit is observed");
                observed++;
                AtlantisMonsterKillScoring.RecordCommitted(map, captured!, DateTimeOffset.UtcNow);
            }

            var committed = await executor.ExecuteWithCommitObserverAsync(envelope, Observe);
            Check.True(committed.Disposition == MonsterDeathRewardExecutionDisposition.Committed &&
                committed.Receipt is { } receipt && receipt.RequestedTalentExperience == 250 &&
                receipt.TalentExperienceGained == 0 && receipt.TalentPointsGained == 0 &&
                receipt.CurrentTalentExperience == 99 && receipt.CurrentTalentPoints == int.MaxValue &&
                receipt.ExperienceGained == 120,
                "full Talent progression still commits the death and fighter reward with no phantom Talent grant");
            var replay = await executor.ExecuteWithCommitObserverAsync(envelope, Observe);
            Check.True(replay.Disposition == MonsterDeathRewardExecutionDisposition.Duplicate &&
                replay.Receipt?.ProgressionRevision == committed.Receipt!.ProgressionRevision,
                "saturated death replay retains the original receipt and progression revision");
        }

        Check.True(map.TryGetAtlantisRunSnapshot(out var run) && run.TeamPoints == 30 &&
            map.TryGetAtlantisWaveSnapshot(out var wave) && wave.RemainingMonsterCount == 0 &&
            observed == 24,
            "twelve commits and their replays score ten normal plus two elite kills exactly once");
        Check.True(map.TrySpawnPendingAtlantisWave(DateTimeOffset.UtcNow, out var nextWave) &&
            nextWave.WaveIndex == 1 && nextWave.RemainingMonsterCount == 12 &&
            map.SnapshotMonsters().Count(monster => monster.IsAlive &&
                nextWave.BoundMonsters.Any(next => next.ObjectId == monster.ObjectId)) == 12,
            "clearing the capped character's first group publishes the next twelve monsters");
        var state = await ReadSealedStateAsync(connectionString, fixture.CharacterId);
        Check.True(state.Level == 160 && state.Experience == 1440 && state.TalentExperience == 99 &&
            state.TalentPoints == int.MaxValue && state.ProgressionRevision == 12,
            "all twelve distinct deaths commit fighter EXP and keep capped Talent state canonical");
        await using var evidence = source.CreateCommand(
            "SELECT count(*) FROM public.monster_death_reward_settlements WHERE character_id = @character;");
        evidence.Parameters.AddWithValue("character", fixture.CharacterId);
        Check.Equal(12L, Convert.ToInt64(await evidence.ExecuteScalarAsync()),
            "duplicate wave observations never create additional durable death settlements");
    }

    private static async Task AssertPartialTalentSaturationAsync(string connectionString)
    {
        var fixture = await CreateSealedFixtureAsync(connectionString, "talent_partial", 0,
            talentExperience: 95, talentPoints: int.MaxValue - 1, fighterLevel: 160);
        await using var source = NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source).ExecuteAsync(CreateEnvelope(fixture,
            CreateCommand(Guid.NewGuid(), experience: 120, talentExperience: 250)));
        Check.True(result.Disposition == MonsterDeathRewardExecutionDisposition.Committed &&
            result.Receipt is { } receipt && receipt.RequestedTalentExperience == 250 &&
            receipt.TalentExperienceGained == 104 && receipt.TalentPointsGained == 1 &&
            receipt.CurrentTalentPoints == int.MaxValue && receipt.CurrentTalentExperience == 99,
            "the durable receipt distinguishes requested Talent EXP from the remaining credited capacity");
    }

    private static async Task AssertLegacyTalentSaturationAsync(string connectionString)
    {
        var fixture = await CreateSealedFixtureAsync(connectionString, "talent_legacy", 0,
            talentExperience: 95, talentPoints: int.MaxValue - 1, fighterLevel: 160);
        await using var store = new PostgresGameStore(connectionString);
        var partial = await store.ApplyMonsterKillRewardAsync(fixture.AccountId, fixture.CharacterId,
            experience: 120, talentExperience: 250);
        Check.True(partial is not null && partial.TalentExperienceGained == 104 &&
            partial.TalentPointsGained == 1 && partial.CurrentTalentPoints == int.MaxValue &&
            partial.CurrentTalentExperience == 99,
            "legacy rewards use the same representable Talent credit at the point ceiling");
        var saturated = await store.ApplyMonsterKillRewardAsync(fixture.AccountId, fixture.CharacterId,
            experience: 120, talentExperience: 250);
        Check.True(saturated is not null && saturated.TalentExperienceGained == 0 &&
            saturated.TalentPointsGained == 0 && saturated.CurrentTalentPoints == int.MaxValue &&
            saturated.CurrentTalentExperience == 99 && saturated.ExperienceGained == 120,
            "fully saturated legacy Talent state still permits the fighter reward");
    }
}
