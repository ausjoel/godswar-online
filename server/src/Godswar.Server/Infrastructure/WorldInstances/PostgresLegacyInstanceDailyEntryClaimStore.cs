using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresLegacyInstanceDailyEntryClaimStore(
    NpgsqlDataSource dataSource) : ILegacyInstanceDailyEntryClaimStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public async Task<LegacyInstanceDailyEntryClaimResult> TryClaimAsync(
        LegacyInstanceDailyEntryClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var characterIds = request.CharacterIds.Order().ToArray();

        await using (var lockCommand = new NpgsqlCommand(
            LockCharactersSql,
            connection,
            transaction))
        {
            lockCommand.Parameters.Add(
                "characterIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                characterIds;
            var lockedCount = 0;
            await using var reader =
                await lockCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lockedCount++;
            }
            if (lockedCount != characterIds.Length)
            {
                throw new InvalidDataException(
                    "A legacy-instance daily-entry character no longer " +
                    "exists.");
            }
        }

        var policy = await ReadDailyEntryPolicyAsync(
            connection,
            transaction,
            request.InstanceKind,
            cancellationToken);
        var usageByCharacter = new Dictionary<int, int>();
        await using (var usageCommand = new NpgsqlCommand(
                         UsageSql,
                         connection,
                         transaction))
        {
            usageCommand.Parameters.AddWithValue(
                "realmId",
                checked((short)request.RealmId.Value));
            usageCommand.Parameters.Add(
                "realmDay",
                NpgsqlDbType.Date).Value = request.RealmDay;
            usageCommand.Parameters.AddWithValue(
                "instanceKind",
                checked((short)request.InstanceKind));
            usageCommand.Parameters.Add(
                "characterIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                characterIds;
            await using var reader =
                await usageCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                usageByCharacter.Add(
                    reader.GetInt32(0),
                    reader.GetInt32(1));
            }
        }

        if (characterIds.Any(characterId =>
                policy.IsLimitReached(
                    usageByCharacter.GetValueOrDefault(characterId))))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                LegacyInstanceDailyEntryClaimStatus.DailyLimitReached,
                policy.DailyEntryLimit,
                policy.FreeEntryLimit,
                new HashSet<int>(),
                policy.PaidRetryLimit);
        }

        IReadOnlySet<int> paymentRequiredCharacterIds =
            policy.PaidRetryLimit is null
                ? new HashSet<int>()
                : characterIds
                    .Where(characterId =>
                        usageByCharacter.GetValueOrDefault(characterId) >=
                        policy.FreeEntryLimit)
                    .ToHashSet();

        await using var command = new NpgsqlCommand(
            ClaimSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.Add(
            "realmDay",
            NpgsqlDbType.Date).Value = request.RealmDay;
        command.Parameters.AddWithValue(
            "instanceKind",
            checked((short)request.InstanceKind));
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            characterIds;
        command.Parameters.AddWithValue(
            "reservationId",
            request.ReservationId);
        command.Parameters.Add(
            "claimedAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ClaimedAtUtc.UtcDateTime;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            LegacyInstanceDailyEntryClaimStatus.Claimed,
            policy.DailyEntryLimit,
            policy.FreeEntryLimit,
            paymentRequiredCharacterIds,
            policy.PaidRetryLimit);
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM legacy_instance_daily_entries " +
            "WHERE reservation_id = @reservationId " +
            "AND admitted_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("reservationId", reservationId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseMembersAsync(
        Guid reservationId,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(characterIds);
        var distinctCharacterIds = characterIds.Distinct().ToArray();
        if (distinctCharacterIds.Length == 0)
        {
            return;
        }
        if (distinctCharacterIds.Length != characterIds.Count ||
            distinctCharacterIds.Any(static characterId => characterId <= 0))
        {
            throw new ArgumentException(
                "A partial legacy-instance release requires unique, " +
                "positive character IDs.",
                nameof(characterIds));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM legacy_instance_daily_entries " +
            "WHERE reservation_id = @reservationId " +
            "AND character_id = ANY(@characterIds) " +
            "AND admitted_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            distinctCharacterIds;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordAdmissionsAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A legacy-instance admission needs a reservation.",
                nameof(reservationId));
        }
        ArgumentNullException.ThrowIfNull(admittedCharacterIds);
        var admitted = admittedCharacterIds.Distinct().ToArray();
        if (admitted.Length == 0)
        {
            return;
        }
        if (admitted.Length != admittedCharacterIds.Count ||
            admitted.Any(static characterId => characterId <= 0))
        {
            throw new ArgumentException(
                "Admitted legacy-instance characters must be unique and " +
                "positive.",
                nameof(admittedCharacterIds));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await using (var reservationLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(" +
            "hashtextextended(@reservationId::text, 3932));",
            connection,
            transaction))
        {
            reservationLock.Parameters.AddWithValue(
                "reservationId",
                reservationId);
            _ = await reservationLock.ExecuteScalarAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            "UPDATE public.legacy_instance_daily_entries " +
            "SET admitted_at = COALESCE(admitted_at, " +
            "GREATEST(claimed_at, clock_timestamp())) " +
            "WHERE reservation_id = @reservationId " +
            "AND character_id = ANY(@characterIds);",
            connection,
            transaction);
        command.Parameters.AddWithValue("reservationId", reservationId);
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = admitted;
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            admitted.Length)
        {
            throw new InvalidDataException(
                "Legacy-instance admissions did not match the reserved " +
                "party members exactly.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<DailyEntryPolicy> ReadDailyEntryPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InstanceCallerEntryKind instanceKind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT free_entry_limit, paid_retry_limit " +
            "FROM legacy_instance_settings " +
            "WHERE instance_kind = @instanceKind FOR SHARE;",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "instanceKind",
            checked((short)instanceKind));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The legacy-instance entry policy is not configured for " +
                $"{instanceKind}.");
        }
        var freeEntryLimit = reader.GetInt16(0);
        short? paidRetryLimit = reader.IsDBNull(1)
            ? null
            : reader.GetInt16(1);
        if (freeEntryLimit < 0 ||
            paidRetryLimit is < 0)
        {
            throw new InvalidDataException(
                "The legacy-instance entry policy is invalid for " +
                $"{instanceKind}.");
        }
        return new(
            checked((ushort)freeEntryLimit),
            paidRetryLimit is null
                ? null
                : checked((ushort)paidRetryLimit.Value));
    }

    private const string LockCharactersSql =
        "SELECT id FROM character_base WHERE id = ANY(@characterIds) " +
        "ORDER BY id FOR UPDATE;";

    private const string UsageSql =
        "SELECT character_id, count(*)::integer " +
        "FROM legacy_instance_daily_entries " +
        "WHERE realm_id = @realmId AND realm_day = @realmDay " +
        "AND instance_kind = @instanceKind " +
        "AND character_id = ANY(@characterIds) " +
        "GROUP BY character_id;";

    private const string ClaimSql =
        """
        INSERT INTO legacy_instance_daily_entries (
            realm_id, realm_day, instance_kind, character_id,
            reservation_id, claimed_at)
        SELECT
            @realmId, @realmDay, @instanceKind, character_id,
            @reservationId, @claimedAt
        FROM unnest(@characterIds) AS character_id;
        """;

    private readonly record struct DailyEntryPolicy(
        ushort FreeEntryLimit,
        ushort? PaidRetryLimit)
    {
        public ushort? DailyEntryLimit => PaidRetryLimit is ushort paid
            ? checked((ushort)(FreeEntryLimit + paid))
            : null;

        public bool IsLimitReached(int usage) =>
            DailyEntryLimit is ushort limit && usage >= limit;
    }
}
