using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandBlackmarketStore
{
    private static async Task<WonderlandBlackmarketReceipt?> ReadReceiptAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandBlackmarketRequest request, bool refund, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT request_hash,result_payload::text FROM command_inbox WHERE principal_type='account'
              AND principal_key=@principal AND aggregate_type='character' AND aggregate_key=@aggregate
              AND command_family=@family AND operation_id=@operation;
            """, connection, transaction);
        AddCommandIdentity(command, request, refund);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), RequestHash(request)))
            throw new InvalidDataException("Blackmarket operation identity was reused with different inputs.");
        return JsonSerializer.Deserialize<WonderlandBlackmarketReceipt>(reader.GetString(1))
            ?? throw new InvalidDataException("Blackmarket receipt is invalid.");
    }

    private static async Task WriteReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandBlackmarketRequest request, bool refund, int before, WonderlandBlackmarketReceipt receipt,
        CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(receipt);
        await using var command = new NpgsqlCommand("""
            WITH audit AS (
              INSERT INTO command_audit(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                operation_id,request_hash,outcome_code,detail_payload)
              VALUES('account',@principal,'character',@aggregate,@family,@operation,@requestHash,'committed',@detail)
              RETURNING id), inbox AS (
              INSERT INTO command_inbox(principal_type,principal_key,aggregate_type,aggregate_key,command_family,
                operation_id,request_hash,result_contract_version,result_code,result_payload,result_hash,audit_id)
              SELECT 'account',@principal,'character',@aggregate,@family,@operation,@requestHash,1,'committed',@payload,@resultHash,id
              FROM audit RETURNING id)
            INSERT INTO character_currency_ledger(command_inbox_id,account_id,character_id,wallet_revision,
              currency_code,delta,balance_before,balance_after,reason_code)
            SELECT id,@account,@character,@revision,'silver',@delta,@before,@after,@family FROM inbox;
            """, connection, transaction);
        AddCommandIdentity(command, request, refund);
        AddIdentity(command, request);
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value = RequestHash(request);
        command.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        command.Parameters.Add("detail", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new { request, receipt });
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("revision", receipt.WalletRevision);
        command.Parameters.AddWithValue("delta", receipt.Silver - before);
        command.Parameters.AddWithValue("before", before);
        command.Parameters.AddWithValue("after", receipt.Silver);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Blackmarket ledger was not written exactly once.");
    }

    private static void AddCommandIdentity(NpgsqlCommand command, WonderlandBlackmarketRequest request, bool refund)
    {
        command.Parameters.AddWithValue("principal", request.Subject.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("aggregate", $"character:{request.Subject.CharacterId}");
        command.Parameters.AddWithValue("family", Family(refund));
        command.Parameters.Add("operation", NpgsqlDbType.Bytea).Value = request.OperationId.ToByteArray();
    }
}
