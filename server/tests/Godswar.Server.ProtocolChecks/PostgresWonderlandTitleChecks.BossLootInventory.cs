using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task CheckBossLootCapacityAndAttributesAsync(NpgsqlDataSource source)
    {
        var run = await CreateAsync(source, count: 1);
        var request = BossRequest(run, 46100, 4450);
        await using (var fill = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,
                attribute1,attribute_level1,item_exp)
            SELECT @character,1,slot::smallint,1007,1,1,0,1,1,2,12345 FROM generate_series(0,95) slot;
            """))
        {
            fill.Parameters.AddWithValue("character", request.Subject.CharacterId);
            Check.Equal(96, await fill.ExecuteNonQueryAsync(), "fill all normal bag slots with owned test items");
        }
        var store = new PostgresWonderlandChestClaimStore(source);
        var full = await BossLootStateAsync(source, request.Subject.CharacterId);
        Check.True((await store.ClaimBossAsync(request)).Status == WonderlandChestClaimStatus.InventoryFull &&
            await BossLootStateAsync(source, request.Subject.CharacterId) == full,
            "a full bag leaves the boss claim, all attributes, inventory revision, and evidence untouched");
        await using (var free = source.CreateCommand("DELETE FROM character_items WHERE user_id=@character AND item_location=1 AND slot_index=95;"))
        {
            free.Parameters.AddWithValue("character", request.Subject.CharacterId);
            Check.Equal(1, await free.ExecuteNonQueryAsync(), "free exactly one test inventory slot");
        }
        var protectedItems = await BossProtectedItemsAsync(source, request.Subject.CharacterId);
        Check.True((await store.ClaimBossAsync(request)).Status == WonderlandChestClaimStatus.Claimed,
            "a rejected corpse claim can be retried after making one bag slot available");
        Check.Equal(protectedItems, await BossProtectedItemsAsync(source, request.Subject.CharacterId),
            "granting a new sack preserves every owned non-reward item's full database representation");
        await AssertBossLootAsync(source, request.Subject.CharacterId, "4450:1:1", 1, 1);

        var stacking = await CreateAsync(source, count: 1);
        var stackRequest = BossRequest(stacking, 46200, 4451);
        await using (var seed = source.CreateCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,item_quality,item_grade,bound,stack,
                attribute1,attribute_level1,item_exp)
            VALUES(@character,1,0,4451,1,1,1,98,1,2,12345);
            """))
        {
            seed.Parameters.AddWithValue("character", stackRequest.Subject.CharacterId);
            await seed.ExecuteNonQueryAsync();
        }
        var attributes = await BossStackAttributesAsync(source, stackRequest.Subject.CharacterId);
        Check.True((await store.ClaimBossAsync(stackRequest)).Status == WonderlandChestClaimStatus.Claimed,
            "boss loot tops up a compatible existing bound sack stack");
        Check.Equal(attributes, await BossStackAttributesAsync(source, stackRequest.Subject.CharacterId),
            "stack top-up changes only stack and update timestamp, preserving every other authoritative attribute");
        await AssertBossLootAsync(source, stackRequest.Subject.CharacterId, "4451:1:99", 1, 1);
        var otherBoss = BossRequest(stacking, 46201, 4451);
        Check.True((await store.ClaimBossAsync(otherBoss)).Status == WonderlandChestClaimStatus.Claimed,
            "the second island-two boss creates a separate entitlement when the first stack reaches ninety-nine");
        await AssertBossLootAsync(source, stackRequest.Subject.CharacterId, "4451:1:100", 2, 2);
        await using var stacks = source.CreateCommand("SELECT count(*),max(stack) FROM character_items WHERE user_id=@character AND prop_id=4451;");
        stacks.Parameters.AddWithValue("character", stackRequest.Subject.CharacterId);
        await using var reader = await stacks.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() && reader.GetInt64(0) == 2 && reader.GetInt16(1) == 99,
            "sacks split into a second stack while preserving the native stack cap");
    }

    private static async Task<string> BossProtectedItemsAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(i) ORDER BY id),'[]'::jsonb)::text
            FROM character_items i WHERE user_id=@character AND prop_id NOT BETWEEN 4450 AND 4461;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> BossStackAttributesAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT (to_jsonb(i)-'stack'-'updated_at')::text FROM character_items i
            WHERE user_id=@character AND item_location=1 AND slot_index=0;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertBossLootAsync(NpgsqlDataSource source, int characterId,
        string expected, long revision, long claims)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT string_agg(prop_id::text||':'||bound::text||':'||total::text,',' ORDER BY prop_id,bound)
                    FROM (SELECT prop_id,bound,sum(stack) AS total FROM character_items
                        WHERE user_id=@character AND prop_id BETWEEN 4450 AND 4461 GROUP BY prop_id,bound) sacks),
                inventory_revision,(SELECT count(*) FROM wonderland_boss_loot_claims WHERE character_id=@character),
                (SELECT count(*) FROM character_inventory_ledger WHERE character_id=@character AND reason_code='wonderland_boss_loot_claim'),
                "Money","Stone","BindingGold",fighter_job_exp,
                (SELECT count(*) FROM wonderland_title_members WHERE character_id=@character),
                (SELECT count(*) FROM wonderland_chest_claims WHERE character_id=@character)
            FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "boss loot recipient remains persisted");
        Check.Equal(expected, reader.IsDBNull(0) ? "<none>" : reader.GetString(0), "native sack IDs, quantities, and bindings match");
        Check.Equal(revision, reader.GetInt64(1), "one inventory revision per new boss claim");
        Check.Equal(claims, reader.GetInt64(2), "exact durable per-boss claim count");
        Check.Equal(claims, reader.GetInt64(3), "one inventory mutation ledger entry per awarded sack");
        Check.Equal(10000, reader.GetInt32(4), "boss loot preserves Silver");
        Check.Equal(10, reader.GetInt32(5), "boss loot preserves Gold");
        Check.Equal(0, reader.GetInt32(6), "boss loot preserves Bound Gold");
        Check.Equal(0L, reader.GetInt64(7), "boss loot preserves character EXP");
        Check.Equal(0L, reader.GetInt64(8), "boss sack claims do not wait for or create island title settlement");
        Check.Equal(0L, reader.GetInt64(9), "corpse sacks do not consume the separate island chest entitlement");
    }
}
