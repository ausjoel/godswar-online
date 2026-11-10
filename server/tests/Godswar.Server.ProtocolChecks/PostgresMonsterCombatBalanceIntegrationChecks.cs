using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresMonsterCombatBalanceIntegrationChecks
{
    public const string CheckName = "PostgreSQL mutable dungeon boss critical resistance and startup snapshots";
    private const string ConnectionVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId = "20260916_154_monster_combat_balance";
    private const string EditedTemplate = "B_boss_xerxer_001";
    private static readonly Regex DisposableDatabase = new(
        @"^godswar_(?:b03|b09|b12)_[a-z0-9_]{1,48}$", RegexOptions.CultureInvariant);

    // Independent service roster, not read back from the migration being tested.
    // Four Medusa bosses have separate standard and advanced map identities.
    private static readonly (short Map, string Template)[] Expected =
    [
        (200, "B_bossA_mage_010"), (200, "B_bossA_skeleton_001"),
        (200, "B_bossB_mage_010"), (200, "B_bossA_medusa_008"),
        (204, "B_bossAD_mage_010"), (204, "B_bossAD_skeleton_001"),
        (204, "B_bossBD_mage_010"), (204, "B_bossAD_medusa_008"),
        (205, "B_bossG_octopus_001"), (205, "B_bossGB_hydra_004"),
        (205, "B_bossG_wraith_002"), (205, "B_bossGC_mage_017"),
        (205, "B_bossGD_octopus_001"),
        (207, "B_boss_xerxer_001"), (207, "B_bosse_dryad_001"),
        (207, "B_bosse_gadsguard_007"), (207, "B_bosse_flamingo_001"),
        (207, "B_bosse_male_001"), (207, "B_bosse_greecewarrior_001"),
        (207, "B_bosse_greecewarrior_002"), (207, "B_bosse_dragon_014"),
        (207, "B_bosse_dracoladon_003"), (207, "B_bosse_bull_001"),
        (207, "B_bosse_pan_002"), (207, "B_bossf_dracoladon_003"),
        (207, "B_bosse_kingofscorpion_01")
    ];

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException($"{CheckName} ({ConnectionVariable} is not set)");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var database = dataSource.CreateCommand("SELECT current_database();"))
        {
            var actualDatabase = (string)(await database.ExecuteScalarAsync())!;
            if (!DisposableDatabase.IsMatch(actualDatabase))
                throw new CheckSkippedException($"{CheckName} requires a disposable B03/B09/B12 database");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await AssertSchemaAndSeedsAsync(dataSource);
        await AssertConstraintsAsync(dataSource);
        await PublishWorldContentAsync(connectionString);
        await AssertLoadedSnapshotsAsync(dataSource, connectionString);
    }

    private static async Task AssertSchemaAndSeedsAsync(NpgsqlDataSource dataSource)
    {
        await using (var columns = dataSource.CreateCommand("""
            SELECT count(*)::integer
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'monster_combat_balance'
              AND is_nullable = 'NO'
              AND ((column_name = 'map_id' AND data_type = 'smallint')
                OR (column_name = 'template_key' AND data_type = 'character varying'
                    AND character_maximum_length = 128)
                OR (column_name = 'display_name' AND data_type = 'character varying'
                    AND character_maximum_length = 160)
                OR (column_name = 'critical_resistance' AND data_type = 'integer'));
            """))
            Check.Equal(4, (int)(await columns.ExecuteScalarAsync())!,
                "mutable balance has the bounded named map/template/rating schema");

        var rows = new List<(short Map, string Template, int Resistance)>();
        await using (var query = dataSource.CreateCommand("""
            SELECT map_id, template_key, critical_resistance
            FROM public.monster_combat_balance ORDER BY map_id, template_key;
            """))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add((reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2)));

        Check.Equal(26, rows.Count, "22 dungeon bosses have26 exact map/template entries");
        Check.True(rows.Select(row => (row.Map, row.Template)).SequenceEqual(
                Expected.OrderBy(row => row.Map).ThenBy(row => row.Template, StringComparer.Ordinal)),
            "all Medusa, advanced Medusa, Atlantis and Wonderland seed identities are present with no extras");
        Check.True(rows.All(row => row.Resistance == 100), "each new boss seed has resistance100");
        Check.True(rows.Count(row => row.Map == 200) == 4 && rows.Count(row => row.Map == 204) == 4 &&
            rows.Count(row => row.Map == 205) == 5 && rows.Count(row => row.Map == 207) == 13,
            "standard/advanced maps and the13 Wonderland bosses remain independently addressable");
    }

    private static async Task AssertConstraintsAsync(NpgsqlDataSource dataSource)
    {
        await AssertRejectedAsync(dataSource, """
            UPDATE public.monster_combat_balance SET critical_resistance = -1
            WHERE map_id = 207 AND template_key = 'B_boss_xerxer_001';
            """, PostgresErrorCodes.CheckViolation, "negative resistance cannot enter the authority table");
        await AssertRejectedAsync(dataSource, """
            INSERT INTO public.monster_combat_balance(map_id, template_key, display_name, critical_resistance)
            SELECT map_id, template_key, display_name, 317
            FROM public.monster_combat_balance
            WHERE map_id = 207 AND template_key = 'B_boss_xerxer_001';
            """, PostgresErrorCodes.UniqueViolation, "the same exact map/template key cannot be duplicated");
        await AssertRejectedAsync(dataSource, """
            UPDATE public.monster_combat_balance SET map_id = 256
            WHERE map_id = 207 AND template_key = 'B_boss_xerxer_001';
            """, PostgresErrorCodes.CheckViolation, "unsupported map IDs are rejected");

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var zero = new NpgsqlCommand("""
            UPDATE public.monster_combat_balance SET critical_resistance = 0
            WHERE map_id = 207 AND template_key = 'B_boss_xerxer_001';
            """, connection, transaction);
        Check.Equal(1, await zero.ExecuteNonQueryAsync(), "zero remains a legitimate nonnegative balance");
        await transaction.RollbackAsync();
    }

    private static async Task AssertRejectedAsync(NpgsqlDataSource dataSource, string sql,
        string expectedSqlState, string message)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var rejected = false;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == expectedSqlState)
        {
            rejected = true;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        Check.True(rejected, message);
    }

    private static async Task PublishWorldContentAsync(string connectionString)
    {
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(connectionString);
        await PostgresNpcContentBaselinePublisher.EnsurePublishedAsync(connectionString);
        await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
        await PostgresMonsterContentBaselinePublisher.EnsurePublishedAsync(connectionString);
        await PostgresEnterBootstrapBaselinePublisher.EnsurePublishedAsync(connectionString);
        await PostgresGameplayContentPublisher.EnsurePublishedAsync(connectionString);
    }

    private static async Task AssertLoadedSnapshotsAsync(NpgsqlDataSource dataSource, string connectionString)
    {
        var first = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
        var firstProfiles = MonsterCombatProfileCatalog.Create(first.Gameplay);
        AssertLoadedSeeds(first, firstProfiles);
        var spawn = Spawn(207, EditedTemplate);
        var oldProfile = firstProfiles.Resolve(spawn);
        var updated = false;
        try
        {
            await SetResistanceAsync(dataSource, 100, 317);
            updated = true;
            // Replaying the idempotent DDL/seed fragment must not overwrite an operator edit.
            var migration = PostgresSchemaMigrationCatalog.All.Single(value => value.Id == MigrationId);
            await using (var replay = dataSource.CreateCommand(migration.Sql))
                await replay.ExecuteNonQueryAsync();

            var refreshed = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
            var refreshedProfiles = MonsterCombatProfileCatalog.Create(refreshed.Gameplay);
            Check.Equal(317, ReadEditedResistance(refreshed), "a new startup load reads the arbitrary database value317");
            Check.Equal(100, ReadEditedResistance(first), "the previously loaded world catalog retains resistance100");
            Check.Equal(oldProfile, firstProfiles.Resolve(spawn), "an active profile catalog does not hot-reload mutable rows");
            Check.Equal(oldProfile with { CriticalResistance = 317 }, refreshedProfiles.Resolve(spawn),
                "new runtime profiles apply the database resistance without changing any other combat statistic");
            Check.Equal(first.Manifest.Gameplay, refreshed.Manifest.Gameplay,
                "mutable resistance is separate from the immutable gameplay revision");
            Check.Equal(first.Manifest.Revision, refreshed.Manifest.Revision,
                "operator tuning does not rewrite the sealed world publication identity");
            Check.True(refreshed.Gameplay.MonsterCombatBalances.Where(row =>
                    row.MapId != 207 || row.TemplateKey != EditedTemplate).All(row => row.CriticalResistance == 100),
                "one map/template edit leaves every other boss rating unchanged");
        }
        finally
        {
            if (updated) await SetResistanceAsync(dataSource, 317, 100);
        }

        var restored = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
        Check.Equal(100, ReadEditedResistance(restored), "cleanup restores the seeded value for subsequent checks");
        Check.Equal(100, ReadEditedResistance(first), "the original snapshot remains pinned throughout the round trip");
    }

    private static void AssertLoadedSeeds(IWorldContentReader world, MonsterCombatProfileCatalog profiles)
    {
        Check.Equal(26, world.Gameplay.MonsterCombatBalances.Count, "full startup loader attaches all26 mutable rows");
        foreach (var (map, template) in Expected)
        {
            var row = world.Gameplay.MonsterCombatBalances.Single(value => value.MapId == map && value.TemplateKey == template);
            Check.Equal(100, row.CriticalResistance, "loaded seed retains its exact database rating");
            Check.Equal(100, profiles.Resolve(Spawn(map, template)).CriticalResistance,
                $"runtime boss profile {map}/{template} uses its seeded rating");
        }
    }

    private static int ReadEditedResistance(IWorldContentReader world) =>
        world.Gameplay.MonsterCombatBalances.Single(row => row.MapId == 207 && row.TemplateKey == EditedTemplate)
            .CriticalResistance;

    private static async Task SetResistanceAsync(NpgsqlDataSource dataSource, int expected, int desired)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.monster_combat_balance SET critical_resistance = @desired
            WHERE map_id = 207 AND template_key = @template AND critical_resistance = @expected;
            """);
        command.Parameters.AddWithValue("desired", desired);
        command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("template", EditedTemplate);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "only the expected exact disposable boss row is changed");
    }

    private static CapturedMonsterSpawn Spawn(short map, string template)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x12);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 46100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 8000000);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 8000000);
        Encoding.ASCII.GetBytes(template).CopyTo(packet, 44);
        var result = new CapturedMonsterSpawn(map, $"map-{map}", template, template, 46100, 0, 0, packet);
        result.Validate(map);
        return result;
    }
}
