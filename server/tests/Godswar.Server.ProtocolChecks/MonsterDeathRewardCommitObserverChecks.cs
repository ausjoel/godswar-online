using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterDeathRewardCommitObserverChecks
{
    public const string CheckName = "Monster-death committed receipt observer survives claimant projection failure";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await CheckBeforeCommitFailureAsync(mode);
            await CheckCommittedOwnershipFailureAsync(mode);
            await CheckCommittedPetFailureAndReplayAsync(mode);
        }
    }

    private static async Task CheckBeforeCommitFailureAsync(MonsterRuntimeMode mode)
    {
        var fixture = Create(mode);
        var attempts = 0;
        IMonsterDeathRewardCommandExecutor executor = new CompatibilityExecutor(() =>
        {
            attempts++;
            return Task.FromException<MonsterDeathRewardExecutionResult>(new IOException("before commit"));
        });
        try
        {
            await MonsterDeathRewardCommitBoundary.ExecuteAsync(token => executor.ExecuteWithCommitObserverAsync(
                fixture.Envelope, fixture.Observe, token), true);
        }
        catch (IOException)
        {
        }
        Check.True(attempts == 2 && fixture.Points == 0 && fixture.Remaining == 12,
            $"{mode}: failures without committed receipt neither score nor advance the finite wave");
    }

    private static async Task CheckCommittedOwnershipFailureAsync(MonsterRuntimeMode mode)
    {
        var fixture = Create(mode);
        var concrete = new OwnershipLostExecutor(fixture.Receipt);
        IMonsterDeathRewardCommandExecutor executor = concrete;
        try
        {
            await MonsterDeathRewardCommitBoundary.ExecuteAsync(token => executor.ExecuteWithCommitObserverAsync(
                fixture.Envelope, fixture.Observe, token), true);
            throw new InvalidOperationException("Claimant ownership failure was not retained.");
        }
        catch (PlayerOwnershipValidationException)
        {
        }
        Check.True(concrete.Calls == 1 && fixture.Points == 1 && fixture.Remaining == 11,
            $"{mode}: committed receipt credits original team while claimant ownership failure still escapes");
    }

    private static async Task CheckCommittedPetFailureAndReplayAsync(MonsterRuntimeMode mode)
    {
        var fixture = Create(mode);
        var attempts = 0;
        IMonsterDeathRewardCommandExecutor executor = new CompatibilityExecutor(() =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? MonsterDeathRewardExecutionResult.Committed(fixture.Receipt)
                : MonsterDeathRewardExecutionResult.Duplicate(fixture.Receipt, fixture.Receipt.ToProjection()));
        });
        try
        {
            await MonsterDeathRewardCommitBoundary.ExecuteAsync<MonsterDeathRewardExecutionResult>(async token =>
            {
                await executor.ExecuteWithCommitObserverAsync(fixture.Envelope, fixture.Observe, token);
                throw new IOException("pet experience projection failed after base death commit");
            }, true);
        }
        catch (IOException)
        {
        }
        Check.True(attempts == 2 && fixture.Points == 1 && fixture.Remaining == 11,
            $"{mode}: failed pet extras cannot erase committed death or duplicate team points during replay");
        await executor.ExecuteWithCommitObserverAsync(fixture.Envelope, fixture.Observe);
        Check.True(fixture.Points == 1 && fixture.Remaining == 11,
            "later successful receipt replay cannot credit the original wave member twice");
    }

    private static Fixture Create(MonsterRuntimeMode mode)
    {
        var map = new MapInstance(WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(205), InstanceKind.Dungeon, 5, Start), mode);
        var content = AtlantisLiveWaveChecks.Content();
        Check.True(map.TryConfigureAtlantisWaves(content, [(101, 90)], Start) &&
            map.TryStartAtlantisEncounter(Start, out _) && map.TrySpawnPendingAtlantisWave(Start, out _),
            "observer fixture starts a live Atlantis wave");
        var monster = map.SnapshotMonsters().First();
        Check.True(map.TryApplyMonsterDamage(monster.ObjectId, monster.MaximumHealth, Start.AddSeconds(1), out var death),
            "observer fixture commits live monster health death");
        var captured = AtlantisMonsterKillScoring.Capture(map, content, map.WorldInstanceId, death)!;
        Check.True(MonsterDeathRewardCommandEnvelope.TryCreateCommand(death.Monster.RuntimeInstanceId, 205,
            death.ObjectId, death.Monster.SpawnGeneration, death.Monster.HealthRevision, 0, 0, out var command),
            "observer fixture command binds the same server death");
        var envelope = MonsterDeathRewardCommandEnvelope.Create(new(11, 101),
            new(Guid.NewGuid(), CommandTransportKind.LegacyTcp), Start.AddSeconds(1), command);
        var receipt = new MonsterDeathRewardExecutionReceipt(command.DeathEventId, command.RuntimeInstanceId,
            command.MapId, command.MonsterObjectId, command.SpawnGeneration, command.DeathHealthRevision,
            101, 0, 0, 0, 90, 90, 0, 0, 1, [], 0, 0, 0, 0, 0, 0, 1, "observer-check", Guid.NewGuid());
        return new(map, captured, envelope, receipt);
    }

    private sealed record Fixture(MapInstance Map, AtlantisMonsterKillScoring.CapturedKill Captured,
        CommandEnvelope<MonsterDeathRewardCommand> Envelope, MonsterDeathRewardExecutionReceipt Receipt)
    {
        public void Observe(MonsterDeathRewardExecutionReceipt receipt)
        {
            Check.Equal(Receipt.DeathEventId, receipt.DeathEventId, "receipt acknowledges this exact server death");
            AtlantisMonsterKillScoring.RecordCommitted(Map, Captured, Start.AddSeconds(2));
        }
        public int Points
        {
            get { Map.TryGetAtlantisRunSnapshot(out var snapshot); return snapshot.TeamPoints; }
        }
        public int Remaining
        {
            get { Map.TryGetAtlantisWaveSnapshot(out var snapshot); return snapshot.RemainingMonsterCount; }
        }
    }

    private sealed class CompatibilityExecutor(Func<Task<MonsterDeathRewardExecutionResult>> execute)
        : IMonsterDeathRewardCommandExecutor
    {
        public Task<MonsterDeathRewardExecutionResult> ExecuteAsync(CommandEnvelope<MonsterDeathRewardCommand> envelope,
            CancellationToken cancellationToken = default) => execute();
    }

    private sealed class OwnershipLostExecutor(MonsterDeathRewardExecutionReceipt receipt)
        : IMonsterDeathRewardCommandExecutor
    {
        public int Calls { get; private set; }
        public Task<MonsterDeathRewardExecutionResult> ExecuteAsync(CommandEnvelope<MonsterDeathRewardCommand> envelope,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Observer-aware path required.");
        public Task<MonsterDeathRewardExecutionResult> ExecuteWithCommitObserverAsync(
            CommandEnvelope<MonsterDeathRewardCommand> envelope, Action<MonsterDeathRewardExecutionReceipt> onCommitted,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            onCommitted(receipt);
            return Task.FromException<MonsterDeathRewardExecutionResult>(
                new PlayerOwnershipValidationException(PlayerOwnershipValidationStatus.OwnershipLost));
        }
    }
}
