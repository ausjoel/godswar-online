using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string PlayerExperiencePillCheckName = "PostgreSQL EXP Pill consumption and fighter progression commit once with cooldown and cap protection";

    public static async Task RunPlayerExperiencePillsAsync()
    {
        var connection = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connection);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        await using var game = new PostgresGameStore(connection);
        await game.EnsureSeedDataAsync();
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        PostgresPetDurableCommandExecutor Executor() => new(source, new PostgresOutboxDispatcherOptions(),
            game.ItemContent, game.PetContent, merge, PetLearnedSkillContentBaseline.Create());
        var fixture = await CreateFixtureAsync(connection);
        await CheckPillProgressionReplayAsync(source, fixture, Executor);
        await CheckPillBoundariesAsync(source, fixture, Executor);
        await CheckPillRollbackAsync(source, fixture, Executor);
    }

    private static async Task CheckPillProgressionReplayAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<PostgresPetDurableCommandExecutor> executor)
    {
        await SeedPlayerExperiencePillAsync(source, fixture, 80, PlayerExperienceCatalog.GetNextLevelExperience(80) - 1, false);
        var before = await ReadPillStateAsync(source, fixture.CharacterId);
        var evidenceBefore = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var envelope = SackEnvelope(fixture);
        var stale = envelope with { Ownership = new PlayerOwnershipFence(Guid.NewGuid(), envelope.Ownership.Generation) };
        var fenced = false;
        try { await executor().ExecuteAsync(stale); }
        catch (PlayerOwnershipValidationException error) when (error.Status == PlayerOwnershipValidationStatus.OwnershipLost) { fenced = true; }
        Check.True(fenced, "stale session ownership cannot use an EXP Pill");
        Check.Equal(before, await ReadPillStateAsync(source, fixture.CharacterId), "ownership rejection preserves EXP and pill");
        Check.Equal(evidenceBefore, await ReadSackEvidenceAsync(source, fixture.CharacterId), "ownership rejection writes no command evidence");
        var service = executor();
        var results = await Task.WhenAll(service.ExecuteAsync(envelope), service.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(results, PetDurableReceiptStatus.PlayerExperienceAdded, "concurrent EXP Pill use");
        var after = await ReadPillStateAsync(source, fixture.CharacterId);
        Check.Equal(before with { Level = 81, Experience = 999_999, Stack = 1,
            ProgressionRevision = before.ProgressionRevision + 1, InventoryRevision = before.InventoryRevision + 1 }, after,
            "one pill credits the full million across level-up and preserves currencies");
        await AssertSackEvidenceDeltaAsync(source, fixture.CharacterId, evidenceBefore, ledger: 1);
        var replay = await executor().ExecuteAsync(envelope);
        Check.True(replay.Disposition == PetDurableExecutionDisposition.Duplicate && replay.Receipt == results[0].Receipt,
            "restart returns the exact EXP progression receipt");
        Check.Equal(after, await ReadPillStateAsync(source, fixture.CharacterId), "replay cannot award EXP or consume twice");
        var cooldown = await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5149);
        Check.True(cooldown is { } natural && natural.ReadyAt - natural.UpdatedAt == TimeSpan.FromSeconds(1),
            "native skill-5149 cooldown advances exactly one second");
        await ExtendConsumableCooldownForAssertionAsync(source, fixture.CharacterId, 5149);
        var cooldownBefore = await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5149);
        var cooldownEnvelope = SackEnvelope(fixture);
        var rejected = await service.ExecuteAsync(cooldownEnvelope);
        var rejectedReplay = await executor().ExecuteAsync(cooldownEnvelope);
        Check.True(rejected.Receipt?.Status == PetDurableReceiptStatus.ConsumableCooldownActive &&
            rejectedReplay.Disposition == PetDurableExecutionDisposition.Duplicate && rejectedReplay.Receipt == rejected.Receipt,
            "a second operation inside cooldown rejects durably");
        Check.Equal(after, await ReadPillStateAsync(source, fixture.CharacterId), "cooldown cannot consume or award anything");
        Check.True(cooldownBefore == await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5149),
            "rejected cooldown retries do not extend the deadline");
    }

    private static async Task CheckPillBoundariesAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<PostgresPetDurableCommandExecutor> executor)
    {
        foreach (var state in new[] { (80, 123L, true, true), (200, uint.MaxValue - 1_000_000L, true, true),
            (80, uint.MaxValue - 999_999L, true, false), (200, 0L, false, false), (200, (long)uint.MaxValue, true, false) })
        {
            await SeedPlayerExperiencePillAsync(source, fixture, state.Item1, state.Item2, state.Item3);
            var before = await ReadPillStateAsync(source, fixture.CharacterId);
            var result = await executor().ExecuteAsync(SackEnvelope(fixture));
            if (state.Item4)
            {
                Check.True(result.IsSuccess && result.Receipt?.PlayerExperience is { IsValid: true },
                    "fully creditable sealed pill use commits valid evidence");
                Check.Equal(before with { Experience = before.Experience + 1_000_000, Stack = 1,
                    ProgressionRevision = before.ProgressionRevision + 1, InventoryRevision = before.InventoryRevision + 1 },
                    await ReadPillStateAsync(source, fixture.CharacterId), "sealed pill stores exactly one million without level changes");
            }
            else
            {
                Check.True(result.Receipt?.Status == PetDurableReceiptStatus.PlayerExperienceMaximumReached,
                    "partially creditable or unsealed maximum-level pill use rejects");
                Check.Equal(before, await ReadPillStateAsync(source, fixture.CharacterId), "cap rejection preserves the complete pill and EXP");
            }
        }
    }
}
