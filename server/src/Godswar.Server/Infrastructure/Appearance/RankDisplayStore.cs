using System.Collections.Concurrent;
using Npgsql;

namespace Godswar.Server.Infrastructure.Appearance;

// RANK-DISPLAY (the admin 2026-11-18): "if equipped armour rank is 9 you can select all the way up to 9, maybe I want
// to set 4". A player picks the highest armour / weapon rank effect (glow, wings) the client may SHOW; the rank
// the gear earns, and every stat that comes from it, are unchanged. Shown rank = min(chosen, earned), so it drops
// by itself when worse gear is equipped. 255 = no cap (the default), 0 = no effect.
// Like character_relations this is idempotent DDL at start-up, not a registered migration: a build without the
// feature still starts against a database this build has used. The table is tiny (one row per character that
// ever used /rank), so it is cached whole; the packet builder reads the cache synchronously.
internal static class RankDisplayStore
{
    public const byte NoCap = 255;

    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.character_rank_display (
            character_id integer PRIMARY KEY REFERENCES public.character_base (id) ON DELETE CASCADE,
            armour_cap smallint NOT NULL DEFAULT 255 CHECK (armour_cap BETWEEN 0 AND 255),
            weapon_cap smallint NOT NULL DEFAULT 255 CHECK (weapon_cap BETWEEN 0 AND 255),
            updated_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    private static readonly ConcurrentDictionary<int, (byte Armour, byte Weapon)> Cache = new();
    private static NpgsqlDataSource? _dataSource;

    public static bool Enabled => _dataSource is not null;

    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("GODSWAR_RANK_DISPLAY")?.Trim(), "0", StringComparison.Ordinal))
        {
            Console.WriteLine("[rank-display] off (GODSWAR_RANK_DISPLAY=0)");
            return;
        }

        try
        {
            var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var ddl = new NpgsqlCommand(SchemaSql, connection))
            {
                await ddl.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var select = new NpgsqlCommand(
                "SELECT character_id, armour_cap, weapon_cap FROM public.character_rank_display", connection))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    Cache[reader.GetInt32(0)] = ((byte)reader.GetInt16(1), (byte)reader.GetInt16(2));
                }
            }

            _dataSource = dataSource;
            Console.WriteLine($"[rank-display] on ({Cache.Count} characters with a chosen rank)");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[rank-display] off (setup failed: {error.Message})");
        }
    }

    public static (byte Armour, byte Weapon) Get(int characterId) =>
        Cache.TryGetValue(characterId, out var caps) ? caps : (NoCap, NoCap);

    public static async Task SetAsync(int characterId, byte armour, byte weapon, CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            throw new InvalidOperationException("The rank display store is off.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_rank_display (character_id, armour_cap, weapon_cap, updated_at)
            VALUES (@id, @armour, @weapon, now())
            ON CONFLICT (character_id) DO UPDATE
                SET armour_cap = excluded.armour_cap, weapon_cap = excluded.weapon_cap, updated_at = now()
            """,
            connection);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("armour", (short)armour);
        command.Parameters.AddWithValue("weapon", (short)weapon);
        await command.ExecuteNonQueryAsync(cancellationToken);
        Cache[characterId] = (armour, weapon);
    }
}

internal enum RankDisplayTarget { Show, Armour, Weapon, Both }

internal static class RankDisplayCommand
{
    // /rank                 -> show earned and shown ranks
    // /rank armour 4        -> show at most armour rank 4 (also: armor, ar, a)
    // /rank weapon 2        -> show at most weapon rank 2 (also: wr, w)
    // /rank armour max      -> no cap again; "off" or 0 = no effect
    // /rank max | /rank reset -> both back to no cap; /rank 4 -> both capped at 4
    public static bool TryParse(string? text, out RankDisplayTarget target, out byte cap, out string? error)
    {
        target = RankDisplayTarget.Show;
        cap = RankDisplayStore.NoCap;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] is not ("/rank" or "/ranks"))
        {
            return false;
        }

        if (words.Length == 1)
        {
            return true;
        }

        string? valueWord;
        if (words.Length == 2)
        {
            target = RankDisplayTarget.Both;
            valueWord = words[1];
        }
        else
        {
            target = words[1] switch
            {
                "armour" or "armor" or "ar" or "a" => RankDisplayTarget.Armour,
                "weapon" or "wr" or "w" => RankDisplayTarget.Weapon,
                "all" or "both" => RankDisplayTarget.Both,
                _ => RankDisplayTarget.Show
            };
            valueWord = words[2];
            if (target == RankDisplayTarget.Show || words.Length > 3)
            {
                error = "Use /rank armour <number>, /rank weapon <number>, or /rank max.";
                return true;
            }
        }

        if (valueWord is "max" or "reset" or "full" or "all")
        {
            cap = RankDisplayStore.NoCap;
            return true;
        }

        if (valueWord is "off" or "none")
        {
            cap = 0;
            return true;
        }

        if (byte.TryParse(valueWord, out var number) && number <= 30)
        {
            cap = number;
            return true;
        }

        error = "Use a rank number (0 = no effect) or max.";
        return true;
    }
}
