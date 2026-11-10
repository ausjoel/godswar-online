using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandChestClaimStore(NpgsqlDataSource dataSource,
    IWonderlandChestGemRollSource? gemRollSource = null) : IWonderlandChestClaimStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly IWonderlandChestGemRollSource _gemRollSource = gemRollSource ?? new CryptographicWonderlandChestGemRollSource();

    public async Task<WonderlandChestClaimReceipt> ClaimAsync(WonderlandChestClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid) return Failure(WonderlandChestClaimStatus.NotEligible);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await new PostgresPlayerOwnershipGuard(_dataSource).LockCurrentAsync(connection, transaction,
            request.Subject, request.Ownership, cancellationToken);
        if (owner.Status != PlayerOwnershipValidationStatus.Current)
            return Failure(WonderlandChestClaimStatus.OwnershipLost);
        var revision = await ReadEligibilityAsync(connection, transaction, request, cancellationToken);
        if (revision is null) return Failure(WonderlandChestClaimStatus.NotEligible);
        var previous = await ReadPreviousClaimAsync(connection, transaction, request, cancellationToken);
        if (previous is not null) return previous;
        if (revision == long.MaxValue) return Failure(WonderlandChestClaimStatus.Unavailable);
        foreach (var itemId in new[] { WonderlandChestGemRewardPolicy.SapphireIV, WonderlandChestGemRewardPolicy.EmeraldIV })
        {
            var policy = await PostgresItemAcquisitionPolicy.ReadLootItemPolicyAsync(connection, transaction,
                itemId, cancellationToken);
            if (policy is not { StackCap: WonderlandChestRewardPolicy.StackCap })
                return Failure(WonderlandChestClaimStatus.Unavailable);
        }
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(connection, transaction,
                request.Subject.AccountId, request.Subject.CharacterId, 30, cancellationToken))
            return Failure(WonderlandChestClaimStatus.OwnershipLost);
        var bag = await ReadBagAsync(connection, transaction, request.Subject.CharacterId, cancellationToken);
        // Reject before RNG if any possible four-gem mix cannot fit.
        foreach (var possible in WonderlandChestGemRewardPolicy.PossibleRewards())
            if (PlanRewards(bag, possible) is null) return Failure(WonderlandChestClaimStatus.InventoryFull);
        var gemRoll = _gemRollSource.NextRoll();
        var rewards = WonderlandChestGemRewardPolicy.Resolve(gemRoll);
        var plan = PlanRewards(bag, rewards);
        if (plan is null) return Failure(WonderlandChestClaimStatus.InventoryFull);
        var mutations = await ApplyRewardsAsync(connection, transaction, request.Subject.CharacterId,
            bag, plan, cancellationToken);
        var nextRevision = revision.Value + 1;
        await using (var update = new NpgsqlCommand("""
            UPDATE character_base SET inventory_revision=@after WHERE id=@character
              AND account_id=@account AND inventory_revision=@before;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("after", nextRevision);
            update.Parameters.AddWithValue("before", revision.Value);
            AddIdentity(update, request);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("Locked chest inventory revision changed.");
        }
        var inbox = await WriteEvidenceAsync(connection, transaction, request, nextRevision, rewards, gemRoll,
            mutations, cancellationToken);
        await InsertClaimAsync(connection, transaction, request, nextRevision, rewards, inbox, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(WonderlandChestClaimStatus.Claimed, nextRevision, rewards);
    }

    private static async Task<long?> ReadEligibilityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandChestClaimRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.inventory_revision FROM character_base c
            JOIN wonderland_title_members m ON m.character_id=c.id AND m.account_id=c.account_id
            JOIN wonderland_title_runs r ON r.world_instance_id=m.world_instance_id AND r.realm_id=c.server_id
            JOIN wonderland_title_milestones s ON s.world_instance_id=m.world_instance_id AND s.island_number=m.island_number
            WHERE c.id=@character AND c.account_id=@account AND c.server_id=@realm AND c.lifecycle_state='active'
              AND m.world_instance_id=@instance AND m.island_number=@island AND s.request_hash=@hash;
            """, connection, transaction);
        AddIdentity(command, request);
        return await command.ExecuteScalarAsync(token) is long revision && revision >= 0 ? revision : null;
    }

    private static async Task<WonderlandChestClaimReceipt?> ReadPreviousClaimAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandChestClaimRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id,realm_id,party_camp,milestone_hash,inventory_revision,rewards::text
            FROM wonderland_chest_claims WHERE world_instance_id=@instance AND island_number=@island AND character_id=@character;
            """, connection, transaction);
        AddIdentity(command, request);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetInt32(0) != request.Subject.AccountId || reader.GetInt16(1) != request.RealmId.Value ||
            reader.GetInt16(2) != request.PartyCamp || reader.GetString(3) != request.MilestoneHash)
            return Failure(WonderlandChestClaimStatus.NotEligible);
        return new(WonderlandChestClaimStatus.AlreadyClaimed, reader.GetInt64(4),
            JsonSerializer.Deserialize<WonderlandChestItemReward[]>(reader.GetString(5))!);
    }

    private static WonderlandChestClaimReceipt Failure(WonderlandChestClaimStatus status) => new(status, 0, []);

    private static void AddIdentity(NpgsqlCommand command, WonderlandChestClaimRequest request)
    {
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("island", checked((short)request.Island));
        command.Parameters.AddWithValue("account", request.Subject.AccountId);
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue("hash", request.MilestoneHash);
    }
}
