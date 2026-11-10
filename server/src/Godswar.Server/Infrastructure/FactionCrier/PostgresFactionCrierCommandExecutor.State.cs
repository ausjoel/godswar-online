using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Npgsql;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed partial class PostgresFactionCrierCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        int realmId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                fighter_job_lv,
                fighter_job_exp,
                fighter_level_sealed,
                "SkillPoint",
                "Money",
                "Stone",
                "BindingGold",
                wallet_revision,
                inventory_revision,
                progression_reward_revision,
                faction_crier_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("realmId", realmId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var value = new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10));
        if (value.Level is < 1 or > 200 ||
            value.Experience is < 0 or > uint.MaxValue ||
            value.TalentPoints < 0 ||
            value.Silver < 0 ||
            value.Gold < 0 ||
            value.BindingGold < 0 ||
            value.WalletRevision < 0 ||
            value.InventoryRevision < 0 ||
            value.ProgressionRevision < 0 ||
            value.FactionCrierRevision < 0)
        {
            throw new InvalidDataException(
                "The locked Faction Crier character is invalid.");
        }
        return value;
    }

    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id,
                request_hash,
                result_payload::text,
                result_hash,
                audit_id
            FROM public.command_inbox
            WHERE principal_type = @principalType
              AND principal_key = @principalKey
              AND aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily
              AND operation_id = @operationId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "principalType",
            FactionCrierPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            FactionCrierPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            FactionCrierPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue("operationId", operationId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredInbox(
                reader.GetInt64(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetInt64(4))
            : null;
    }

    private async Task<bool> HasPriorClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int realmId,
        FactionCrierExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        var sql = plan.Operation switch
        {
            FactionCrierOperation.DailyClaim =>
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM public.faction_crier_daily_claims
                    WHERE character_id = @characterId
                      AND realm_id = @realmId
                      AND claim_day = @period
                );
                """,
            FactionCrierOperation.WeeklyReclaim =>
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM public.faction_crier_weekly_reclaims
                    WHERE character_id = @characterId
                      AND realm_id = @realmId
                      AND week_start = @period
                );
                """,
            _ => null
        };
        if (sql is null)
        {
            return false;
        }

        var period = plan.ClaimDay ?? plan.ClaimWeekStart ??
            throw new InvalidDataException(
                "The Faction Crier claim period is missing.");
        await using var command = CreateCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("realmId", realmId);
        command.Parameters.AddWithValue("period", period);
        return await command.ExecuteScalarAsync(cancellationToken)
            is true;
    }

    private static bool TryDebitWallet(
        LockedCharacter character,
        FactionCrierExecutionPlan plan,
        out CharacterWalletSnapshot wallet)
    {
        var silver = character.Silver;
        var gold = character.Gold;
        var bindingGold = character.BindingGold;
        if (plan.CurrencyCost < 0)
        {
            wallet = default!;
            return false;
        }

        switch (plan.Currency)
        {
            case FactionCrierCurrency.None when plan.CurrencyCost == 0:
                break;
            case FactionCrierCurrency.Silver
                when silver >= plan.CurrencyCost:
                silver -= plan.CurrencyCost;
                break;
            case FactionCrierCurrency.Gold
                when gold >= plan.CurrencyCost:
                gold -= plan.CurrencyCost;
                break;
            case FactionCrierCurrency.BoundGold
                when bindingGold >= plan.CurrencyCost:
                bindingGold -= plan.CurrencyCost;
                break;
            default:
                wallet = default!;
                return false;
        }

        wallet = new CharacterWalletSnapshot(silver, gold, bindingGold);
        return true;
    }

    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await UpdateInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            "duplicate_count",
            "last_duplicate_at",
            cancellationToken);

    private async Task RecordRequestConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await UpdateInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            "request_conflict_count",
            "last_request_conflict_at",
            cancellationToken);

    private async Task UpdateInboxCounterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string counterColumn,
        string timestampColumn,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE public.command_inbox
            SET {counterColumn} = {counterColumn} + 1,
                {timestampColumn} = now()
            WHERE id = @inboxId
              AND {counterColumn} < 1000000;
            """;
        await using var command = CreateCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Faction Crier inbox counter did not advance.");
        }
    }
}
