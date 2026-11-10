using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresLegacyInstanceOpalPaymentIntegrationChecks
{
    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no current database name.");
    }

    private static async Task<OpalPayerFixture> CreatePayerAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        int accountId;
        int characterId;
        await using (var command = dataSource.CreateCommand(
            """
            WITH account AS (
                INSERT INTO public.accounts (username, password)
                VALUES (@username, 'test')
                RETURNING id
            ), character AS (
                INSERT INTO public.character_base (
                    account_id, server_id, name, "Map")
                SELECT id, 1, @characterName, 0
                FROM account
                RETURNING id, account_id
            )
            SELECT id, account_id FROM character;
            """))
        {
            command.Parameters.AddWithValue("username", $"opal_{token}");
            command.Parameters.AddWithValue("characterName", $"OP{token}");
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync(),
                "Opal fixture creates one character");
            characterId = reader.GetInt32(0);
            accountId = reader.GetInt32(1);
        }

        var ownership = await PlayerOwnershipTestFences.InstallAsync(
            dataSource,
            accountId,
            characterId);
        return new(accountId, characterId, ownership);
    }

    private static async Task<long> InsertOpalAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        short slot,
        short stack,
        short quality = 1,
        short grade = 1,
        short bound = 1)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp)
            VALUES (
                @characterId, 1, @slot, 3932,
                @quality, @grade, @bound, @stack, 0)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("stack", stack);
        command.Parameters.AddWithValue("quality", quality);
        command.Parameters.AddWithValue("grade", grade);
        command.Parameters.AddWithValue("bound", bound);
        return await command.ExecuteScalarAsync() is long itemId
            ? itemId
            : throw new InvalidDataException(
                "Opal fixture returned no item identity.");
    }

    private static async Task<int> CountOpalStacksAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*)::integer FROM public.character_items " +
            "WHERE user_id = @characterId AND item_location = 1 " +
            "AND prop_id = 3932;");
        command.Parameters.AddWithValue("characterId", characterId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ReadSemanticOpalQuantityAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        short quality,
        short grade,
        short bound)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(sum(stack), 0)::integer " +
            "FROM public.character_items " +
            "WHERE user_id = @characterId AND item_location = 1 " +
            "AND prop_id = 3932 AND item_quality = @quality " +
            "AND item_grade = @grade AND bound = @bound;");
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("quality", quality);
        command.Parameters.AddWithValue("grade", grade);
        command.Parameters.AddWithValue("bound", bound);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ReadOpalQuantityAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(sum(stack), 0)::integer " +
            "FROM public.character_items " +
            "WHERE user_id = @characterId " +
            "AND item_location = 1 AND prop_id = 3932;");
        command.Parameters.AddWithValue("characterId", characterId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ReadMaximumOpalStackAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(max(stack), 0)::integer " +
            "FROM public.character_items " +
            "WHERE user_id = @characterId " +
            "AND item_location = 1 AND prop_id = 3932;");
        command.Parameters.AddWithValue("characterId", characterId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReadInventoryRevisionAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT inventory_revision FROM public.character_base " +
            "WHERE id = @characterId;");
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountPaymentRowsAsync(
        NpgsqlDataSource dataSource,
        Guid reservationId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*)::integer " +
            "FROM public.legacy_instance_opal_payments " +
            "WHERE reservation_id = @reservationId;");
        command.Parameters.AddWithValue("reservationId", reservationId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyDictionary<int, string>>
        ReadPaymentStatusesAsync(
            NpgsqlDataSource dataSource,
            Guid reservationId)
    {
        var result = new Dictionary<int, string>();
        await using var command = dataSource.CreateCommand(
            "SELECT character_id, payment_status " +
            "FROM public.legacy_instance_opal_payments " +
            "WHERE reservation_id = @reservationId " +
            "ORDER BY character_id;");
        command.Parameters.AddWithValue("reservationId", reservationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt32(0), reader.GetString(1));
        }
        return result;
    }

    private static async Task AssertInventoryEvidenceAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long expectedRevision,
        int expectedEvidenceRows,
        int expectedChargeRows,
        int expectedRefundRows)
    {
        Check.Equal(
            expectedRevision,
            await ReadInventoryRevisionAsync(dataSource, characterId),
            "Opal mutation advances the expected inventory revision");
        var counts = new int[4];
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*)::integer
                 FROM public.command_audit
                 WHERE command_family = 'legacy_instance_opal_payment'
                   AND aggregate_key = @aggregateKey),
                (SELECT count(*)::integer
                 FROM public.command_inbox
                 WHERE command_family = 'legacy_instance_opal_payment'
                   AND aggregate_key = @aggregateKey),
                (SELECT count(*)::integer
                 FROM public.character_inventory_ledger
                 WHERE character_id = @characterId
                   AND reason_code = 'legacy_instance_opal_charge'),
                (SELECT count(*)::integer
                 FROM public.character_inventory_ledger
                 WHERE character_id = @characterId
                   AND reason_code = 'legacy_instance_opal_refund');
            """);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "Opal evidence query returns one row");
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = reader.GetInt32(index);
        }
        Check.True(
            counts[0] == expectedEvidenceRows &&
            counts[1] == expectedEvidenceRows &&
            counts[2] == expectedChargeRows &&
            counts[3] == expectedRefundRows,
            "Opal mutations append matching audit, inbox, and ledger evidence");
    }

    private static async Task<int> CountItemAuditRowsAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        string action)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*)::integer " +
            "FROM public.character_item_audit " +
            "WHERE source = 'legacy-instance-opal' " +
            "AND user_id = @characterId AND action = @action;");
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("action", action);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertDailyClaimAsync(
        NpgsqlDataSource dataSource,
        Guid reservationId,
        RealmId realmId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.legacy_instance_daily_entries (
                realm_id, realm_day, instance_kind, character_id,
                reservation_id, claimed_at)
            VALUES (
                @realmId, DATE '2026-09-01', @instanceKind,
                @characterId, @reservationId, @claimedAt);
            """);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.AddWithValue(
            "instanceKind",
            checked((short)InstanceCallerEntryKind.Atlantis));
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "claimedAt",
            NpgsqlDbType.TimestampTz).Value = Utc(0, 30).UtcDateTime;
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Opal fixture records its daily Atlantis claim");
    }

    private static async Task ClearOwnershipAsync(
        NpgsqlDataSource dataSource,
        params OpalPayerFixture[] payers)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = NULL
            WHERE id = ANY(@characterIds);
            """);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            payers.Select(static payer => payer.CharacterId).ToArray();
        Check.Equal(
            payers.Length,
            await command.ExecuteNonQueryAsync(),
            "Opal recovery fixture releases online ownership");
    }

    private static async Task<bool> DailyClaimExistsAsync(
        NpgsqlDataSource dataSource,
        Guid reservationId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 " +
            "FROM public.legacy_instance_daily_entries " +
            "WHERE reservation_id = @reservationId " +
            "AND character_id = @characterId);");
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.AddWithValue("characterId", characterId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed record OpalPayerFixture(
        int AccountId,
        int CharacterId,
        PlayerOwnershipFence Ownership)
    {
        public LegacyInstanceOpalPayer ToPayer() => new(
            AccountId,
            CharacterId,
            Ownership);
    }
}
