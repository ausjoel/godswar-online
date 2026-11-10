using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandBlackmarketStore(NpgsqlDataSource dataSource) : IWonderlandBlackmarketStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    public Task<WonderlandBlackmarketReceipt> ChargeAsync(WonderlandBlackmarketRequest request, CancellationToken token) =>
        ChangeAsync(request, refund: false, token);
    public Task<WonderlandBlackmarketReceipt> RefundAsync(WonderlandBlackmarketRequest request, CancellationToken token) =>
        ChangeAsync(request, refund: true, token);

    private async Task<WonderlandBlackmarketReceipt> ChangeAsync(WonderlandBlackmarketRequest request,
        bool refund, CancellationToken token)
    {
        if (!request.IsValid) return Failure(WonderlandBlackmarketStatus.NotEligible);
        await using var connection = await _dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var owner = await new PostgresPlayerOwnershipGuard(_dataSource).LockCurrentAsync(
            connection, transaction, request.Subject, request.Ownership, token);
        // Compensation is tied to an exact committed debit, and can finish even
        // if the original session lost ownership while relocation was rejected.
        if (!refund && owner.Status != PlayerOwnershipValidationStatus.Current)
            return Failure(WonderlandBlackmarketStatus.OwnershipLost);
        if (!refund && await ReadReceiptAsync(connection, transaction, request, refund: true, token) is not null)
            return Failure(WonderlandBlackmarketStatus.NotEligible);
        var prior = await ReadReceiptAsync(connection, transaction, request, refund, token);
        if (prior is not null) return prior;
        if (refund && await ReadReceiptAsync(connection, transaction, request, refund: false, token) is null)
            return Failure(WonderlandBlackmarketStatus.NotEligible);

        int balance;
        long revision;
        await using (var read = new NpgsqlCommand("""
            SELECT c."Money",c.wallet_revision FROM character_base c
            WHERE c.id=@character AND c.account_id=@account AND c.server_id=@realm
              AND c.lifecycle_state='active' AND (@refund OR
                (@source=0 AND EXISTS (
                  SELECT 1 FROM legacy_instance_daily_entries e
                  WHERE e.character_id=c.id AND e.realm_id=c.server_id AND e.instance_kind=2
                    AND e.reservation_id=@reservation AND e.admitted_at IS NOT NULL)) OR
                (@source>0 AND EXISTS (
                  SELECT 1 FROM wonderland_title_members m
                  JOIN wonderland_title_runs r ON r.world_instance_id=m.world_instance_id
                  WHERE m.character_id=c.id AND m.account_id=c.account_id AND r.realm_id=c.server_id
                    AND m.world_instance_id=@instance AND m.island_number=@source))) FOR UPDATE OF c;
            """, connection, transaction))
        {
            AddIdentity(read, request);
            read.Parameters.AddWithValue("refund", refund);
            read.Parameters.AddWithValue("instance", request.Instance.Value);
            read.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
            read.Parameters.AddWithValue("source", checked((short)(request.TargetIsland - 1)));
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return Failure(WonderlandBlackmarketStatus.NotEligible);
            balance = reader.GetInt32(0);
            revision = reader.GetInt64(1);
        }
        if (!refund && balance < request.Cost) return Failure(WonderlandBlackmarketStatus.InsufficientSilver);
        if (revision == long.MaxValue || refund && balance > int.MaxValue - request.Cost)
            return Failure(WonderlandBlackmarketStatus.Unavailable);
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(connection, transaction,
                request.Subject.AccountId, request.Subject.CharacterId, 30, token))
            return Failure(WonderlandBlackmarketStatus.OwnershipLost);
        var after = checked(balance + (refund ? request.Cost : -request.Cost));
        var receipt = new WonderlandBlackmarketReceipt(WonderlandBlackmarketStatus.Committed, after, revision + 1);
        await using (var update = new NpgsqlCommand("""
            UPDATE character_base SET "Money"=@after,wallet_revision=@revision
            WHERE id=@character AND account_id=@account AND server_id=@realm
              AND "Money"=@before AND wallet_revision=@previous;
            """, connection, transaction))
        {
            AddIdentity(update, request);
            update.Parameters.AddWithValue("before", balance);
            update.Parameters.AddWithValue("after", after);
            update.Parameters.AddWithValue("previous", revision);
            update.Parameters.AddWithValue("revision", receipt.WalletRevision);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Blackmarket wallet changed while locked.");
        }
        await WriteReceiptAsync(connection, transaction, request, refund, balance, receipt, token);
        await transaction.CommitAsync(token);
        return receipt;
    }

    private static WonderlandBlackmarketReceipt Failure(WonderlandBlackmarketStatus status) => new(status, 0, 0);
    private static string Family(bool refund) => refund ? "wonderland_blackmarket_refund" : "wonderland_blackmarket_charge";
    private static byte[] RequestHash(WonderlandBlackmarketRequest request) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
    private static void AddIdentity(NpgsqlCommand command, WonderlandBlackmarketRequest request)
    {
        command.Parameters.AddWithValue("account", request.Subject.AccountId);
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        command.Parameters.AddWithValue("realm", checked((short)request.Realm.Value));
    }
}
