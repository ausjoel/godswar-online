using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    private static async Task SeedPlayerExperiencePillAsync(NpgsqlDataSource source, PetFixture fixture,
        int level, long experience, bool sealedLevel)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@character AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@character AND cooldown_group=5149;
            UPDATE character_base SET fighter_job_lv=@level,fighter_job_exp=@exp,fighter_level_sealed=@sealed WHERE id=@character;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,@slot,4174,1,1,1,2);
            """);
        command.Parameters.AddWithValue("character", fixture.CharacterId);
        command.Parameters.AddWithValue("slot", checked((short)fixture.EggSlot));
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("exp", experience);
        command.Parameters.AddWithValue("sealed", sealedLevel);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PlayerPillState> ReadPillStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT fighter_job_lv,fighter_job_exp,fighter_level_sealed,progression_reward_revision,inventory_revision,
              COALESCE((SELECT sum(stack) FROM character_items WHERE user_id=@character AND item_location=1 AND prop_id=4174),0),
              "Money","Stone","BindingGold" FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "EXP Pill character evidence returns one row");
        return new(reader.GetInt32(0), reader.GetInt64(1), reader.GetBoolean(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8));
    }

    private static async Task CheckPillRollbackAsync(NpgsqlDataSource source, PetFixture fixture,
        Func<PostgresPetDurableCommandExecutor> executor)
    {
        await SeedPlayerExperiencePillAsync(source, fixture, 80, 123, true);
        var before = await ReadPillStateAsync(source, fixture.CharacterId);
        var bag = await ReadSackBusinessStateAsync(source, fixture.CharacterId);
        var evidence = await ReadSackEvidenceAsync(source, fixture.CharacterId);
        var envelope = SackEnvelope(fixture);
        await using (var trigger = source.CreateCommand("""
            CREATE FUNCTION public.reject_player_exp_pill_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected EXP Pill ledger failure'; END; $$;
            CREATE TRIGGER reject_player_exp_pill_test BEFORE INSERT ON public.character_inventory_ledger
            FOR EACH ROW WHEN (NEW.reason_code='player_experience_item_consumed') EXECUTE FUNCTION public.reject_player_exp_pill_test();
            """)) await trigger.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await executor().ExecuteAsync(envelope); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected, "injected durable inventory failure aborts pill use");
            Check.Equal(before, await ReadPillStateAsync(source, fixture.CharacterId),
                "EXP, level, progression and inventory revisions roll back with the pill");
            Check.Equal(bag, await ReadSackBusinessStateAsync(source, fixture.CharacterId), "pill rollback preserves exact owned-item rows");
            Check.Equal(evidence, await ReadSackEvidenceAsync(source, fixture.CharacterId),
                "pill rollback leaves no audit, inbox, ledger or outbox evidence");
            Check.True(await ReadConsumableCooldownAsync(source, fixture.CharacterId, 5149) is null,
                "failed pill use also rolls back cooldown creation and deadline");
        }
        finally
        {
            await using var cleanup = source.CreateCommand("""
                DROP TRIGGER reject_player_exp_pill_test ON public.character_inventory_ledger;
                DROP FUNCTION public.reject_player_exp_pill_test();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await executor().ExecuteAsync(envelope)).IsSuccess, "same uncommitted pill operation retries after fault removal");
        Check.Equal(before with { Experience = 1_000_123, Stack = 1,
            InventoryRevision = before.InventoryRevision + 1, ProgressionRevision = before.ProgressionRevision + 1 },
            await ReadPillStateAsync(source, fixture.CharacterId), "retry grants one exact pill's EXP");
    }

    private sealed record PlayerPillState(int Level, long Experience, bool Sealed, long ProgressionRevision,
        long InventoryRevision, long Stack, int Silver, int Gold, int BoundGold);
}
