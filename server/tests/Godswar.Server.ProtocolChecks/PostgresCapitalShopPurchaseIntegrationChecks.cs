using System.Globalization;
using System.Text.RegularExpressions;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCapitalShopPurchaseIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL Silver capital-shop purchase persistence";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40}|b12_[a-z0-9_]{1,48})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable B03/B09/B12 " +
                $"database; received '{databaseName}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();

        Check.True(
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.PropsVendor,
                category: 0,
                listingIndex: 0,
                expectedItemId: 3100,
                out var offer) &&
            offer.Currency == CapitalNpcShopCurrency.Silver,
            "Silver persistence check resolves a real Props Merchant offer");

        const int silverBefore = 1_000;
        const int goldBefore = 333;
        const int bindingGoldBefore = 444;
        const int quantity = 2;
        var expectedCost = checked(offer.UnitPrice * quantity);
        var expectedSilverAfter = silverBefore - expectedCost;
        var fixture = await CreateFixtureAsync(
            dataSource,
            silverBefore,
            goldBefore,
            bindingGoldBefore);
        var purchaseId = Guid.NewGuid();

        {
            var result = await store.PurchaseCapitalShopItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                purchaseId,
                offer,
                quantity);

            Check.True(result.Purchased, "Silver shop purchase commits");
            Check.Equal(
                expectedSilverAfter,
                result.CurrencyBalance,
                "Silver shop result returns the debited balance");
            var projection = result.Character ??
                throw new InvalidDataException(
                    "Silver shop purchase returned no character projection.");
            Check.Equal(
                expectedSilverAfter,
                projection.Silver,
                "Silver shop result refreshes Silver immediately");
            Check.Equal(
                goldBefore,
                projection.Gold,
                "Silver shop result preserves Gold");
            Check.Equal(
                bindingGoldBefore,
                projection.BindingGold,
                "Silver shop result preserves Binding Gold");

            var duplicateHandled = false;
            try
            {
                var duplicate = await store.PurchaseCapitalShopItemAsync(
                    fixture.AccountId,
                    fixture.CharacterId,
                    purchaseId,
                    offer,
                    quantity);
                duplicateHandled =
                    duplicate.Purchased &&
                    duplicate.CurrencyBalance == expectedSilverAfter;
            }
            catch (PostgresException error) when (
                error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The current contract fails a duplicate operation closed.
                // The durable-state assertions below prove its tentative
                // wallet and inventory mutations were rolled back.
                duplicateHandled = true;
            }
            Check.True(
                duplicateHandled,
                "duplicate Silver operation replays or fails closed");

            var state = await ReadStateAsync(dataSource, fixture);
            Check.Equal(
                expectedSilverAfter,
                state.Silver,
                "Silver shop purchase persists the Silver debit");
            Check.Equal(
                goldBefore,
                state.Gold,
                "Silver shop persistence preserves Gold");
            Check.Equal(
                bindingGoldBefore,
                state.BindingGold,
                "Silver shop persistence preserves Binding Gold");
            Check.Equal(
                1L,
                state.WalletRevision,
                "Silver shop purchase advances wallet revision once");
            Check.Equal(
                1L,
                state.InventoryRevision,
                "Silver shop purchase advances inventory revision once");
            Check.True(
                string.Equals(
                    "silver",
                    state.CurrencyCode,
                    StringComparison.Ordinal),
                "Silver shop purchase writes the Silver ledger code");
            Check.Equal(
                -(long)expectedCost,
                state.Delta,
                "Silver shop ledger records the exact debit");
            Check.Equal(
                (long)silverBefore,
                state.BalanceBefore,
                "Silver shop ledger records its opening balance");
            Check.Equal(
                (long)expectedSilverAfter,
                state.BalanceAfter,
                "Silver shop ledger records its closing balance");
            Check.Equal(
                quantity,
                state.PurchasedStack,
                "Silver shop inventory evidence covers the purchased stack");
            Check.True(
                state.InventoryLedgerCount > 0,
                "Silver shop purchase writes inventory evidence");

            Check.True(
                string.Equals(
                    nameof(CapitalNpcShopCurrency.Silver),
                    state.RequestCurrency,
                    StringComparison.Ordinal) &&
                string.Equals(
                    nameof(CapitalNpcShopCurrency.Silver),
                    state.ResultCurrency,
                    StringComparison.Ordinal),
                "Silver shop audit and inbox identify the selected currency");
            Check.Equal(
                silverBefore,
                state.EvidenceSilverBefore,
                "Silver shop evidence records opening Silver");
            Check.Equal(
                expectedSilverAfter,
                state.EvidenceSilverAfter,
                "Silver shop evidence records closing Silver");
            Check.Equal(
                goldBefore,
                state.EvidenceGoldAfter,
                "Silver shop evidence proves Gold was preserved");
            Check.Equal(
                bindingGoldBefore,
                state.EvidenceBindingGoldAfter,
                "Silver shop evidence proves Binding Gold was preserved");
        }
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            string.Empty;
    }

    private static async Task<CapitalShopFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource,
        int silver,
        int gold,
        int bindingGold)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"shop_silver_{token}";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Silver shop fixture account has no identity."));
        }

        int realmId;
        await using (var realm = new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            realmId = Convert.ToInt32(
                await realm.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Silver shop fixture has no realm."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone", "BindingGold",
                wallet_revision, inventory_revision)
            VALUES (
                @accountId, @realmId, @name, 1, 0,
                80, @silver, @gold, @bindingGold, 0, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue("name", $"Shop{token}");
            character.Parameters.AddWithValue("silver", silver);
            character.Parameters.AddWithValue("gold", gold);
            character.Parameters.AddWithValue("bindingGold", bindingGold);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Silver shop fixture character has no identity."));
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Silver shop fixture captures an economy baseline");
        await transaction.CommitAsync();
        return new CapitalShopFixture(
            accountId,
            characterId);
    }

    private static async Task<CapitalShopDurableState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        CapitalShopFixture fixture,
        int itemId = 3100)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row."Money",
                character_row."Stone",
                character_row."BindingGold",
                character_row.wallet_revision,
                character_row.inventory_revision,
                currency.currency_code,
                currency.delta,
                currency.balance_before,
                currency.balance_after,
                audit.detail_payload ->> 'currency',
                inbox.result_payload ->> 'currency',
                (inbox.result_payload ->> 'silverBefore')::integer,
                (inbox.result_payload ->> 'silverAfter')::integer,
                (inbox.result_payload ->> 'goldAfter')::integer,
                (inbox.result_payload ->> 'bindingGoldAfter')::integer,
                (SELECT count(*)
                 FROM public.character_inventory_ledger inventory
                 WHERE inventory.command_inbox_id = inbox.id),
                COALESCE((
                    SELECT sum((inventory.after_state ->> 'stack')::integer)
                    FROM public.character_inventory_ledger inventory
                    WHERE inventory.command_inbox_id = inbox.id
                      AND (inventory.after_state ->> 'prop_id')::integer =
                          @itemId
                ), 0)::integer
            FROM public.character_base character_row
            JOIN public.command_inbox inbox
              ON inbox.principal_key = @principalKey
             AND inbox.aggregate_key = @aggregateKey
             AND inbox.command_family = 'capital_shop_purchase'
            JOIN public.command_audit audit ON audit.id = inbox.audit_id
            JOIN public.character_currency_ledger currency
              ON currency.command_inbox_id = inbox.id
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{fixture.CharacterId}");
        command.Parameters.AddWithValue("itemId", itemId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Silver shop durable state was not found.");
        }
        return new CapitalShopDurableState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt64(15),
            reader.GetInt32(16));
    }

    private sealed record CapitalShopFixture(
        int AccountId,
        int CharacterId);

    private sealed record CapitalShopDurableState(
        int Silver,
        int Gold,
        int BindingGold,
        long WalletRevision,
        long InventoryRevision,
        string CurrencyCode,
        long Delta,
        long BalanceBefore,
        long BalanceAfter,
        string RequestCurrency,
        string ResultCurrency,
        int EvidenceSilverBefore,
        int EvidenceSilverAfter,
        int EvidenceGoldAfter,
        int EvidenceBindingGoldAfter,
        long InventoryLedgerCount,
        int PurchasedStack);
}
