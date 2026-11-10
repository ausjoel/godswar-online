using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task CheckBossLootRollbackAsync(NpgsqlDataSource source)
    {
        var request = BossRequest(await CreateAsync(source, count: 1), 46803, 4461);
        var before = await BossLootStateAsync(source, request.Subject.CharacterId);
        await using (var fail = source.CreateCommand("""
            CREATE FUNCTION public.reject_wonderland_boss_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected boss loot receipt failure'; END; $$;
            CREATE TRIGGER reject_wonderland_boss_test BEFORE INSERT ON public.wonderland_boss_loot_claims
            FOR EACH ROW EXECUTE FUNCTION public.reject_wonderland_boss_test();
            """)) await fail.ExecuteNonQueryAsync();
        try
        {
            var rejected = false;
            try { await new PostgresWonderlandChestClaimStore(source).ClaimBossAsync(request); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.RaiseException) { rejected = true; }
            Check.True(rejected && await BossLootStateAsync(source, request.Subject.CharacterId) == before,
                "a final receipt failure rolls back the boss sack, inventory revision, baseline, audit, inbox, and ledger");
        }
        finally
        {
            await using var cleanup = source.CreateCommand("""
                DROP TRIGGER reject_wonderland_boss_test ON public.wonderland_boss_loot_claims;
                DROP FUNCTION public.reject_wonderland_boss_test();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await new PostgresWonderlandChestClaimStore(source).ClaimBossAsync(request)).Status ==
            WonderlandChestClaimStatus.Claimed, "rollback leaves the original boss death retryable");
        await AssertBossLootAsync(source, request.Subject.CharacterId, "4461:1:1", 1, 1);
    }

    private static async Task<string> BossLootStateAsync(NpgsqlDataSource source, int characterId)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object(
                'items',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.id) FROM character_items i WHERE user_id=@character),
                'revision',c.inventory_revision,'silver',c."Money",'gold',c."Stone",'bgold',c."BindingGold",
                'experience',c.fighter_job_exp,
                'claims',(SELECT jsonb_agg(to_jsonb(b) ORDER BY boss_object_id) FROM wonderland_boss_loot_claims b WHERE character_id=@character),
                'chests',(SELECT count(*) FROM wonderland_chest_claims WHERE character_id=@character),
                'baseline',(SELECT jsonb_agg(to_jsonb(b)) FROM character_economy_baseline b WHERE character_id=@character),
                'inbox',(SELECT count(*) FROM command_inbox WHERE aggregate_key='character:'||@character::text),
                'audit',(SELECT count(*) FROM command_audit WHERE aggregate_key='character:'||@character::text),
                'ledger',(SELECT count(*) FROM character_inventory_ledger WHERE character_id=@character))::text
            FROM character_base c WHERE c.id=@character;
            """);
        command.Parameters.AddWithValue("character", characterId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
