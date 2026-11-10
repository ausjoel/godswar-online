using System.Text;
using Godswar.Server.Application.World.Content;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rates;

// EVENT DROPS (q07): polls public.event_drop_windows every 15 s. The game server never creates the table:
// Development/professions/event-drop-windows.sql does, so a database without it (or with no rows) keeps the
// stock drop tables exactly. A failed read keeps the last good snapshot.
internal static class EventDropWindowRuntime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static NpgsqlDataSource? _dataSource;
    private static string _loaded = string.Empty;

    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await ReloadAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[event-drops] start-up load failed; no event windows: {error.GetType().Name}: {error.Message}");
        }
    }

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
                await ReloadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine($"[event-drops] reload failed; keeping the last windows: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private static async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ?? throw new InvalidOperationException("The event drop runtime is not initialised.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var windows = new List<EventDropWindow>();
        var fingerprint = new StringBuilder();
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('public.event_drop_windows') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT name, start_utc, end_utc, enabled, yearly, item_ids, table_key FROM public.event_drop_windows ORDER BY id",
                    connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var items = reader.GetFieldValue<int[]>(5).Where(static id => id > 0).Select(static id => (uint)id).ToHashSet();
                    var window = new EventDropWindow(
                        reader.GetString(0),
                        reader.GetFieldValue<DateTimeOffset>(1),
                        reader.GetFieldValue<DateTimeOffset>(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        items,
                        reader.IsDBNull(6) ? null : reader.GetString(6));
                    windows.Add(window);
                    fingerprint.Append(window.Name).Append('|').Append(window.StartUtc.ToUnixTimeSeconds()).Append('|')
                        .Append(window.EndUtc.ToUnixTimeSeconds()).Append('|').Append(window.Enabled).Append('|')
                        .Append(window.Yearly).Append('|').Append(string.Join(',', items.Order())).Append('|')
                        .Append(window.TableKey).Append('\n');
                }
            }
            else
            {
                fingerprint.Append("no table");
            }
        }

        var loaded = fingerprint.ToString();
        if (loaded == _loaded)
        {
            return;
        }

        _loaded = loaded;
        var snapshot = new EventDropWindowSnapshot(windows);
        EventDropWindowCatalog.Install(snapshot);
        Console.WriteLine(
            $"[event-drops] {windows.Count} windows ({windows.Count(static w => w.Enabled)} enabled), " +
            $"{snapshot.GatedItemCount} items only drop inside them" +
            string.Concat(windows.Select(static w => $"; {w.Name} {(w.Enabled ? "on" : "off")} {w.StartUtc:u}..{w.EndUtc:u}{(w.Yearly ? " yearly" : string.Empty)}")));
    }
}
