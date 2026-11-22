using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Operations.Health;

// W6A: the only writer of public.session_health (migration 20261113_242_session_health).
//
// Rules this file keeps, because a health system must never be the thing that breaks a login:
//  * Record() never waits and never throws. It drops the row when the queue (1,024 rows) is full.
//  * One background task writes rows in batches. A database error is swallowed, logged at most once a minute,
//    and the batch is dropped.
//  * Off when GODSWAR_SESSION_HEALTH=0, or when the table is missing (an older database): the server starts and
//    plays exactly as before.
//  * Retention: rows older than GODSWAR_SESSION_HEALTH_RETENTION_DAYS (default 30) are deleted hourly, and the
//    table is capped at 200,000 rows so a flood of refused logins cannot grow it without bound.
internal static class SessionHealthRecorder
{
    internal const int QueueCapacity = 1024;
    internal const int BatchSize = 64;
    internal const int DefaultRetentionDays = 30;
    internal const long MaximumRows = 200_000;

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private static Channel<SessionJourney>? _queue;
    private static Action<SessionJourney>? _testSink;
    private static long _dropped;
    private static long _lastErrorLogTicks;

    public static bool Enabled { get; private set; }
    public static int RetentionDays { get; private set; } = DefaultRetentionDays;
    public static long Dropped => Interlocked.Read(ref _dropped);

    public static async Task InitializeAsync(string connectionString)
    {
        try
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("GODSWAR_SESSION_HEALTH")?.Trim(), "0", StringComparison.Ordinal))
            {
                Console.WriteLine("[session-health] off (GODSWAR_SESSION_HEALTH=0)");
                return;
            }
            RetentionDays = ReadRetentionDays(
                Environment.GetEnvironmentVariable("GODSWAR_SESSION_HEALTH_RETENTION_DAYS"));
            var dataSource = NpgsqlDataSource.Create(connectionString);
            await using (var probe = dataSource.CreateCommand("SELECT to_regclass('public.session_health') IS NOT NULL"))
            {
                if (await probe.ExecuteScalarAsync() is not true)
                {
                    Console.WriteLine("[session-health] off (table public.session_health is missing)");
                    await dataSource.DisposeAsync();
                    return;
                }
            }
            var queue = Channel.CreateBounded<SessionJourney>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });
            _queue = queue;
            Enabled = true;
            _ = Task.Run(() => WriteLoopAsync(dataSource, queue.Reader));
            Console.WriteLine($"[session-health] on retention-days={RetentionDays}");
        }
        catch (Exception error)
        {
            Enabled = false;
            _queue = null;
            Console.WriteLine($"[session-health] off ({error.GetType().Name})");
        }
    }

    internal static int ReadRetentionDays(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 365
            ? days
            : DefaultRetentionDays;

    /// <summary>Hands a finished connection to the writer. Never waits, never throws.</summary>
    public static void Record(SessionJourney? journey)
    {
        try
        {
            if (journey is null || !journey.Attempted)
            {
                return;
            }
            journey.Close();
            if (_testSink is { } sink)
            {
                sink(journey);
                return;
            }
            if (_queue is not { } queue || !queue.Writer.TryWrite(journey))
            {
                if (Enabled)
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
        catch
        {
            // Health recording is best effort by design.
        }
    }

    internal static void UseSinkForTests(Action<SessionJourney>? sink) => _testSink = sink;

    private static async Task WriteLoopAsync(NpgsqlDataSource dataSource, ChannelReader<SessionJourney> reader)
    {
        var batch = new List<SessionJourney>(BatchSize);
        var nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                using var wake = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    if (!await reader.WaitToReadAsync(wake.Token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timed out with nothing to write: fall through so the hourly prune still runs.
                }
                batch.Clear();
                while (batch.Count < BatchSize && reader.TryRead(out var journey))
                {
                    batch.Add(journey);
                }
                if (batch.Count > 0)
                {
                    await WriteBatchAsync(dataSource, batch);
                }
                if (DateTimeOffset.UtcNow >= nextPrune)
                {
                    nextPrune = DateTimeOffset.UtcNow + PruneInterval;
                    await PruneAsync(dataSource);
                }
            }
            catch (Exception error)
            {
                LogErrorSometimes(error);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    internal const string InsertSql =
        """
        INSERT INTO public.session_health
            (endpoint, realm_id, started_at, duration_ms, account_id, character_id, client_build, network,
             outcome, reason, error_type, error_text, transport_error, last_opcode, last_send_opcode, stages, quarantined)
        VALUES
            ($1, $2, $3, $4,
             COALESCE($5, (SELECT a.id FROM public.accounts a WHERE $6::text IS NOT NULL AND lower(a.username) = lower($6) LIMIT 1)),
             $7, $8, $9,
             $10,
             CASE WHEN $10 = 'refused' AND $11 = 'bad_credentials' AND $5 IS NULL AND $6::text IS NOT NULL AND NOT EXISTS
                       (SELECT 1 FROM public.accounts a WHERE lower(a.username) = lower($6))
                  THEN 'unknown_account' ELSE $11 END,
             $12, $13, $14, $15, $16, $17::jsonb, $18)
        """;

    private static async Task WriteBatchAsync(NpgsqlDataSource dataSource, List<SessionJourney> rows)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var batch = new NpgsqlBatch(connection);
        foreach (var row in rows)
        {
            var command = new NpgsqlBatchCommand(InsertSql);
            var p = command.Parameters;
            p.Add(new NpgsqlParameter { Value = row.Endpoint, NpgsqlDbType = NpgsqlDbType.Text });
            p.Add(new NpgsqlParameter { Value = row.RealmId, NpgsqlDbType = NpgsqlDbType.Integer });
            p.Add(new NpgsqlParameter { Value = row.StartedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            p.Add(new NpgsqlParameter { Value = row.DurationMilliseconds, NpgsqlDbType = NpgsqlDbType.Bigint });
            p.Add(Nullable(row.AccountId, NpgsqlDbType.Integer));
            p.Add(Nullable(row.AccountId is null ? row.AttemptedName : null, NpgsqlDbType.Text));
            p.Add(Nullable(row.CharacterId, NpgsqlDbType.Integer));
            p.Add(Nullable(row.ClientBuild, NpgsqlDbType.Bigint));
            p.Add(Nullable(row.Network, NpgsqlDbType.Text));
            p.Add(new NpgsqlParameter { Value = Short(row.Outcome, 32) ?? "open", NpgsqlDbType = NpgsqlDbType.Text });
            p.Add(Nullable(Short(row.Reason, 64), NpgsqlDbType.Text));
            p.Add(Nullable(Short(row.ErrorType, 96), NpgsqlDbType.Text));
            p.Add(Nullable(row.ErrorText, NpgsqlDbType.Text));
            p.Add(Nullable(Short(row.TransportError, 96), NpgsqlDbType.Text));
            p.Add(new NpgsqlParameter { Value = row.LastOpcode, NpgsqlDbType = NpgsqlDbType.Integer });
            p.Add(new NpgsqlParameter { Value = row.LastSendOpcode, NpgsqlDbType = NpgsqlDbType.Integer });
            p.Add(new NpgsqlParameter { Value = row.StagesJson(), NpgsqlDbType = NpgsqlDbType.Text });
            p.Add(Nullable(row.Quarantined, NpgsqlDbType.Text));
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync();
    }

    // W14: two separate commands. One command text holding two statements with positional $n parameters is
    // refused by PostgreSQL ("cannot insert multiple commands into a prepared statement", 42601), so before W14
    // the hourly prune failed every time ("[session-health] write skipped: PostgresException") and neither the
    // 30-day retention nor the 200,000-row cap ever ran. Inserts were not affected.
    internal const string PruneByAgeSql =
        "DELETE FROM public.session_health WHERE started_at < now() - make_interval(days => $1)";
    internal const string PruneByCountSql =
        "DELETE FROM public.session_health WHERE id <= (SELECT max(id) FROM public.session_health) - $1";

    private static async Task PruneAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var batch = BuildPruneBatch(connection, RetentionDays, MaximumRows);
        await batch.ExecuteNonQueryAsync();
    }

    internal static NpgsqlBatch BuildPruneBatch(NpgsqlConnection? connection, int retentionDays, long maximumRows)
    {
        var batch = new NpgsqlBatch(connection);
        var byAge = new NpgsqlBatchCommand(PruneByAgeSql);
        byAge.Parameters.Add(new NpgsqlParameter { Value = retentionDays, NpgsqlDbType = NpgsqlDbType.Integer });
        batch.BatchCommands.Add(byAge);
        var byCount = new NpgsqlBatchCommand(PruneByCountSql);
        byCount.Parameters.Add(new NpgsqlParameter { Value = maximumRows, NpgsqlDbType = NpgsqlDbType.Bigint });
        batch.BatchCommands.Add(byCount);
        return batch;
    }

    private static NpgsqlParameter Nullable<T>(T? value, NpgsqlDbType type) where T : struct =>
        new() { Value = value.HasValue ? value.Value : DBNull.Value, NpgsqlDbType = type };

    private static NpgsqlParameter Nullable(string? value, NpgsqlDbType type) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = type };

    private static string? Short(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];

    private static void LogErrorSometimes(Exception error)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastErrorLogTicks);
        if (now - last < 60_000 && last != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _lastErrorLogTicks, now);
        Console.WriteLine($"[session-health] write skipped: {error.GetType().Name}");
    }
}
