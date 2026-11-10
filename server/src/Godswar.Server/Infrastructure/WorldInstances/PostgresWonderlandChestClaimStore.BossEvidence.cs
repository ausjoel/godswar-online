using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandChestClaimStore
{
    private static async Task<long> WriteBossEvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandBossLootClaimRequest first, long revision,
        IReadOnlyList<WonderlandChestItemReward> rewards, IReadOnlyList<ChestInventoryMutation> mutations, CancellationToken token)
    {
        var operation = new byte[20];
        first.WorldInstanceId.Value.TryWriteBytes(operation);
        BinaryPrimitives.WriteInt32BigEndian(operation.AsSpan(16), checked((int)first.BossObjectId));
        var payload = JsonSerializer.Serialize(new { source = "boss_corpse",
            boss = BossProof(first), rewards, inventoryRevision = revision });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        await using var command = new NpgsqlCommand("""
            WITH audit AS (
                INSERT INTO command_audit(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                    operation_id,request_hash,outcome_code,detail_payload)
                VALUES('account',@principal,'character',@aggregate,'wonderland_boss_loot_claim',@operation,@hash,'committed',@payload)
                RETURNING id)
            INSERT INTO command_inbox(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                operation_id,request_hash,result_contract_version,result_code,result_payload,result_hash,audit_id)
            SELECT 'account',@principal,'character',@aggregate,'wonderland_boss_loot_claim',@operation,@hash,1,'committed',@payload,@hash,id
            FROM audit RETURNING id;
            """, connection, transaction);
        command.Parameters.AddWithValue("principal", first.Subject.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("aggregate", $"character:{first.Subject.CharacterId}");
        command.Parameters.Add("operation", NpgsqlDbType.Bytea).Value = operation;
        command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        var inbox = await command.ExecuteScalarAsync(token) is long id && id > 0 ? id :
            throw new InvalidDataException("Boss loot evidence has no command identity.");
        for (var ordinal = 0; ordinal < mutations.Count; ordinal++)
        {
            var mutation = mutations[ordinal];
            await using var ledger = new NpgsqlCommand("""
                INSERT INTO character_inventory_ledger(command_inbox_id,account_id,character_id,inventory_revision,
                    entry_ordinal,item_instance_id,mutation_kind,before_state,after_state,reason_code)
                VALUES(@inbox,@account,@character,@revision,@ordinal,@item,@kind,@before,@after,'wonderland_boss_loot_claim');
                """, connection, transaction);
            AddBossIdentity(ledger, first);
            ledger.Parameters.AddWithValue("inbox", inbox);
            ledger.Parameters.AddWithValue("revision", revision);
            ledger.Parameters.AddWithValue("ordinal", checked((short)ordinal));
            ledger.Parameters.AddWithValue("item", mutation.Id);
            ledger.Parameters.AddWithValue("kind", mutation.Kind);
            ledger.Parameters.Add("before", NpgsqlDbType.Jsonb).Value = (object?)mutation.Before ?? DBNull.Value;
            ledger.Parameters.Add("after", NpgsqlDbType.Jsonb).Value = mutation.After;
            if (await ledger.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Boss loot ledger write failed.");
        }
        return inbox;
    }

    private static async Task InsertBossClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandBossLootClaimRequest request, long revision, long inbox, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO wonderland_boss_loot_claims(world_instance_id,boss_object_id,spawn_generation,death_event_id,
                island_number,character_id,account_id,realm_id,party_camp,sack_item_id,request_hash,evidence,
                inventory_revision,command_inbox_id)
            VALUES(@instance,@boss,@generation,@death,@island,@character,@account,@realm,@camp,@sack,@hash,@proof,@revision,@inbox);
            """, connection, transaction);
        AddBossIdentity(command, request);
        command.Parameters.AddWithValue("generation", checked((int)request.SpawnGeneration));
        command.Parameters.AddWithValue("death", request.DeathEventId);
        command.Parameters.AddWithValue("camp", checked((short)request.PartyCamp));
        command.Parameters.AddWithValue("sack", checked((int)request.SackItemId));
        command.Parameters.AddWithValue("hash", BossProofHash(request));
        command.Parameters.Add("proof", NpgsqlDbType.Jsonb).Value = BossProof(request);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("inbox", inbox);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Boss loot claim write failed.");
    }
}
