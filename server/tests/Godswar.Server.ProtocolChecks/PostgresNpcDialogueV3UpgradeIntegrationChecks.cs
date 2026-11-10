using System.Text.RegularExpressions;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL NPC dialogue V15-to-V21 complete Duel Arena upgrade";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b(?:09|11|12)_[a-z0-9_]{1,48})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable B03/B09/B11/B12 " +
                $"database; received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        _ = await PostgresGameplayContentPublisher.EnsurePublishedAsync(
            connectionString);
        await PostgresNpcContentPublicationIntegrationChecks
            .SeedAndPublishCanonicalV1FixtureAsync(dataSource);
        await SeedAndPublishCanonicalV14FixtureAsync(dataSource);
        await PostgresNpcContentPublicationIntegrationChecks
            .PublishCanonicalV2FixtureAsync(dataSource);
        await SeedAndPublishCanonicalV15FixtureAsync(dataSource);
        Check.True(
            string.Equals(
                NpcContentBaselineV2.ExpectedRevision,
                await ReadPublishedNpcRevisionAsync(dataSource),
                StringComparison.Ordinal),
            "canonical V2 spawn fixture is the published predecessor");
        Check.True(
            string.Equals(
                NpcDialogueBaselineV15.ExpectedRevision,
                await ReadPublishedRevisionAsync(dataSource),
                StringComparison.Ordinal),
            "canonical V15 dialogue fixture is the published predecessor");
        var v14Predecessor = await ReadCanonicalV14SnapshotAsync(dataSource);
        var v15Predecessor = await ReadCanonicalV15SnapshotAsync(dataSource);

        var npcPublication = await PostgresNpcContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            npcPublication.Created &&
            npcPublication.Revision == NpcContentBaselineV7.ExpectedRevision &&
            string.Equals(
                NpcContentBaselineV7.ExpectedRevision,
                await ReadPublishedNpcRevisionAsync(dataSource),
                StringComparison.Ordinal),
            "published live V2 spawn predecessor is promoted to V7");

        var publication = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.Equal(
            PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision,
            publication.Revision,
            "V23 dialogue revision is published");
        Check.True(
            publication.Created,
            "published live V15 predecessor is promoted to V23");
        Check.True(
            string.Equals(
                PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision,
                await ReadPublishedRevisionAsync(dataSource),
                StringComparison.Ordinal),
            "V23 becomes the current dialogue publication");
        Check.Equal(
            v14Predecessor,
            await ReadCanonicalV14SnapshotAsync(dataSource),
            "V23 publication leaves every sealed V14 row unchanged");
        Check.Equal(
            v15Predecessor,
            await ReadCanonicalV15SnapshotAsync(dataSource),
            "V23 publication leaves every sealed V15 row unchanged");
        await AssertV14DeltaAsync(dataSource);
        await AssertV15DeltaAsync(dataSource);
        await AssertV23DeltaAsync(dataSource);
        await AssertV15ToV23DeltaAsync(dataSource);
        await AssertTransporterRoutesAsync(dataSource);
        await AssertV14BattlefieldRoutesAsync(dataSource);
        await AssertCurrentProfileSchemaAsync(dataSource);

        await AssertHolyStoneRoutesAsync(dataSource);
        await AssertPetManagerRoutesAsync(dataSource);
        await AssertFactionCrierRoutesAsync(dataSource);
        _ = await PostgresMonsterContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresEnterBootstrapBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        var pinned = await PostgresWorldContentReaderLoader.LoadAsync(
            connectionString);
        foreach (var npcKey in new[] { "Athens_086", "Sparta_086" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.HolyStone &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    [101, 201, 301, 401, 501, 601, 701, 801]),
                $"{npcKey} loader pins Mount Gear Drilling action 801");
        }
        foreach (var npcKey in new[] { "Athens_088", "Sparta_088" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 2 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.PetManager &&
                dialogue.Routes[0].DialogIndex ==
                    PetManagerProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    PetManagerProtocol.InitialMenuSubIds) &&
                dialogue.Routes[1].RouteOrder == 1 &&
                dialogue.Routes[1].Behavior ==
                    NpcDialogueBehavior.PetPointReset &&
                dialogue.Routes[1].DialogIndex ==
                    PetManagerProtocol.PointResetDialogIndex &&
                dialogue.Routes[1].InitialMenuSubIds.SequenceEqual(
                    PetManagerProtocol.PointResetInitialMenuSubIds),
                $"{npcKey} loader pins both Pet Manager functions");
        }
        foreach (var npcKey in new[] { "Athens_055", "Sparta_055" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.FactionCrier &&
                dialogue.Routes[0].DialogIndex ==
                    FactionCrierProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    FactionCrierProtocol.InitialMenuSubIds),
                $"{npcKey} loader pins Faction Crier dialog 15");
        }
        foreach (var npcKey in new[] { "Athens_060", "Sparta_060" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.InstanceCaller &&
                dialogue.Routes[0].DialogIndex ==
                    InstanceCallerProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    InstanceCallerProtocol.InitialMenuSubIds),
                $"{npcKey} loader pins Instance Caller dialog 9");
        }
        var transporterEndpoints = new[]
        {
            (NpcKey: "Athens_041",
                Menu: TransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Mycenae_All_013",
                Menu: TransporterProtocol.MycenaeInitialMenuSubIds),
            (NpcKey: "Sparta_042",
                Menu: TransporterProtocol.SpartaInitialMenuSubIds)
        };
        foreach (var endpoint in transporterEndpoints)
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(endpoint.NpcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].RouteOrder == 0 &&
                dialogue.Routes[0].ClientScriptKey == endpoint.NpcKey &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.Transporter &&
                dialogue.Routes[0].DialogIndex ==
                    TransporterProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    endpoint.Menu),
                $"{endpoint.NpcKey} loader pins its finite Transporter menu");
        }
        var battlefieldEndpoints = new[]
        {
            (NpcKey: "Athens_056",
                Menu: BattlefieldTransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Sparta_056",
                Menu: BattlefieldTransporterProtocol.SpartaInitialMenuSubIds)
        };
        foreach (var endpoint in battlefieldEndpoints)
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(endpoint.NpcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.BattlefieldTransporter &&
                dialogue.Routes[0].DialogIndex ==
                    BattlefieldTransporterProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    endpoint.Menu),
                $"{endpoint.NpcKey} loader pins its Battlefield menu");
        }
        foreach (var npcKey in new[]
                 {
                     DuelArenaCapturedLayout.GatekeeperNpcKey,
                     DuelArenaCapturedLayout.WardNpcKey
                 })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                DuelArenaCapturedTransportProtocol.IsCapturedRoute(
                    dialogue.Routes[0]),
                $"{npcKey} loader pins its captured Duel Arena transport route");
        }
        await PostgresNpcDialoguePublicationIntegrationChecks
            .AssertDuelArenaServiceRoutesAsync(pinned);
        var repeat = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(!repeat.Created, "V23 repeat publication is a no-op");
        await CheckLegacyV21ReleaseUpgradeAsync(dataSource, connectionString);
        await CheckSealedV22ReleaseUpgradeAsync(dataSource, connectionString);
    }

    private static async Task<string?> ReadPublishedRevisionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT revision
            FROM npc_dialogue_publication
            WHERE family = 'npc-dialogues';
            """);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ReadPublishedNpcRevisionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT revision
            FROM npc_content_publication
            WHERE family = 'npcs';
            """);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return (string?)await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "PostgreSQL returned no current database name.");
    }

    private static async Task AssertHolyStoneRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN ('Athens_086', 'Sparta_086')
            GROUP BY binding.npc_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior
            ORDER BY binding.npc_key, binding.route_order;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            Check.Equal(
                0,
                (int)reader.GetInt16(1),
                "Holy Stone Artisan route order");
            Check.True(
                reader.GetInt32(2) == 30 &&
                reader.GetInt16(3) ==
                    (short)NpcDialogueBehavior.HolyStone &&
                reader.GetFieldValue<int[]>(4).SequenceEqual(
                    [101, 201, 301, 401, 501, 601, 701, 801]),
                "Holy Stone Artisan publishes action 801 on dialog 30");

            rows++;
        }

        Check.Equal(2, rows, "both city Holy Stone Artisans expose action 801");
    }

    private static async Task AssertPetManagerRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN ('Athens_088', 'Sparta_088')
            GROUP BY binding.npc_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior
            ORDER BY binding.npc_key, binding.route_order;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            var routeOrder = (int)reader.GetInt16(1);
            var expectedDialog = routeOrder == 0
                ? PetManagerProtocol.DialogIndex
                : PetManagerProtocol.PointResetDialogIndex;
            var expectedBehavior = routeOrder == 0
                ? NpcDialogueBehavior.PetManager
                : NpcDialogueBehavior.PetPointReset;
            var expectedMenu = routeOrder == 0
                ? PetManagerProtocol.InitialMenuSubIds
                : PetManagerProtocol.PointResetInitialMenuSubIds;
            Check.True(
                routeOrder is 0 or 1 &&
                reader.GetInt32(2) == expectedDialog &&
                reader.GetInt16(3) == (short)expectedBehavior &&
                reader.GetFieldValue<int[]>(4).SequenceEqual(expectedMenu),
                $"Pet Manager publishes ordered route {routeOrder}");
            rows++;
        }

        Check.Equal(4, rows, "both city Pet Managers publish two routes");
    }

    private static async Task AssertFactionCrierRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN ('Athens_055', 'Sparta_055')
            GROUP BY binding.npc_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior
            ORDER BY binding.npc_key;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            Check.True(
                reader.GetInt16(1) == 0 &&
                reader.GetInt32(2) == FactionCrierProtocol.DialogIndex &&
                reader.GetInt16(3) ==
                    (short)NpcDialogueBehavior.FactionCrier &&
                reader.GetFieldValue<int[]>(4).SequenceEqual(
                    FactionCrierProtocol.InitialMenuSubIds),
                "Faction Crier publishes its bounded dialog-15 route");
            rows++;
        }

        Check.Equal(2, rows, "both city Faction Criers publish one route");
    }
}
