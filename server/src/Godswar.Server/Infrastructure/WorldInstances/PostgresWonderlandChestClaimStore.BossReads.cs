using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandChestClaimStore
{
    private static async Task<long?> LockBossClaimCharacterAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandBossLootClaimRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT inventory_revision FROM character_base WHERE id=@character AND account_id=@account
                AND server_id=@realm AND lifecycle_state='active' FOR UPDATE;
            """, connection, transaction);
        AddBossIdentity(command, request);
        return await command.ExecuteScalarAsync(token) is long revision && revision >= 0 ? revision : null;
    }

    private static async Task<WonderlandChestClaimReceipt?> ReadBossClaimAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandBossLootClaimRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id,character_id,realm_id,request_hash,inventory_revision,sack_item_id
            FROM wonderland_boss_loot_claims WHERE world_instance_id=@instance AND boss_object_id=@boss
                AND (character_id=@character OR account_id=@account);
            """, connection, transaction);
        AddBossIdentity(command, request);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetInt32(0) != request.Subject.AccountId || reader.GetInt32(1) != request.Subject.CharacterId ||
            reader.GetInt16(2) != request.RealmId.Value || reader.GetString(3) != BossProofHash(request) ||
            reader.GetInt32(5) != request.SackItemId) return Failure(WonderlandChestClaimStatus.NotEligible);
        return new(WonderlandChestClaimStatus.AlreadyClaimed, reader.GetInt64(4), [new(request.SackItemId, 1, 1)]);
    }

    private static string BossProof(WonderlandBossLootClaimRequest request) => JsonSerializer.Serialize(new
    {
        policy = "wonderland-boss-sacks-v1", instance = request.WorldInstanceId.Value, realm = request.RealmId.Value,
        request.ReservationId, request.StartedAt, request.DiedAt, request.BossObjectId, request.SpawnGeneration,
        request.DeathEventId, request.Island, request.PartyCamp, request.SackItemId,
        request.Subject.AccountId, request.Subject.CharacterId
    });

    private static string BossProofHash(WonderlandBossLootClaimRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BossProof(request))));

    private static void AddBossIdentity(NpgsqlCommand command, WonderlandBossLootClaimRequest request)
    {
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("boss", checked((int)request.BossObjectId));
        command.Parameters.AddWithValue("island", checked((short)request.Island));
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        command.Parameters.AddWithValue("account", request.Subject.AccountId);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
    }
}
