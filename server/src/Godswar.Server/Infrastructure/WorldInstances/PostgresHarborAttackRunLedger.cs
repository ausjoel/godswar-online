using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// INSTANCES: public.harbor_attack_runs + public.harbor_attack_run_members.
/// Bay Under Attack is instance kind 3 in legacy_instance_daily_entries.
/// </summary>
/// <remarks>
/// Like server_rates and the economy build's Medal store, these tables are NOT a
/// registered schema migration: they are created with idempotent DDL on first
/// use, so the previous binary still starts against a database this build has
/// used (the migration runner refuses a history it does not know). No foreign
/// keys, so creating them takes no lock on a hot table. The same DDL is in
/// Development/build/instances/migration.sql (pre-release step).
/// </remarks>
internal sealed partial class PostgresHarborAttackRunLedger(NpgsqlDataSource dataSource)
    : IHarborAttackRunLedger
{
    private const short HarborAttackInstanceKind = 3;

    /// <summary>
    /// GENERATED (project choice, not retail): only runs this recent are
    /// refunded; older open rows are just closed as 'expired'.
    /// </summary>
    public static readonly TimeSpan RefundWindow = TimeSpan.FromHours(36);

    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.harbor_attack_runs (
            reservation_id uuid PRIMARY KEY,
            realm_id smallint NOT NULL,
            map_id smallint NOT NULL CHECK (map_id IN (208, 209)),
            started_at timestamptz NOT NULL,
            closed_at timestamptz,
            close_reason text,
            refunded_entries integer NOT NULL DEFAULT 0
                CHECK (refunded_entries >= 0),
            CONSTRAINT ck_harbor_attack_runs_close_reason
                CHECK (close_reason IS NULL OR close_reason IN (
                    'completed', 'terminated', 'deadline', 'abandoned',
                    'refunded', 'expired', 'interrupted')),
            CONSTRAINT ck_harbor_attack_runs_closed
                CHECK ((closed_at IS NULL) = (close_reason IS NULL))
        );
        CREATE INDEX IF NOT EXISTS ix_harbor_attack_runs_open
            ON public.harbor_attack_runs (realm_id, started_at)
            WHERE closed_at IS NULL;
        CREATE TABLE IF NOT EXISTS public.harbor_attack_run_members (
            run_reservation_id uuid NOT NULL,
            member_reservation_id uuid NOT NULL,
            character_id integer NOT NULL,
            PRIMARY KEY (run_reservation_id, member_reservation_id, character_id)
        );
        COMMENT ON TABLE public.harbor_attack_runs IS
            'INSTANCES: one row per Bay Under Attack run; a row still open at start-up was interrupted by a stop.';
        COMMENT ON TABLE public.harbor_attack_run_members IS
            'INSTANCES: every daily-entry reservation admitted to a Bay Under Attack run (leader party and late joiners).';
        """ + CompletionRewardSchemaSql;

    /// <summary>Christina, the end boss (client Monster.ini keys, both level bands).</summary>
    private const string EndBossTemplatesSql = "('B_bossSA_mage_018', 'B_bossSAQ_mage_018')";

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private int _ensured;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _ensured) == 0)
            {
                // One DDL run at a time (concurrent CREATE ... IF NOT EXISTS can
                // deadlock), across processes too.
                await SchemaGate.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref _ensured) == 0)
                    {
                        await using var schema = await connection.BeginTransactionAsync(cancellationToken);
                        await using (var guard = new NpgsqlCommand(
                            "SELECT pg_advisory_xact_lock(@key);", connection, schema))
                        {
                            guard.Parameters.AddWithValue("key", 0x4842524C4447); // "HBRLDG"
                            await guard.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await using (var ddl = new NpgsqlCommand(SchemaSql, connection, schema))
                        {
                            await ddl.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await schema.CommitAsync(cancellationToken);
                        Volatile.Write(ref _ensured, 1);
                    }
                }
                finally
                {
                    SchemaGate.Release();
                }
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task OpenAsync(
        Guid reservationId,
        short realmId,
        byte mapId,
        DateTimeOffset startedAtUtc,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            "INSERT INTO public.harbor_attack_runs " +
            "(reservation_id, realm_id, map_id, started_at) " +
            "VALUES (@reservation, @realm, @map, @started) " +
            "ON CONFLICT (reservation_id) DO NOTHING;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("reservation", reservationId);
            command.Parameters.AddWithValue("realm", realmId);
            command.Parameters.AddWithValue("map", (short)mapId);
            command.Parameters.AddWithValue("started", startedAtUtc.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        // The leader's party entered on the run's own reservation.
        await InsertMembersAsync(connection, transaction, reservationId, reservationId,
            characterIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddMemberAsync(
        Guid runReservationId,
        Guid memberReservationId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await InsertMembersAsync(connection, null, runReservationId, memberReservationId,
            [characterId], cancellationToken);
    }

    private static async Task InsertMembersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid runReservationId,
        Guid memberReservationId,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken)
    {
        if (characterIds.Count == 0)
        {
            return;
        }
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.harbor_attack_run_members " +
            "(run_reservation_id, member_reservation_id, character_id) " +
            "SELECT @run, @member, unnest(@characters) " +
            "ON CONFLICT DO NOTHING;",
            connection,
            transaction);
        command.Parameters.AddWithValue("run", runReservationId);
        command.Parameters.AddWithValue("member", memberReservationId);
        command.Parameters.AddWithValue("characters", characterIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseAsync(
        Guid reservationId,
        string reason,
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE public.harbor_attack_runs " +
            "SET closed_at = @closed, close_reason = @reason " +
            "WHERE reservation_id = @reservation AND closed_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("reservation", reservationId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("closed", closedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HarborAttackRefundResult> RefundInterruptedAsync(
        short realmId,
        DateTimeOffset processStartedAtUtc,
        bool refundEntries,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var windowStart = (processStartedAtUtc - RefundWindow).UtcDateTime;

        // 1. Lock every open run from before this process.
        await using (var lockRows = new NpgsqlCommand(
            "SELECT reservation_id FROM public.harbor_attack_runs " +
            "WHERE realm_id = @realm AND closed_at IS NULL AND started_at < @processStart " +
            "FOR UPDATE;",
            connection,
            transaction))
        {
            lockRows.Parameters.AddWithValue("realm", realmId);
            lockRows.Parameters.AddWithValue("processStart", processStartedAtUtc.UtcDateTime);
            await lockRows.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. A run whose Christina death reward committed (killed by one of its
        //    own members, on its map, during its 30 minutes) was completed, even if
        //    the in-memory close never reached the database: it is closed
        //    'completed' and never refunded, so her loot cannot be farmed twice.
        int completed;
        await using (var finish = new NpgsqlCommand(
            $"""
            UPDATE public.harbor_attack_runs r
            SET closed_at = clock_timestamp(), close_reason = 'completed'
            WHERE r.realm_id = @realm
              AND r.closed_at IS NULL
              AND r.started_at < @processStart
              AND EXISTS (
                  SELECT 1
                  FROM public.monster_death_reward_settlements s
                  JOIN public.harbor_attack_run_members m
                    ON m.run_reservation_id = r.reservation_id
                   AND m.character_id = s.character_id
                  JOIN public.gm_monster_spawns g
                    ON g.map_id = s.map_id
                   AND g.object_id = s.monster_object_id
                  WHERE s.map_id = r.map_id
                    AND s.committed_at >= r.started_at
                    -- a run lasts 30 minutes (HarborAttackPolicy) plus the end window
                    AND s.committed_at < r.started_at + interval '35 minutes'
                    AND g.template_key IN {EndBossTemplatesSql});
            """,
            connection,
            transaction))
        {
            finish.Parameters.AddWithValue("realm", realmId);
            finish.Parameters.AddWithValue("processStart", processStartedAtUtc.UtcDateTime);
            completed = await finish.ExecuteNonQueryAsync(cancellationToken);
        }

        // BAY2: a run that paid its completion EXP is closed with its own end
        // reason and never refunded (the payout normally closes the row in its
        // own transaction; this covers an open row written after it).
        completed += await ClosePaidRunsAsync(connection, transaction, realmId,
            processStartedAtUtc, cancellationToken);

        // 3. Every other open run in the window gives back the daily entry of
        //    every reservation admitted to it (the leader's party and each late
        //    joiner's own reservation).
        int refunded = 0;
        int runs = 0;
        if (!refundEntries)
        {
            // Switch off: the interrupted runs are closed without a refund, so a
            // later start-up with the switch on cannot refund them either.
            await using var interrupted = new NpgsqlCommand(
                "UPDATE public.harbor_attack_runs " +
                "SET closed_at = clock_timestamp(), close_reason = 'interrupted' " +
                "WHERE realm_id = @realm AND closed_at IS NULL " +
                "AND started_at < @processStart AND started_at >= @windowStart;",
                connection,
                transaction);
            interrupted.Parameters.AddWithValue("realm", realmId);
            interrupted.Parameters.AddWithValue("processStart", processStartedAtUtc.UtcDateTime);
            interrupted.Parameters.AddWithValue("windowStart", windowStart);
            runs = await interrupted.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        await using (var refund = new NpgsqlCommand(
            """
            WITH interrupted AS (
                SELECT reservation_id
                FROM public.harbor_attack_runs
                WHERE realm_id = @realm
                  AND closed_at IS NULL
                  AND started_at < @processStart
                  AND started_at >= @windowStart
            ), reservations AS (
                SELECT i.reservation_id AS run_id, i.reservation_id AS entry_id
                FROM interrupted i
                UNION
                SELECT m.run_reservation_id, m.member_reservation_id
                FROM public.harbor_attack_run_members m
                JOIN interrupted i ON i.reservation_id = m.run_reservation_id
            ), released AS (
                DELETE FROM public.legacy_instance_daily_entries d
                USING reservations x
                WHERE d.reservation_id = x.entry_id
                  AND d.realm_id = @realm
                  AND d.instance_kind = @kind
                RETURNING x.run_id
            ), counted AS (
                SELECT i.reservation_id, count(r.run_id)::integer AS entries
                FROM interrupted i
                LEFT JOIN released r ON r.run_id = i.reservation_id
                GROUP BY i.reservation_id
            ), closed AS (
                UPDATE public.harbor_attack_runs h
                SET closed_at = clock_timestamp(),
                    close_reason = 'refunded',
                    refunded_entries = c.entries
                FROM counted c
                WHERE h.reservation_id = c.reservation_id
                RETURNING c.entries
            )
            SELECT COALESCE(sum(entries), 0)::integer, count(*)::integer FROM closed;
            """,
            connection,
            transaction))
        {
            refund.Parameters.AddWithValue("realm", realmId);
            refund.Parameters.AddWithValue("kind", HarborAttackInstanceKind);
            refund.Parameters.AddWithValue("processStart", processStartedAtUtc.UtcDateTime);
            refund.Parameters.AddWithValue("windowStart", windowStart);
            await using var reader = await refund.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            refunded = reader.GetInt32(0);
            runs = reader.GetInt32(1);
        }

        int expired;
        await using (var expire = new NpgsqlCommand(
            "UPDATE public.harbor_attack_runs " +
            "SET closed_at = clock_timestamp(), close_reason = 'expired' " +
            "WHERE realm_id = @realm AND closed_at IS NULL AND started_at < @windowStart;",
            connection,
            transaction))
        {
            expire.Parameters.AddWithValue("realm", realmId);
            expire.Parameters.AddWithValue("windowStart", windowStart);
            expired = await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(refunded, runs, completed, expired);
    }
}
