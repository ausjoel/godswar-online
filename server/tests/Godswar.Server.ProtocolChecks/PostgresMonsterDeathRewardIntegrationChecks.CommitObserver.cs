using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Rewards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMonsterDeathRewardIntegrationChecks
{
    private static async Task AssertCommitObserverOwnershipBoundaryAsync(string connectionString)
    {
        var fixture = await CreateFixtureAsync(connectionString, "observer");
        var now = DateTimeOffset.UtcNow;
        var content = AtlantisLiveWaveChecks.Content();
        var map = new MapInstance(WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(205), InstanceKind.Dungeon, 5, now));
        Check.True(map.TryConfigureAtlantisWaves(content, [(fixture.CharacterId, 90)], now) &&
            map.TryStartAtlantisEncounter(now, out _) && map.TrySpawnPendingAtlantisWave(now, out _),
            "PostgreSQL observer fixture owns a real Atlantis wave");
        var monster = map.SnapshotMonsters().First();
        Check.True(map.TryApplyMonsterDamage(monster.ObjectId, monster.MaximumHealth, now, out var death) && death.Killed,
            "PostgreSQL observer fixture commits authoritative monster health death");
        var captured = AtlantisMonsterKillScoring.Capture(map, content, map.WorldInstanceId, death)!;
        Check.True(MonsterDeathRewardCommandEnvelope.TryCreateCommand(death.Monster.RuntimeInstanceId, 205,
            death.ObjectId, death.Monster.SpawnGeneration, death.Monster.HealthRevision, 120, 0, out var command),
            "PostgreSQL observer command pins the same live death");
        var envelope = CreateEnvelope(fixture, command);
        await using var source = NpgsqlDataSource.Create(connectionString);
        var observed = 0;
        void Observe(MonsterDeathRewardExecutionReceipt receipt)
        {
            Check.Equal(command.DeathEventId, receipt.DeathEventId, "observer receives the exact committed death receipt");
            observed++;
            AtlantisMonsterKillScoring.RecordCommitted(map, captured, DateTimeOffset.UtcNow);
        }

        var beforeFailure = new PostgresMonsterDeathRewardCommandExecutor(source, new PostgresOutboxDispatcherOptions(),
            new CommitObserverProbe(stage => stage == PostgresMonsterDeathRewardCommandStage.BeforeCommit
                ? Task.FromException(new IOException("before commit observer fixture fault"))
                : Task.CompletedTask));
        try
        {
            await beforeFailure.ExecuteWithCommitObserverAsync(envelope, Observe);
            throw new InvalidOperationException("Before-commit fixture fault did not occur.");
        }
        catch (IOException)
        {
        }
        map.TryGetAtlantisRunSnapshot(out var failedRun);
        Check.True(observed == 0 && failedRun.TeamPoints == 0,
            "real rolled-back transaction never acknowledges or scores a monster death");

        var afterCommit = new PostgresMonsterDeathRewardCommandExecutor(source, new PostgresOutboxDispatcherOptions(),
            new CommitObserverProbe(async stage =>
            {
                if (stage != PostgresMonsterDeathRewardCommandStage.AfterCommit) return;
                await using var replace = source.CreateCommand(
                    "UPDATE public.character_base SET checkpoint_owner_generation = checkpoint_owner_generation + 1 WHERE id = @id;");
                replace.Parameters.AddWithValue("id", fixture.CharacterId);
                Check.Equal(1, await replace.ExecuteNonQueryAsync(), "replace claimant ownership after durable commit");
            }));
        try
        {
            await afterCommit.ExecuteWithCommitObserverAsync(envelope, Observe);
            throw new InvalidOperationException("Post-commit ownership guard did not reject replaced claimant.");
        }
        catch (PlayerOwnershipValidationException error)
        {
            Check.True(error.Status == PlayerOwnershipValidationStatus.OwnershipLost,
                "post-commit claimant ownership guard remains enforced");
        }
        map.TryGetAtlantisRunSnapshot(out var committedRun);
        map.TryGetAtlantisWaveSnapshot(out var committedWave);
        Check.True(observed == 1 && committedRun.TeamPoints == 1 && committedWave.RemainingMonsterCount == 11,
            "durable receipt credits the original team before claimant ownership rejection");

        var newOwner = envelope with { Ownership = new(envelope.Ownership.OwnerId, envelope.Ownership.Generation + 1) };
        var replay = await CreateExecutor(source).ExecuteWithCommitObserverAsync(newOwner, Observe);
        map.TryGetAtlantisRunSnapshot(out var replayRun);
        map.TryGetAtlantisWaveSnapshot(out var replayWave);
        Check.True(replay.Disposition == MonsterDeathRewardExecutionDisposition.Duplicate && observed == 2 &&
            replayRun.TeamPoints == 1 && replayWave.RemainingMonsterCount == 11,
            "replacement-owner durable replay acknowledges the same receipt without duplicate team credit");
        await using var evidence = source.CreateCommand(
            "SELECT count(*) FROM public.monster_death_reward_settlements WHERE death_event_id = @death;");
        evidence.Parameters.AddWithValue("death", command.DeathEventId);
        Check.Equal(1L, Convert.ToInt64(await evidence.ExecuteScalarAsync()), "observer recovery never duplicates durable settlement");
    }

    private sealed class CommitObserverProbe(Func<PostgresMonsterDeathRewardCommandStage, Task> action)
        : IPostgresMonsterDeathRewardCommandProbe
    {
        public async ValueTask ReachedAsync(PostgresMonsterDeathRewardCommandStage stage, CancellationToken cancellationToken) =>
            await action(stage);
    }
}
