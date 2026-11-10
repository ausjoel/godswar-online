using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandChestClaimStore
{
    public async Task<WonderlandChestClaimReceipt> ClaimBossAsync(
        WonderlandBossLootClaimRequest first, CancellationToken token = default)
    {
        if (first is null || !first.IsValid) return Failure(WonderlandChestClaimStatus.NotEligible);
        await using var connection = await _dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var owner = await new PostgresPlayerOwnershipGuard(_dataSource).LockCurrentAsync(connection, transaction,
            first.Subject, first.Ownership, token);
        if (owner.Status != PlayerOwnershipValidationStatus.Current) return Failure(WonderlandChestClaimStatus.OwnershipLost);
        var revision = await LockBossClaimCharacterAsync(connection, transaction, first, token);
        if (revision is null)
            return Failure(WonderlandChestClaimStatus.NotEligible);
        var previous = await ReadBossClaimAsync(connection, transaction, first, token);
        if (previous is not null) return previous;
        if (revision == long.MaxValue) return Failure(WonderlandChestClaimStatus.Unavailable);
        WonderlandChestItemReward[] rewards = [new(first.SackItemId, 1, 1)];
        foreach (var reward in rewards)
            if (await PostgresItemAcquisitionPolicy.ReadLootItemPolicyAsync(connection, transaction, reward.ItemId, token)
                is not { StackCap: WonderlandChestRewardPolicy.StackCap, Bound: 1 })
                return Failure(WonderlandChestClaimStatus.Unavailable);
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(connection, transaction,
                first.Subject.AccountId, first.Subject.CharacterId, 30, token))
            return Failure(WonderlandChestClaimStatus.OwnershipLost);
        var bag = await ReadBagAsync(connection, transaction, first.Subject.CharacterId, token);
        var plan = PlanRewards(bag, rewards);
        if (plan is null) return Failure(WonderlandChestClaimStatus.InventoryFull);
        var mutations = await ApplyRewardsAsync(connection, transaction, first.Subject.CharacterId, bag, plan, token);
        var next = revision.Value + 1;
        await using (var update = new NpgsqlCommand("""
            UPDATE character_base SET inventory_revision=@after WHERE id=@character AND account_id=@account
                AND inventory_revision=@before;
            """, connection, transaction))
        {
            AddBossIdentity(update, first);
            update.Parameters.AddWithValue("before", revision.Value);
            update.Parameters.AddWithValue("after", next);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Boss loot lost its locked inventory revision.");
        }
        var inbox = await WriteBossEvidenceAsync(connection, transaction, first, next, rewards, mutations, token);
        await InsertBossClaimAsync(connection, transaction, first, next, inbox, token);
        await transaction.CommitAsync(token);
        return new(WonderlandChestClaimStatus.Claimed, next, rewards);
    }

}
