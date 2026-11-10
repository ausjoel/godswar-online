using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>One timed item of a character: where it is and when it expires (UTC).</summary>
internal readonly record struct TimedItemSlot(short Location, short Slot, int ItemId, DateTimeOffset ExpiresAtUtc);

// EXPIRY (b03, GodsWar-2.51/Polish patch): real-time item expiry.
// character_items.expires_at is an absolute UTC instant stamped by the
// database (trg_character_items_stamp_expiry, Development/server-next/
// item-expiry-migration.sql), so the clock keeps running while the player is
// offline. This runtime only REMOVES rows that are past it:
//   * at login, every location of every character of the account
//     (before the character snapshot is read), and
//   * every GODSWAR_ITEM_EXPIRY_SECONDS (default 60) for the kit bag of an
//     online character (GameClientHandler.ItemExpiry.cs).
// Every removal writes a character_item_audit row (source 'item-expiry',
// old_item = the full row). A row a sealed pet still points at is never
// removed here. Without the migration the runtime stays disabled and the
// server behaves exactly as before. Failures are logged, never fatal.
internal static class ItemExpiryRuntime
{
    private const string IntervalEnvironmentVariable = "GODSWAR_ITEM_EXPIRY_SECONDS";
    public const string AuditSource = "item-expiry";
    public const short LocationEquipment = 0;
    public const short LocationKitBag = 1;

    private static NpgsqlDataSource? _dataSource;

    public static bool Enabled { get; private set; }

    public static TimeSpan SweepInterval { get; } = ReadInterval();

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await using var command = _dataSource.CreateCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'character_items'
                      AND column_name = 'expires_at')
                """);
            Enabled = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            Console.WriteLine(Enabled
                ? $"[expiry] item expiry on (bag sweep every {SweepInterval.TotalSeconds:0} s)"
                : "[expiry] item expiry off: character_items.expires_at is missing (run item-expiry-migration.sql)");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Enabled = false;
            Console.WriteLine($"[expiry] item expiry off: start-up check failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Login: removes every expired item of every character of the account, in
    /// all locations. Returns the number of removed rows; 0 when disabled or on
    /// error (the login continues; the items are retried at the next login).
    /// </summary>
    public static async Task<int> PurgeAccountAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        if (!Enabled || _dataSource is null)
        {
            return 0;
        }

        try
        {
            await using var command = _dataSource.CreateCommand(PurgeSql(
                "user_id IN (SELECT cb.id FROM character_base cb WHERE cb.account_id = @owner)"));
            command.Parameters.AddWithValue("owner", accountId);
            command.Parameters.AddWithValue("source", AuditSource + ":login");
            var removed = await ReadRemovedAsync(command, cancellationToken);
            if (removed.Count > 0)
            {
                Console.WriteLine(
                    $"[expiry] login account={accountId} removed {removed.Count} expired item(s): " +
                    string.Join(", ", removed.Select(static r => $"char {r.CharacterId} loc {r.Location} slot {r.Slot} item {r.ItemId}")));
            }
            return removed.Count;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[expiry] login purge failed account={accountId}: {error.GetType().Name}: {error.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Online sweep: removes the expired items in the character's kit bag and
    /// returns their bag slots. Other locations wait for the next login.
    /// </summary>
    public static async Task<IReadOnlyList<int>> PurgeKitBagAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        if (!Enabled || _dataSource is null)
        {
            return [];
        }

        await using var command = _dataSource.CreateCommand(PurgeSql(
            "user_id = @owner AND item_location = " + LocationKitBag));
        command.Parameters.AddWithValue("owner", characterId);
        command.Parameters.AddWithValue("source", AuditSource + ":online");
        var removed = await ReadRemovedAsync(command, cancellationToken);
        if (removed.Count > 0)
        {
            Console.WriteLine(
                $"[expiry] online char={characterId} removed {removed.Count} expired bag item(s): " +
                string.Join(", ", removed.Select(static r => $"slot {r.Slot} item {r.ItemId}")));
        }
        return removed.Select(static r => (int)r.Slot).ToArray();
    }

    /// <summary>The timed items worn or carried (equipment and kit bag).</summary>
    public static async Task<IReadOnlyList<TimedItemSlot>> ReadTimedAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        if (!Enabled || _dataSource is null)
        {
            return [];
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT item_location, slot_index, prop_id, expires_at
            FROM character_items
            WHERE user_id = @owner
              AND expires_at IS NOT NULL
              AND item_location IN (0, 1)
            ORDER BY item_location, slot_index
            """);
        command.Parameters.AddWithValue("owner", characterId);
        var rows = new List<TimedItemSlot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TimedItemSlot(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return rows;
    }

    private static string PurgeSql(string ownerFilter) => $"""
        WITH removed AS (
            DELETE FROM character_items ci
            WHERE {ownerFilter}
              AND ci.expires_at IS NOT NULL
              AND ci.expires_at <= now()
              AND NOT EXISTS (
                  SELECT 1 FROM sealed_pet_items sp WHERE sp.item_instance_id = ci.id)
            RETURNING ci.*
        ), audited AS (
            INSERT INTO character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item)
            SELECT @source, 'delete', user_id, item_location, slot_index,
                   prop_id, item_quality, item_grade, item_exp, to_jsonb(removed)
            FROM removed
        )
        SELECT user_id, item_location, slot_index, prop_id FROM removed
        """;

    private static async Task<List<(int CharacterId, short Location, short Slot, int ItemId)>> ReadRemovedAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var removed = new List<(int, short, short, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            removed.Add((reader.GetInt32(0), reader.GetInt16(1), reader.GetInt16(2), reader.GetInt32(3)));
        }
        return removed;
    }

    private static TimeSpan ReadInterval()
    {
        var raw = Environment.GetEnvironmentVariable(IntervalEnvironmentVariable);
        return int.TryParse(raw, out var seconds) && seconds is >= 5 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    }
}
