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
    private static async Task<long> WriteEvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandChestClaimRequest request, long revision, IReadOnlyList<WonderlandChestItemReward> rewards, int gemRoll,
        IReadOnlyList<ChestInventoryMutation> mutations, CancellationToken token)
    {
        var operation = new byte[20];
        request.WorldInstanceId.Value.TryWriteBytes(operation);
        BinaryPrimitives.WriteInt32BigEndian(operation.AsSpan(16), request.Island);
        var payload = JsonSerializer.Serialize(new { instanceId = request.WorldInstanceId.Value, island = request.Island,
            milestoneHash = request.MilestoneHash, partyCamp = request.PartyCamp,
            policy = WonderlandChestGemRewardPolicy.Revision, gemRoll, rewards, inventoryRevision = revision });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        await using var command = new NpgsqlCommand("""
            WITH audit AS (
                INSERT INTO command_audit(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                    operation_id,request_hash,outcome_code,detail_payload)
                VALUES('account',@principal,'character',@aggregate,'wonderland_chest_claim',@operation,@digest,'committed',@payload)
                RETURNING id)
            INSERT INTO command_inbox(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                operation_id,request_hash,result_contract_version,result_code,result_payload,result_hash,audit_id)
            SELECT 'account',@principal,'character',@aggregate,'wonderland_chest_claim',@operation,@digest,1,'committed',@payload,@digest,id
            FROM audit RETURNING id;
            """, connection, transaction);
        command.Parameters.AddWithValue("principal", request.Subject.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("aggregate", $"character:{request.Subject.CharacterId}");
        command.Parameters.Add("operation", NpgsqlDbType.Bytea).Value = operation;
        command.Parameters.Add("digest", NpgsqlDbType.Bytea).Value = hash;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        var inbox = await command.ExecuteScalarAsync(token) is long id && id > 0 ? id :
            throw new InvalidDataException("Chest evidence has no command identity.");
        for (var ordinal = 0; ordinal < mutations.Count; ordinal++)
        {
            var mutation = mutations[ordinal];
            await using var ledger = new NpgsqlCommand("""
                INSERT INTO character_inventory_ledger(command_inbox_id,account_id,character_id,inventory_revision,
                    entry_ordinal,item_instance_id,mutation_kind,before_state,after_state,reason_code)
                VALUES(@inbox,@account,@character,@revision,@ordinal,@item,@kind,@before,@after,'wonderland_chest_claim');
                """, connection, transaction);
            AddIdentity(ledger, request);
            ledger.Parameters.AddWithValue("inbox", inbox);
            ledger.Parameters.AddWithValue("revision", revision);
            ledger.Parameters.AddWithValue("ordinal", checked((short)ordinal));
            ledger.Parameters.AddWithValue("item", mutation.Id);
            ledger.Parameters.AddWithValue("kind", mutation.Kind);
            ledger.Parameters.Add("before", NpgsqlDbType.Jsonb).Value = (object?)mutation.Before ?? DBNull.Value;
            ledger.Parameters.Add("after", NpgsqlDbType.Jsonb).Value = mutation.After;
            if (await ledger.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Chest ledger write failed.");
        }
        return inbox;
    }

    private static async Task InsertClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandChestClaimRequest request, long revision, IReadOnlyList<WonderlandChestItemReward> rewards,
        long inbox, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO wonderland_chest_claims(world_instance_id,island_number,character_id,account_id,realm_id,
                party_camp,milestone_hash,reward_policy,rewards,inventory_revision,command_inbox_id)
            VALUES(@instance,@island,@character,@account,@realm,@camp,@hash,@policy,@rewards,@revision,@inbox);
            """, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("camp", checked((short)request.PartyCamp));
        command.Parameters.AddWithValue("policy", WonderlandChestGemRewardPolicy.Revision);
        command.Parameters.Add("rewards", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(rewards);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("inbox", inbox);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Chest claim write failed.");
    }
}
