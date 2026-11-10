using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task CheckChestTransactionRollbackAsync(NpgsqlDataSource source)
    {
        var milestone = Copy(await CreateAsync(source, count: 1), island: 5);
        var request = ChestRequest(milestone);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(milestone)).Succeeded,
            "faction reward fixture clear settles");
        var before = await ChestStateAsync(source, request.Subject.CharacterId);
        await using (var fail = source.CreateCommand("""
            CREATE FUNCTION public.reject_wonderland_chest_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected chest receipt failure'; END; $$;
            CREATE TRIGGER reject_wonderland_chest_test BEFORE INSERT ON public.wonderland_chest_claims
            FOR EACH ROW EXECUTE FUNCTION public.reject_wonderland_chest_test();
            """)) await fail.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await new PostgresWonderlandChestClaimStore(source).ClaimAsync(request); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected && await ChestStateAsync(source, request.Subject.CharacterId) == before,
                "receipt failure rolls back the inventory grant, revision, inbox, audit, and ledger");
        }
        finally
        {
            await using var cleanup = source.CreateCommand("""
                DROP TRIGGER reject_wonderland_chest_test ON public.wonderland_chest_claims;
                DROP FUNCTION public.reject_wonderland_chest_test();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await new PostgresWonderlandChestClaimStore(source, new FixedWonderlandGemRoll(15)).ClaimAsync(request)).Status ==
            WonderlandChestClaimStatus.Claimed, "failed transaction leaves a retryable entitlement");
        await AssertChestSacksAsync(source, request.Subject.CharacterId, "4213:1:4", 1, 1, 1);
        var athens = Copy(await CreateAsync(source, count: 1), island: 5);
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(athens)).Succeeded,
            "second faction fixture clear settles");
        var athensClaim = ChestRequest(athens, camp: 1);
        Check.True((await new PostgresWonderlandChestClaimStore(source, new FixedWonderlandGemRoll(0)).ClaimAsync(athensClaim)).Status ==
            WonderlandChestClaimStatus.Claimed, "both factions receive gems independently of their enemy marshal's corpse loot");
        await AssertChestSacksAsync(source, athensClaim.Subject.CharacterId, "4223:1:4", 1, 1, 1);
    }

    private static async Task<string> ChestStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object(
              'items',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.id) FROM character_items i WHERE user_id=@character),
              'revision',c.inventory_revision,'silver',c."Money",'gold',c."Stone",'bgold',c."BindingGold",
              'experience',c.fighter_job_exp,
              'claims',(SELECT count(*) FROM wonderland_chest_claims WHERE character_id=@character),
              'inbox',(SELECT count(*) FROM command_inbox WHERE aggregate_key='character:'||@character::text),
              'audit',(SELECT count(*) FROM command_audit WHERE aggregate_key='character:'||@character::text),
              'ledger',(SELECT count(*) FROM character_inventory_ledger WHERE character_id=@character))::text
            FROM character_base c WHERE c.id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertChestSacksAsync(NpgsqlDataSource source, int characterId, string expected,
        long revision, long claims, long ledger)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT string_agg(prop_id::text||':'||bound::text||':'||stack::text,',' ORDER BY slot_index)
                    FROM character_items WHERE user_id=@character AND (prop_id IN (4213,4223) OR prop_id BETWEEN 4450 AND 4461)),
              inventory_revision,(SELECT count(*) FROM wonderland_chest_claims WHERE character_id=@character),
              (SELECT count(*) FROM character_inventory_ledger WHERE character_id=@character AND reason_code='wonderland_chest_claim'),
              "Money","Stone","BindingGold",fighter_job_exp
            FROM character_base WHERE id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "chest recipient remains persisted");
        Check.Equal(expected, reader.IsDBNull(0) ? "<none>" : reader.GetString(0),
            "native gem identities, bound flags and stack quantities match, with no boss sacks granted by the chest");
        Check.Equal(revision, reader.GetInt64(1), "chest inventory revision advances exactly once per grant");
        Check.Equal(claims, reader.GetInt64(2), "durable chest claim count matches");
        Check.Equal(ledger, reader.GetInt64(3), "durable chest item ledger count matches");
        Check.Equal(10000, reader.GetInt32(4), "chest grant preserves the fixture's Silver balance");
        Check.Equal(10, reader.GetInt32(5), "chest grant preserves the fixture's Gold balance");
        Check.Equal(0, reader.GetInt32(6), "chest grant preserves the fixture's Bound Gold balance");
        Check.Equal(0L, reader.GetInt64(7), "chest grant preserves the fixture's EXP");
    }
}
