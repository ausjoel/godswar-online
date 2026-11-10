using static Godswar.Server.Infrastructure.Inventory.PostgresItemAcquisitionPolicy;
using Godswar.Server.State;
using System.Data;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresCapitalShopPurchaseStore
{
    public async Task<CapitalShopPurchaseResult>
        PurchaseCapitalShopItemAsync(
        int accountId,
        int characterId,
        Guid purchaseId,
        CapitalShopOffer offer,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 ||
            purchaseId == Guid.Empty || quantity is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }
        if (!offer.IsValid)
        {
            return new(
                CapitalShopPurchaseStatus.UnsupportedItem,
                Character: null,
                CurrencyBalance: 0);
        }

        var totalCost = checked((long)offer.UnitPrice * quantity);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var policy = await ReadLootItemPolicyAsync(
            connection,
            transaction,
            offer.Item.Id,
            cancellationToken);
        if (!policy.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.UnsupportedItem,
                Character: null,
                CurrencyBalance: 0);
        }

        var character = await LockCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        if (!character.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.CharacterNotFound,
                Character: null,
                CurrencyBalance: 0);
        }
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.CharacterNotFound,
                Character: null,
                CurrencyBalance: 0);
        }
        var currencyBalance = character.Value.GetBalance(offer.Currency);
        if (totalCost > currencyBalance)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.InsufficientCurrency,
                Character: null,
                currencyBalance);
        }

        var stackCap = Math.Clamp(
            policy.Value.StackCap,
            (short)1,
            (short)byte.MaxValue);
        var bag = await LockCapitalShopBagAsync(
            connection,
            transaction,
            characterId,
            offer.Item,
            stackCap,
            cancellationToken);
        var inventoryPlan = PlanCapitalShopMutation(
            bag,
            quantity,
            stackCap);
        if (inventoryPlan is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.InsufficientCapacity,
                Character: null,
                currencyBalance);
        }

        var walletRevision = checked(character.Value.WalletRevision + 1);
        var inventoryRevision = checked(
            character.Value.InventoryRevision + 1);
        var balanceAfter = checked(
            currencyBalance - checked((int)totalCost));
        var mutations = await ApplyCapitalShopItemsAsync(
            connection,
            transaction,
            characterId,
            offer.Item,
            inventoryPlan,
            cancellationToken);
        var inboxId = await InsertCapitalShopEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            purchaseId,
            offer,
            quantity,
            checked((int)totalCost),
            character.Value,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            mutations,
            cancellationToken);
        await UpdateCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            character.Value,
            ToCurrencyCode(offer.Currency),
            balanceAfter,
            walletRevision,
            inventoryRevision,
            cancellationToken);
        await InsertCapitalShopLedgersAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            ToCurrencyCode(offer.Currency),
            checked((int)totalCost),
            currencyBalance,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            mutations,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var refreshed = await GetCharacterByIdAsync(
            characterId,
            cancellationToken) ?? throw new InvalidDataException(
                "Purchased shop state could not be reloaded.");
        var refreshedBalance = offer.Currency switch
        {
            CapitalNpcShopCurrency.Silver => refreshed.Silver,
            CapitalNpcShopCurrency.Gold => refreshed.Gold,
            CapitalNpcShopCurrency.BindingGold => refreshed.BindingGold,
            CapitalNpcShopCurrency.Honor => refreshed.MedusaHonorPoints,
            CapitalNpcShopCurrency.Point => refreshed.ExchangePoint,
            CapitalNpcShopCurrency.Medal => refreshed.ExchangeMedal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(offer),
                offer.Currency,
                "Unsupported capital shop currency.")
        };
        if (refreshedBalance != balanceAfter)
        {
            throw new InvalidDataException(
                "Purchased shop wallet projection is stale.");
        }
        return new(
            CapitalShopPurchaseStatus.Purchased,
            refreshed,
            balanceAfter);
    }

    private static async Task<CapitalShopLockedCharacter?>
        LockCapitalShopCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT "Money", "Stone", "BindingGold",
                   COALESCE(medusa_honor_points, 0),
                   COALESCE(exchange_point, 0),
                   COALESCE(exchange_medal, 0),
                   wallet_revision, inventory_revision
            FROM public.character_base
            WHERE id = @characterId AND account_id = @accountId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CapitalShopLockedCharacter(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7))
            : null;
    }

    private readonly record struct CapitalShopLockedCharacter(
        int Silver,
        int Gold,
        int BindingGold,
        int Honor,
        int Point,
        int Medal,
        long WalletRevision,
        long InventoryRevision)
    {
        public int GetBalance(CapitalNpcShopCurrency currency) =>
            currency switch
            {
                CapitalNpcShopCurrency.Silver => Silver,
                CapitalNpcShopCurrency.Gold => Gold,
                CapitalNpcShopCurrency.BindingGold => BindingGold,
                CapitalNpcShopCurrency.Honor => Honor,
                CapitalNpcShopCurrency.Point => Point,
                CapitalNpcShopCurrency.Medal => Medal,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(currency),
                    currency,
                    "Unsupported capital shop currency.")
            };
    }

}
