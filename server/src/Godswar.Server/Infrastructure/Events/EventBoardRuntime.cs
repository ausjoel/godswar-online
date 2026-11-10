using Godswar.Server.Application.Events;
using Godswar.Server.Application.Rates;
using Npgsql;

namespace Godswar.Server.Infrastructure.Events;

// EVENTSBOARD: the admin's own rows for the Event window (public.event_board_entries). Like server_rates and
// NpcContentRuntime this is NOT a registered schema migration: idempotent DDL on first use, so a binary
// without the feature still starts against a database this build has used. Nothing touches the database
// while event.board is 0. A failed load keeps the last good rows.
internal static class EventBoardRuntime
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.event_board_entries (
            id serial PRIMARY KEY,
            kind text NOT NULL DEFAULT 'event' CHECK (kind IN ('event', 'notice', 'guide')),
            sort_order integer NOT NULL DEFAULT 0,
            name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 63),
            day_mask smallint NOT NULL DEFAULT 127 CHECK (day_mask BETWEEN 0 AND 127),
            start_minute smallint CHECK (start_minute BETWEEN 0 AND 1439),
            end_minute smallint CHECK (end_minute BETWEEN 0 AND 1439),
            time_text text NOT NULL DEFAULT '' CHECK (char_length(time_text) <= 31),
            content text NOT NULL DEFAULT '' CHECK (char_length(content) <= 1151),
            link text NOT NULL DEFAULT '' CHECK (char_length(link) <= 127),
            auto_name text NOT NULL DEFAULT '' CHECK (char_length(auto_name) <= 63),
            auto_address text NOT NULL DEFAULT '' CHECK (char_length(auto_address) <= 31),
            replaces text,
            enabled boolean NOT NULL DEFAULT true
        );

        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON public.event_board_entries TO gweditor;
                GRANT USAGE, SELECT ON SEQUENCE public.event_board_entries_id_seq TO gweditor;
            END IF;
        EXCEPTION WHEN insufficient_privilege THEN
            RAISE NOTICE 'event_board_entries: not owner, gweditor grant skipped';
        END $$;
        """;

    // PostgreSQL insufficient_privilege: the table exists but this role (godswar) neither owns nor may read it.
    private const string InsufficientPrivilege = "42501";
    private const string UndefinedTable = "42P01";

    private static string? _lastError;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NpgsqlDataSource? _dataSource;
    private static bool _ensured;
    private static IReadOnlyList<EventBoardCustomRow> _rows = [];

    public static bool Enabled => ServerRates.Current.Absolute(ServerRateKeys.EventBoard) == 1;

    public static IReadOnlyList<EventBoardCustomRow> Rows => Volatile.Read(ref _rows);

    /// <summary>Never fails start-up.</summary>
    public static Task InitializeAsync(string connectionString)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[event-board] no database; generated entries only: {error.GetType().Name}");
        }

        return Task.CompletedTask;
    }

    /// <summary>Installs rows directly (checks).</summary>
    internal static void InstallForTesting(IReadOnlyList<EventBoardCustomRow> rows) => Volatile.Write(ref _rows, rows);

    public static async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null || !Enabled)
        {
            return;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            if (!_ensured)
            {
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                _ensured = true;
            }

            var rows = new List<EventBoardCustomRow>();
            await using var command = new NpgsqlCommand(
                "SELECT id, kind, sort_order, name, day_mask, start_minute, end_minute, time_text, content, link, auto_name, auto_address, replaces, enabled FROM public.event_board_entries ORDER BY sort_order, id",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new EventBoardCustomRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt16(4),
                    reader.IsDBNull(5) ? null : reader.GetInt16(5),
                    reader.IsDBNull(6) ? null : reader.GetInt16(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetBoolean(13)));
            }

            Volatile.Write(ref _rows, rows);
            if (_lastError is not null)
            {
                Console.WriteLine($"[event-board] loaded {rows.Count} rows again");
                _lastError = null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Table dropped while running (undo.sql): create it again on the next tick.
            if (error is PostgresException { SqlState: UndefinedTable })
            {
                _ensured = false;
            }

            // Log once per distinct failure, not every 30 s.
            var message = $"{error.GetType().Name}: {error.Message}";
            if (message != _lastError)
            {
                _lastError = message;
                var hint = error is PostgresException { SqlState: InsufficientPrivilege }
                    ? " (fix: as postgres run ALTER TABLE public.event_board_entries OWNER TO godswar;)"
                    : string.Empty;
                Console.WriteLine($"[event-board] load failed; keeping {Rows.Count} rows: {message}{hint}");
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
