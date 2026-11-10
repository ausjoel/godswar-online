using System.Globalization;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Rates;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rates;

// RATES: loads public.server_rates at start-up and hot-reloads it
// (GodsWar-2.51/Polish patch). A failure here never stops the game server:
// it keeps the last good snapshot (compiled defaults = stock behaviour).
internal static class ServerRatesRuntime
{
    private const string PollEnvironmentVariable = "GODSWAR_RATES_POLL_SECONDS";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NpgsqlDataSource? _dataSource;
    private static bool _ensured;
    private static string? _lastError;
    private static DateTimeOffset _lastCheckedUtc = DateTimeOffset.UnixEpoch;
    private static long _reloadCount;

    public static TimeSpan PollInterval { get; } = ReadPollInterval();

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ServerRates.DescribeHook = DescribeJson;
        ServerRates.ReloadHook = ReloadForManagementAsync;
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await ReloadAsync("startup", force: true, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _lastError = $"{error.GetType().Name}: {error.Message}";
            Console.WriteLine(
                "[rates] start-up load failed; using compiled defaults " +
                $"(stock behaviour) until the table can be read: {_lastError}");
        }
    }

    /// <summary>Polls the revision row; never throws except on shutdown.</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
                await ReloadAsync("poll", force: false, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                var message = $"{error.GetType().Name}: {error.Message}";
                if (!string.Equals(message, _lastError, StringComparison.Ordinal))
                {
                    Console.WriteLine(
                        "[rates] reload failed; keeping revision " +
                        $"{ServerRates.Current.Revision}: {message}");
                }
                _lastError = message;
            }
        }
    }

    public static async Task<bool> ReloadAsync(
        string reason,
        bool force,
        CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ??
            throw new InvalidOperationException(
                "The server-rates runtime is not initialised.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!_ensured)
            {
                await PostgresServerRatesStore.EnsureAsync(
                    dataSource,
                    cancellationToken);
                _ensured = true;
                force = true;
            }

            var current = ServerRates.Current;
            if (!force)
            {
                var revision = await PostgresServerRatesStore
                    .ReadRevisionAsync(dataSource, cancellationToken);
                _lastCheckedUtc = DateTimeOffset.UtcNow;
                if (revision == current.Revision &&
                    current.Source == "database")
                {
                    _lastError = null;
                    return false;
                }
            }

            var state = await PostgresServerRatesStore.LoadAsync(
                dataSource,
                cancellationToken);
            _lastCheckedUtc = DateTimeOffset.UtcNow;
            var warnings = new List<string>();
            var snapshot = ServerRatesSnapshot.Create(
                state.Rows.Select(static row =>
                    KeyValuePair.Create(row.Key, row.Value)),
                state.Revision,
                state.UpdatedAtUtc,
                state.UpdatedBy,
                "database",
                warnings);
            ServerRates.Install(snapshot);
            Interlocked.Increment(ref _reloadCount);
            _lastError = null;
            foreach (var warning in warnings)
            {
                Console.WriteLine($"[rates] warning: {warning}");
            }

            var changes = ServerRateCatalog.Definitions
                .Where(definition =>
                    current.Get(definition.Key) != snapshot.Get(definition.Key))
                .Select(definition =>
                    $"{definition.Key} {Format(current.Get(definition.Key))}" +
                    $"->{Format(snapshot.Get(definition.Key))}")
                .ToArray();
            var nonDefault = snapshot.NonDefaultKeys
                .Select(key => $"{key}={Format(snapshot.Get(key))}")
                .ToArray();
            Console.WriteLine(
                $"[rates] loaded revision={snapshot.Revision} reason={reason} " +
                $"by={snapshot.UpdatedBy} " +
                $"changed=[{string.Join(", ", changes)}] " +
                $"non-default=[{string.Join(", ", nonDefault)}]");
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string> ReloadForManagementAsync(
        CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return DescribeJson();
        }

        try
        {
            await ReloadAsync("management", force: true, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _lastError = $"{error.GetType().Name}: {error.Message}";
            Console.WriteLine($"[rates] management reload failed: {_lastError}");
        }

        return DescribeJson();
    }

    public static string DescribeJson()
    {
        var snapshot = ServerRates.Current;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "status",
                _lastError is null && snapshot.Source == "database"
                    ? "ok"
                    : "degraded");
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteString("source", snapshot.Source);
            writer.WriteString("updatedBy", snapshot.UpdatedBy);
            writer.WriteString(
                "updatedAtUtc",
                snapshot.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(
                "loadedAtUtc",
                snapshot.LoadedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(
                "lastCheckedUtc",
                _lastCheckedUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("pollSeconds", (int)PollInterval.TotalSeconds);
            writer.WriteNumber("reloads", Interlocked.Read(ref _reloadCount));
            if (_lastError is null)
            {
                writer.WriteNull("lastError");
            }
            else
            {
                writer.WriteString("lastError", _lastError);
            }

            writer.WriteStartObject("rates");
            foreach (var definition in ServerRateCatalog.Definitions)
            {
                writer.WriteNumber(
                    definition.Key,
                    snapshot.Get(definition.Key) / 1.0000000000000000000000000000m);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("nonDefault");
            foreach (var key in snapshot.NonDefaultKeys)
            {
                writer.WriteStringValue(key);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string Format(decimal value) =>
        (value / 1.0000000000000000000000000000m)
            .ToString(CultureInfo.InvariantCulture);

    private static TimeSpan ReadPollInterval()
    {
        var raw = Environment.GetEnvironmentVariable(PollEnvironmentVariable);
        return int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds) &&
            seconds is >= 1 and <= 3600
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(5);
    }
}
