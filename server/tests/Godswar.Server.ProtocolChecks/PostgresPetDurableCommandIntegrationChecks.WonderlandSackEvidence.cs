using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    private static async Task SeedWonderlandSackAsync(NpgsqlDataSource source, PetFixture fixture, uint sackId, short quantity)
    {
        await using var command = source.CreateCommand("""
            DELETE FROM character_items WHERE user_id=@character AND item_location=1;
            DELETE FROM character_bag_consumable_cooldowns WHERE character_id=@character AND cooldown_group BETWEEN 5400 AND 5411;
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            VALUES(@character,1,@slot,@sack,1,1,1,@quantity);
            """);
        command.Parameters.AddWithValue("character", fixture.CharacterId);
        command.Parameters.AddWithValue("slot", checked((short)fixture.EggSlot));
        command.Parameters.AddWithValue("sack", checked((int)sackId));
        command.Parameters.AddWithValue("quantity", quantity);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task FillSackBagAsync(NpgsqlDataSource source, PetFixture fixture, bool fragmented)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack)
            SELECT @character,1,slot::smallint,@item,1,1,@bound,@quantity FROM generate_series(0,95) slot WHERE slot<>@source;
            """);
        command.Parameters.AddWithValue("character", fixture.CharacterId);
        command.Parameters.AddWithValue("source", fixture.EggSlot);
        command.Parameters.AddWithValue("item", fragmented ? 10134 : 1007);
        command.Parameters.AddWithValue("bound", fragmented ? (short)1 : (short)0);
        command.Parameters.AddWithValue("quantity", fragmented ? (short)98 : (short)1);
        Check.Equal(95, await command.ExecuteNonQueryAsync(), "fixture fills every bag slot beside the sack");
    }

    private static async Task AssertSackInventoryAsync(NpgsqlDataSource source, int characterId,
        int sackQuantity, uint rewardId, int rewardQuantity, int rewardStacks)
    {
        await using var command = source.CreateCommand("""
            SELECT COALESCE(sum(stack) FILTER (WHERE prop_id BETWEEN 4450 AND 4461),0),
              COALESCE(sum(stack) FILTER (WHERE prop_id=@reward),0),count(*) FILTER (WHERE prop_id=@reward),
              bool_and(bound=1 AND stack BETWEEN 1 AND 99) FILTER (WHERE prop_id=@reward)
            FROM character_items WHERE user_id=@character AND item_location=1;
            """);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("reward", checked((int)rewardId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "sack inventory evidence returns one row");
        Check.Equal((long)sackQuantity, reader.GetInt64(0), "exactly one source sack was consumed");
        Check.Equal((long)rewardQuantity, reader.GetInt64(1), "exact selected reward quantity is persisted");
        Check.Equal((long)rewardStacks, reader.GetInt64(2), "reward stacks respect native capacity");
        Check.True(!reader.IsDBNull(3) && reader.GetBoolean(3), "bound sack provenance and stack-99 cap are preserved");
    }

    private static async Task<string> ReadSackProtectedStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object('silver',c."Money",'gold',c."Stone",'bgold',c."BindingGold",
              'exp',c.fighter_job_exp,'level',c.fighter_job_lv,
              'equipment',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.id) FROM character_items i WHERE user_id=c.id AND item_location<>1),
              'pets',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM character_pets p WHERE user_id=c.id))::text
            FROM character_base c WHERE c.id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadSackBusinessStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object('silver',c."Money",'gold',c."Stone",'bgold',c."BindingGold",
              'exp',c.fighter_job_exp,'revision',c.inventory_revision,
              'items',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.id) FROM character_items i WHERE user_id=c.id))::text
            FROM character_base c WHERE c.id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SackEvidenceCounts> ReadSackEvidenceAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT inventory_revision,
              COALESCE((SELECT current_version FROM pet_durable_stream_versions WHERE character_id=@character),0),
              (SELECT count(*) FROM character_inventory_ledger WHERE character_id=@character),
              (SELECT count(*) FROM command_audit WHERE aggregate_type='character_pet_value' AND aggregate_key=@aggregate),
              (SELECT count(*) FROM command_inbox WHERE aggregate_type='character_pet_value' AND aggregate_key=@aggregate),
              (SELECT count(*) FROM outbox_events WHERE aggregate_type='character_pet_value' AND aggregate_key=@aggregate),
              (SELECT count(*) FROM outbox_events WHERE aggregate_type='character_inventory' AND aggregate_key=@inventory)
            FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("aggregate", $"character:{characterId}");
        command.Parameters.AddWithValue("inventory", $"character:{characterId}:inventory");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "sack command evidence returns one row");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }

    private static async Task AssertSackEvidenceDeltaAsync(NpgsqlDataSource source, int characterId,
        SackEvidenceCounts before, int ledger)
    {
        var after = await ReadSackEvidenceAsync(source, characterId);
        Check.Equal(before.InventoryRevision + 1, after.InventoryRevision, "one sack increments inventory revision once");
        Check.Equal(before.StreamRevision + 1, after.StreamRevision, "one sack increments activation stream once");
        Check.Equal(before.Ledger + ledger, after.Ledger, "ledger contains exactly the source and reward mutations");
        Check.Equal(before.Audit + 1, after.Audit, "one durable command audit is written");
        Check.Equal(before.Inbox + 1, after.Inbox, "one frozen operation receipt is written");
        Check.Equal(before.ActivationOutbox + 1, after.ActivationOutbox, "activation projection is published once");
        Check.Equal(before.InventoryOutbox + 1, after.InventoryOutbox, "inventory projection is published once");
    }

    private sealed record SackEvidenceCounts(long InventoryRevision, long StreamRevision, long Ledger,
        long Audit, long Inbox, long ActivationOutbox, long InventoryOutbox);
}
