using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FighterLevelSealDurabilityChecks
{
    private static async Task<SealFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource,
        string scenario,
        int bindingGold,
        int level)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
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
            account.Parameters.AddWithValue(
                "username",
                $"seal_{scenario}_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int realmId;
        await using (var realm = new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            realmId = Convert.ToInt32(await realm.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, fighter_job_exp, "SkillPoint",
                "Money", "Stone", "BindingGold")
            VALUES (
                @accountId, @realmId, @name, 1, 0,
                @level, 0, 0, 0, 0, @bindingGold)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue(
                "name",
                $"Seal{scenario}{token}"[..Math.Min(
                    32,
                    4 + scenario.Length + token.Length)]);
            character.Parameters.AddWithValue("level", level);
            character.Parameters.AddWithValue(
                "bindingGold",
                bindingGold);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Level Sealer fixture captures an economy baseline");
        var ownership = await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new(
            accountId,
            characterId,
            realmId,
            ownership);
    }

    private static async Task RotateOwnershipAsync(
        NpgsqlDataSource dataSource,
        SealFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_generation =
                    checkpoint_owner_generation + 1
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "rotate Level Sealer fixture ownership");
    }

    private static async Task<SealState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        SealFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.fighter_level_sealed,
                character_row."BindingGold",
                character_row.wallet_revision,
                character_row.progression_reward_revision,
                character_row.fighter_level_seal_revision,
                (
                    SELECT count(*)::integer
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = 'account'
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = 'character'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'fighter_level_seal'
                ),
                (
                    SELECT count(*)::integer
                    FROM public.command_audit audit
                    WHERE audit.principal_type = 'account'
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = 'character'
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = 'fighter_level_seal'
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_currency_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = 'fighter_level_unseal'
                ),
                COALESCE((
                    SELECT sum(ledger.delta)::bigint
                    FROM public.character_currency_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = 'fighter_level_unseal'
                ), 0),
                COALESCE((
                    SELECT sum(inbox.duplicate_count)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_key = @principalKey
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'fighter_level_seal'
                ), 0),
                COALESCE((
                    SELECT sum(inbox.request_conflict_count)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_key = @principalKey
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'fighter_level_seal'
                ), 0)
            FROM public.character_base character_row
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{fixture.CharacterId}");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read Level Sealer state");
        return new(
            reader.GetBoolean(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10));
    }

    private sealed record SealFixture(
        int AccountId,
        int CharacterId,
        int RealmId,
        PlayerOwnershipFence Ownership);

    private readonly record struct SealState(
        bool LevelSealed,
        int BindingGold,
        long WalletRevision,
        long ProgressionRevision,
        long SealRevision,
        int InboxCount,
        int AuditCount,
        int CurrencyLedgerCount,
        long CurrencyDelta,
        long DuplicateCount,
        long RequestConflictCount);
}
