using System.Text.RegularExpressions;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

// ROUND-NPCSUCCESSOR (issue #27): startup acceptance of a registered operator successor of the reviewed NPC dialogue
// release, and rejection of everything that is not one. Runs only against a disposable copy of a deployed realm
// (database name godswar_b12_npcsucc*) whose dialogue publication is the reviewed release; otherwise it is skipped.
internal static class PostgresNpcDialogueSuccessorIntegrationChecks
{
    public const string CheckName = "PostgreSQL NPC dialogue operator successor admission";
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern =
        new(@"^godswar_b12_npcsucc[a-z0-9_]{0,40}$", RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ScalarAsync<string>(dataSource, "SELECT current_database();");
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable copy named godswar_b12_npcsucc*; received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var reviewed = PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision;
        if (!string.Equals(await PublishedAsync(dataSource), reviewed, StringComparison.Ordinal))
        {
            throw new CheckSkippedException($"{CheckName} requires a copy whose NPC dialogue publication is the reviewed release");
        }

        var spawn = await ScalarAsync<string>(dataSource,
            "SELECT spawn_revision FROM npc_dialogue_revisions WHERE revision = '" + reviewed + "';");

        // 1. A valid successor is accepted as published and never reset to the baseline.
        var edited = await CreateSuccessorAsync(dataSource, reviewed, spawn, " (edited)", register: true);
        await PublishPointerAsync(dataSource, edited);
        var accepted = await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
        Check.True(accepted.Revision == edited && !accepted.Created && await PublishedAsync(dataSource) == edited,
            "a registered successor of the reviewed release stays published (not reset)");
        var world = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
        Check.True(world is not null, "the world content loader validates and loads the successor in full");

        // 2. A successor of the successor is accepted too.
        var second = await CreateSuccessorAsync(dataSource, edited, spawn, " (again)", register: true);
        await PublishPointerAsync(dataSource, second);
        Check.True((await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString)).Revision == second,
            "a registered successor chain reaching the reviewed release is accepted");

        // 3. Unregistered content with a correct hash is rejected.
        var unregistered = await CreateSuccessorAsync(dataSource, reviewed, spawn, " (unregistered)", register: false);
        await ExpectRejectedAsync(dataSource, connectionString, unregistered, "an unregistered revision is rejected");

        // 4. A registered revision whose id is not the hash of its content is rejected.
        var forged = await CreateSuccessorAsync(dataSource, reviewed, spawn, " (forged)", register: true, forgeRevision: true);
        await ExpectRejectedAsync(dataSource, connectionString, forged, "a forged revision hash is rejected");

        // 5. A changed menu entry is rejected.
        var menu = await CreateSuccessorAsync(dataSource, reviewed, spawn, " (menu)", register: true, changeMenu: true);
        await ExpectRejectedAsync(dataSource, connectionString, menu, "a successor that changes a menu entry is rejected");

        // 6. Incomplete rows are rejected.
        var partial = await CreateSuccessorAsync(dataSource, reviewed, spawn, " (partial)", register: true, dropOneText: true);
        await ExpectRejectedAsync(dataSource, connectionString, partial, "a successor with a missing text row is rejected");

        // 7. A successor bound to another NPC spawn revision is rejected.
        var otherSpawn = await ScalarAsync<string?>(dataSource,
            "SELECT revision FROM npc_content_revisions WHERE revision <> '" + spawn + "' ORDER BY revision LIMIT 1;");
        if (otherSpawn is not null)
        {
            var wrongSpawn = await CreateSuccessorAsync(dataSource, reviewed, otherSpawn, " (spawn)", register: true);
            await ExpectRejectedAsync(dataSource, connectionString, wrongSpawn, "a successor bound to another NPC spawn revision is rejected");
        }

        // 8. The registry only accepts the supported publisher and is immutable.
        Check.True(await FailsAsync(dataSource,
                "INSERT INTO npc_dialogue_successor_releases (revision, predecessor_revision, publisher) " +
                "VALUES ('" + unregistered + "', '" + reviewed + "', 'someone-else');"),
            "the registry refuses an unsupported publisher");
        Check.True(await FailsAsync(dataSource,
                "DELETE FROM npc_dialogue_successor_releases WHERE revision = '" + edited + "';"),
            "successor registrations are immutable");

        // Back to the reviewed release: the baseline path is unchanged.
        await PublishPointerAsync(dataSource, reviewed);
        var back = await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
        Check.True(back.Revision == reviewed && !back.Created, "the reviewed release still publishes as before");
    }

    private static async Task<string> CreateSuccessorAsync(
        NpgsqlDataSource dataSource,
        string predecessor,
        string spawn,
        string suffix,
        bool register,
        bool forgeRevision = false,
        bool changeMenu = false,
        bool dropOneText = false)
    {
        var texts = new List<NpcTextDefinition>();
        await using (var command = dataSource.CreateCommand(
            "SELECT npc_key, scene_key, display_name, description FROM npc_dialogue_texts WHERE revision = @r;"))
        {
            command.Parameters.AddWithValue("r", predecessor);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                texts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        texts.Sort(static (a, b) => string.CompareOrdinal(a.NpcKey, b.NpcKey));
        texts[0] = texts[0] with { DisplayName = texts[0].DisplayName + suffix };
        if (dropOneText)
        {
            texts.RemoveAt(texts.Count - 1);
        }

        var revision = WorldContentRevisionHasher.HashNpcDialogueRelease(
            WorldContentRevisionHasher.HashNpcDialogues(texts, NpcDialogueBaselineV27.CreateRoutes()), spawn).Sha256;
        if (forgeRevision)
        {
            revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(revision + suffix)));
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecAsync(connection, transaction,
            "INSERT INTO npc_dialogue_revisions (revision, spawn_revision, text_count, profile_count, route_count, menu_entry_count, source) " +
            "SELECT @rev, @spawn, @texts, profile_count, route_count, menu_entry_count, 'gwadmin-web-npc-text-v1' " +
            "FROM npc_dialogue_revisions WHERE revision = @pre;",
            ("rev", revision), ("spawn", spawn), ("texts", texts.Count), ("pre", predecessor));
        foreach (var text in texts)
        {
            await ExecAsync(connection, transaction,
                "INSERT INTO npc_dialogue_texts (revision, npc_key, scene_key, display_name, description) VALUES (@rev, @n, @s, @d, @x);",
                ("rev", revision), ("n", text.NpcKey), ("s", text.SceneKey), ("d", text.DisplayName), ("x", text.Description));
        }

        await ExecAsync(connection, transaction,
            "INSERT INTO npc_dialogue_profiles (revision, profile_key, dialog_index, behavior, initial_request_sub_id) " +
            "SELECT @rev, profile_key, dialog_index, behavior, initial_request_sub_id FROM npc_dialogue_profiles WHERE revision = @pre;",
            ("rev", revision), ("pre", predecessor));
        await ExecAsync(connection, transaction,
            "INSERT INTO npc_dialogue_profile_entries (revision, profile_key, menu_order, sub_id) " +
            "SELECT @rev, profile_key, menu_order, sub_id + CASE WHEN @change AND menu_order = 0 AND profile_key = " +
            "(SELECT min(profile_key) FROM npc_dialogue_profile_entries WHERE revision = @pre) THEN 1 ELSE 0 END " +
            "FROM npc_dialogue_profile_entries WHERE revision = @pre;",
            ("rev", revision), ("pre", predecessor), ("change", changeMenu));
        await ExecAsync(connection, transaction,
            "INSERT INTO npc_dialogue_bindings (revision, npc_key, client_script_key, profile_key, route_order) " +
            "SELECT @rev, npc_key, client_script_key, profile_key, route_order FROM npc_dialogue_bindings WHERE revision = @pre;",
            ("rev", revision), ("pre", predecessor));
        if (register)
        {
            await ExecAsync(connection, transaction,
                "INSERT INTO npc_dialogue_successor_releases (revision, predecessor_revision, publisher) VALUES (@rev, @pre, @pub);",
                ("rev", revision), ("pre", predecessor), ("pub", PostgresNpcDialogueBaselinePublisher.SuccessorPublisher));
        }

        await transaction.CommitAsync();
        return revision;
    }

    private static async Task ExpectRejectedAsync(NpgsqlDataSource dataSource, string connectionString, string revision, string name)
    {
        var reviewed = PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision;
        var published = await TryPublishPointerAsync(dataSource, revision);
        var rejected = !published;
        if (published)
        {
            try
            {
                await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            rejected = rejected && await PublishedAsync(dataSource) == revision;
        }

        Check.True(rejected, name + " (and the bad publication is left for the operator, not overwritten)");
        await PublishPointerAsync(dataSource, reviewed);
    }

    private static async Task<bool> TryPublishPointerAsync(NpgsqlDataSource dataSource, string revision)
    {
        try
        {
            await PublishPointerAsync(dataSource, revision);
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private static async Task PublishPointerAsync(NpgsqlDataSource dataSource, string revision)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE npc_dialogue_publication SET revision = @r, published_at = now(), publisher = @p WHERE family = 'npc-dialogues';");
        command.Parameters.AddWithValue("r", revision);
        command.Parameters.AddWithValue("p", PostgresNpcDialogueBaselinePublisher.SuccessorPublisher);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<string> PublishedAsync(NpgsqlDataSource dataSource) =>
        ScalarAsync<string>(dataSource, "SELECT revision FROM npc_dialogue_publication WHERE family = 'npc-dialogues';");

    private static async Task<bool> FailsAsync(NpgsqlDataSource dataSource, string sql)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
            return false;
        }
        catch (PostgresException)
        {
            return true;
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
