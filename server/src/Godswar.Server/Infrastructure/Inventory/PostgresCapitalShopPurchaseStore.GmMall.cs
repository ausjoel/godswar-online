using static Godswar.Server.Infrastructure.Inventory.PostgresItemAcquisitionPolicy;
using System.Data;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

// GM Mall: the GM-only shop window opened with /mall. Same transaction shape as a
// capital shop purchase (character and bag locked, stacks planned against the
// template stack cap, command audit + inbox, inventory ledger), with three
// differences: the item is always a plain quality 1 / grade 1 / tradable copy, the
// price is 1 silver per unit or nothing (free mode skips the currency ledger row,
// whose CHECK forbids a zero delta), and every created or topped-up stack is also
// written to character_item_audit with source "gm-mall".
internal sealed partial class PostgresCapitalShopPurchaseStore
{
    internal const string GmMallAuditSource = "gm-mall";
    private const string GmMallCommandFamily = "gm_mall_purchase";

    public async Task<CapitalShopPurchaseResult> GrantGmMallItemAsync(
        int accountId,
        int characterId,
        Guid purchaseId,
        uint itemId,
        int unitSilver,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || purchaseId == Guid.Empty ||
            itemId == 0 || unitSilver is < 0 or > 1 ||
            quantity is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var item = PlainGmMallItem(itemId);
        var offer = new CapitalShopOffer(item, unitSilver, CapitalNpcShopCurrency.Silver);
        var totalCost = unitSilver * quantity;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var policy = await ReadLootItemPolicyAsync(connection, transaction, itemId, cancellationToken);
        if (!policy.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CapitalShopPurchaseStatus.UnsupportedItem, Character: null, CurrencyBalance: 0);
        }

        var character = await LockCapitalShopCharacterAsync(
            connection, transaction, accountId, characterId, cancellationToken);
        if (!character.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CapitalShopPurchaseStatus.CharacterNotFound, Character: null, CurrencyBalance: 0);
        }
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection, transaction, accountId, characterId,
                commandTimeoutSeconds: 30, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CapitalShopPurchaseStatus.CharacterNotFound, Character: null, CurrencyBalance: 0);
        }

        var silver = character.Value.Silver;
        if (totalCost > silver)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CapitalShopPurchaseStatus.InsufficientCurrency, Character: null, silver);
        }

        var stackCap = Math.Clamp(policy.Value.StackCap, (short)1, (short)byte.MaxValue);
        var bag = await LockCapitalShopBagAsync(
            connection, transaction, characterId, item, stackCap, cancellationToken);
        var plan = PlanCapitalShopMutation(bag, quantity, stackCap);
        if (plan is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CapitalShopPurchaseStatus.InsufficientCapacity, Character: null, silver);
        }

        var walletRevision = checked(character.Value.WalletRevision + 1);
        var inventoryRevision = checked(character.Value.InventoryRevision + 1);
        var balanceAfter = silver - totalCost;
        var mutations = await ApplyCapitalShopItemsAsync(
            connection, transaction, characterId, item, plan, cancellationToken);
        var inboxId = await InsertCapitalShopEvidenceAsync(
            connection, transaction, accountId, characterId, purchaseId, offer,
            quantity, totalCost, character.Value, balanceAfter, walletRevision,
            inventoryRevision, mutations, cancellationToken, GmMallCommandFamily);
        await UpdateCapitalShopCharacterAsync(
            connection, transaction, accountId, characterId, character.Value,
            "silver", balanceAfter, walletRevision, inventoryRevision, cancellationToken);
        await InsertCapitalShopLedgersAsync(
            connection, transaction, inboxId, accountId, characterId, "silver",
            totalCost, silver, balanceAfter, walletRevision, inventoryRevision,
            mutations, cancellationToken, GmMallCommandFamily);
        await InsertGmMallItemAuditAsync(connection, transaction, characterId, mutations, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var refreshed = await GetCharacterByIdAsync(characterId, cancellationToken)
            ?? throw new InvalidDataException("GM Mall state could not be reloaded.");
        if (refreshed.Silver != balanceAfter)
        {
            throw new InvalidDataException("GM Mall wallet projection is stale.");
        }

        return new(CapitalShopPurchaseStatus.Purchased, refreshed, balanceAfter);
    }

    /// <summary>The copy the GM Mall hands out: quality 1, grade 1, tradable, no attributes or sockets.</summary>
    internal static CompactItemEntry PlainGmMallItem(uint itemId) =>
        new(itemId, null, null, null, null, null, 1, 1, 0, 1, 0, 0,
            null, null, null, null, null, 0,
            null, null, null, null, null, null, null, null, null, null, null, null);

    private static async Task InsertGmMallItemAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        IReadOnlyList<CapitalShopMutation> mutations,
        CancellationToken cancellationToken)
    {
        foreach (var mutation in mutations)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.character_item_audit (
                    source, action, user_id, item_location, slot_index,
                    prop_id, item_quality, item_grade, item_exp, old_item)
                SELECT @source, @action, user_id, item_location, slot_index,
                       prop_id, item_quality, item_grade, item_exp, @oldItem
                FROM public.character_items
                WHERE id = @itemInstanceId AND user_id = @characterId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("source", GmMallAuditSource);
            command.Parameters.AddWithValue("action", mutation.Kind == "add" ? "grant" : "grant-stack");
            command.Parameters.Add("oldItem", NpgsqlDbType.Jsonb).Value =
                mutation.BeforeState is null ? DBNull.Value : mutation.BeforeState;
            command.Parameters.AddWithValue("itemInstanceId", mutation.ItemInstanceId);
            command.Parameters.AddWithValue("characterId", characterId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException("GM Mall item audit was not written once.");
            }
        }
    }
}
