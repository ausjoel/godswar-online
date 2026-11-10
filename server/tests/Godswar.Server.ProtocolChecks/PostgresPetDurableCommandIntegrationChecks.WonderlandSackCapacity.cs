using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    private static async Task CheckSackCapacityAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<IWonderlandSackRollSource, PostgresPetDurableCommandExecutor> executor)
    {
        await SeedWonderlandSackAsync(source, fixture, 4461, 2);
        await FillSackBagAsync(source, fixture, fragmented: false);
        var before = await ReadSackBusinessStateAsync(source, fixture.CharacterId);
        var rolls = new SackTestRollSource(0);
        var service = executor(rolls);
        var envelope = SackEnvelope(fixture);
        var rejected = await service.ExecuteAsync(envelope);
        Check.True(rejected.Disposition == PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status == PetDurableReceiptStatus.WonderlandSackBagFull,
            "full bag rejects before consuming any sack or selecting an outcome");
        Check.Equal(0, rolls.Calls, "full-bag rejection cannot selectively reroll outcomes");
        Check.Equal(before, await ReadSackBusinessStateAsync(source, fixture.CharacterId), "full bag preserves source and inventory revision");
        var repeated = await executor(rolls).ExecuteAsync(envelope);
        Check.True(repeated.Disposition == PetDurableExecutionDisposition.Duplicate && repeated.Receipt == rejected.Receipt,
            "full-bag rejection also replays its original operation");
        await using (var free = source.CreateCommand("DELETE FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=95;"))
        {
            free.Parameters.AddWithValue("character", fixture.CharacterId);
            Check.Equal(1, await free.ExecuteNonQueryAsync(), "one free slot accommodates every possible single outcome");
        }
        Check.True((await service.ExecuteAsync(SackEnvelope(fixture))).IsSuccess, "a fresh request succeeds after making room");
        await AssertSackInventoryAsync(source, fixture.CharacterId, 1, 10134, 99, 1);
        await ExpireConsumableCooldownAsync(source, fixture.CharacterId, 5411);
        Check.True((await service.ExecuteAsync(SackEnvelope(fixture))).IsSuccess,
            "the final sack's released slot can hold its reward even when the bag starts full");
        await AssertSackInventoryAsync(source, fixture.CharacterId, 0, 10134, 198, 2);
        Check.Equal(2, rolls.Calls, "only two successful sack operations drew a reward");
    }

    private static async Task CheckSackFragmentedStacksAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<IWonderlandSackRollSource, PostgresPetDurableCommandExecutor> executor)
    {
        await SeedWonderlandSackAsync(source, fixture, 4461, 1);
        await FillSackBagAsync(source, fixture, fragmented: true);
        var before = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var result = await executor(new SackTestRollSource(0)).ExecuteAsync(SackEnvelope(fixture));
        Check.True(result.IsSuccess, "99-item reward can fill 95 partial stacks and the released sack slot atomically");
        await AssertSackInventoryAsync(source, fixture.CharacterId, 0, 10134, 95 * 98 + 99, 96);
        await AssertSackEvidenceDeltaAsync(source, fixture.CharacterId, before, ledger: 97);
    }

    private static async Task CheckSackRollbackAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<IWonderlandSackRollSource, PostgresPetDurableCommandExecutor> executor)
    {
        await SeedWonderlandSackAsync(source, fixture, 4456, 1);
        var before = await ReadSackBusinessStateAsync(source, fixture.CharacterId);
        var evidence = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var envelope = SackEnvelope(fixture);
        await using (var trigger = source.CreateCommand("""
            CREATE FUNCTION public.reject_wonderland_sack_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected sack reward ledger failure'; END; $$;
            CREATE TRIGGER reject_wonderland_sack_test BEFORE INSERT ON public.character_inventory_ledger
            FOR EACH ROW WHEN (NEW.reason_code='wonderland_sack_reward') EXECUTE FUNCTION public.reject_wonderland_sack_test();
            """)) await trigger.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await executor(new SackTestRollSource(0)).ExecuteAsync(envelope); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected, "injected persistence failure aborts the sack operation");
            Check.Equal(before, await ReadSackBusinessStateAsync(source, fixture.CharacterId),
                "failed ledger write rolls back source consumption and reward grant");
            Check.Equal(evidence, await ReadSackEvidenceAsync(source, fixture.CharacterId),
                "failed grant rolls back revisions, audits, inbox and both outbox streams");
        }
        finally
        {
            await using var cleanup = source.CreateCommand("""
                DROP TRIGGER reject_wonderland_sack_test ON public.character_inventory_ledger;
                DROP FUNCTION public.reject_wonderland_sack_test();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await executor(new SackTestRollSource(0)).ExecuteAsync(envelope)).IsSuccess,
            "an uncommitted operation retries after the persistence fault clears");
        await AssertSackInventoryAsync(source, fixture.CharacterId, 0, 10134, 10, 1);
    }
}
