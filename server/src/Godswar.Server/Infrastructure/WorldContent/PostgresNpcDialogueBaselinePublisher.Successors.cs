using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

// ROUND-NPCSUCCESSOR (issue #27): admission of an operator-published NPC dialogue successor.
// A published revision that is neither a reviewed predecessor nor the reviewed release is accepted, as published and
// never reset to the baseline, only when ALL of these hold; anything else stays a startup failure:
//   1. it is registered in npc_dialogue_successor_releases by the one supported publisher, and its predecessor chain
//      (bounded, acyclic, every link registered) reaches the reviewed release this server ships;
//   2. it targets the currently published NPC spawn revision, which is the one the reviewed release targets;
//   3. its declared and stored counts equal the reviewed release's;
//   4. every route binding, profile and menu entry, and every text key and scene key, equals the reviewed release
//      (only display names and descriptions may differ);
//   5. its revision is the canonical WorldContentRevisionHasher.HashNpcDialogueRelease of its stored texts and the
//      reviewed routes over that spawn revision.
// The world content loader still validates the published rows in full afterwards.
internal static partial class PostgresNpcDialogueBaselinePublisher
{
    public const string SuccessorPublisher = "gwadmin-web-npc-text-v1";
    private const int MaximumSuccessorChain = 64;

    private static async Task VerifyRegisteredSuccessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NpcDialoguePublicationResult current,
        CancellationToken cancellationToken)
    {
        await VerifySuccessorChainAsync(connection, transaction, current.Revision, cancellationToken);

        var spawn = await ReadCurrentSpawnRevisionAsync(connection, transaction, cancellationToken);
        if (!string.Equals(current.SpawnRevision, spawn.Revision, StringComparison.Ordinal) ||
            !string.Equals(spawn.Revision, NpcDialogueBaselineV27.ExpectedSpawnRevision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The NPC dialogue successor does not target the NPC spawn revision of the reviewed release.");
        }

        if (current.TextCount != NpcDialogueBaselineV27.ExpectedTextCount ||
            current.ProfileCount != NpcDialogueBaselineV27.ExpectedProfileCount ||
            current.RouteCount != NpcDialogueBaselineV27.ExpectedRouteCount ||
            current.MenuEntryCount != NpcDialogueBaselineV27.ExpectedMenuEntryCount ||
            !await StoredCountsMatchAsync(connection, transaction, current.Revision, cancellationToken))
        {
            throw new InvalidDataException(
                "The NPC dialogue successor does not carry the reviewed release's complete rows.");
        }

        if (await CountStructuralDifferencesAsync(
                connection, transaction, current.Revision, CurrentReleaseRevision, cancellationToken) != 0)
        {
            throw new InvalidDataException(
                "The NPC dialogue successor changes routes, profiles, menus or text keys of the reviewed release.");
        }

        var texts = await ReadReleaseTextsAsync(connection, transaction, current.Revision, cancellationToken);
        var payload = WorldContentRevisionHasher.HashNpcDialogues(texts, NpcDialogueBaselineV27.CreateRoutes());
        if (!WorldContentRevisionHasher.MatchesNpcDialogueRelease(current.Revision, payload, spawn.Revision))
        {
            throw new InvalidDataException(
                "The NPC dialogue successor's revision is not the canonical hash of its content.");
        }
    }

    private static async Task VerifySuccessorChainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var link = revision;
        for (var depth = 0; depth < MaximumSuccessorChain; depth++)
        {
            if (!seen.Add(link))
            {
                throw new InvalidDataException("The NPC dialogue successor chain loops.");
            }

            await using var command = new NpgsqlCommand(
                """
                SELECT predecessor_revision, publisher
                FROM npc_dialogue_successor_releases
                WHERE revision = @revision;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, link);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The published NPC dialogue revision is neither a reviewed V1-V26 predecessor, the reviewed " +
                    "dependency-bound release, nor a registered successor of it.");
            }

            var predecessor = reader.GetString(0);
            if (!string.Equals(reader.GetString(1), SuccessorPublisher, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The NPC dialogue successor was registered by an unsupported publisher.");
            }

            if (string.Equals(predecessor, CurrentReleaseRevision, StringComparison.Ordinal))
            {
                return;
            }

            link = predecessor;
        }

        throw new InvalidDataException("The NPC dialogue successor chain is too long or never reaches the reviewed release.");
    }

    private static async Task<bool> StoredCountsMatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT COUNT(*)::integer FROM npc_dialogue_texts WHERE revision = @revision),
                   (SELECT COUNT(*)::integer FROM npc_dialogue_profiles WHERE revision = @revision),
                   (SELECT COUNT(*)::integer FROM npc_dialogue_bindings WHERE revision = @revision),
                   (SELECT COUNT(*)::integer FROM npc_dialogue_profile_entries WHERE revision = @revision);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            reader.GetInt32(0) == NpcDialogueBaselineV27.ExpectedTextCount &&
            reader.GetInt32(1) == NpcDialogueBaselineV27.ExpectedProfileCount &&
            reader.GetInt32(2) == NpcDialogueBaselineV27.ExpectedRouteCount &&
            reader.GetInt32(3) == NpcDialogueBaselineV27.ExpectedMenuEntryCount;
    }

    private static async Task<long> CountStructuralDifferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string successor,
        string reviewed,
        CancellationToken cancellationToken)
    {
        // Symmetric differences of every structural column; texts compare keys only.
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT COUNT(*) FROM (
                 (SELECT npc_key, scene_key FROM npc_dialogue_texts WHERE revision = @a
                  EXCEPT SELECT npc_key, scene_key FROM npc_dialogue_texts WHERE revision = @b)
                 UNION ALL
                 (SELECT npc_key, scene_key FROM npc_dialogue_texts WHERE revision = @b
                  EXCEPT SELECT npc_key, scene_key FROM npc_dialogue_texts WHERE revision = @a)) d) +
              (SELECT COUNT(*) FROM (
                 (SELECT profile_key, dialog_index, behavior, initial_request_sub_id FROM npc_dialogue_profiles WHERE revision = @a
                  EXCEPT SELECT profile_key, dialog_index, behavior, initial_request_sub_id FROM npc_dialogue_profiles WHERE revision = @b)
                 UNION ALL
                 (SELECT profile_key, dialog_index, behavior, initial_request_sub_id FROM npc_dialogue_profiles WHERE revision = @b
                  EXCEPT SELECT profile_key, dialog_index, behavior, initial_request_sub_id FROM npc_dialogue_profiles WHERE revision = @a)) d) +
              (SELECT COUNT(*) FROM (
                 (SELECT profile_key, menu_order, sub_id FROM npc_dialogue_profile_entries WHERE revision = @a
                  EXCEPT SELECT profile_key, menu_order, sub_id FROM npc_dialogue_profile_entries WHERE revision = @b)
                 UNION ALL
                 (SELECT profile_key, menu_order, sub_id FROM npc_dialogue_profile_entries WHERE revision = @b
                  EXCEPT SELECT profile_key, menu_order, sub_id FROM npc_dialogue_profile_entries WHERE revision = @a)) d) +
              (SELECT COUNT(*) FROM (
                 (SELECT npc_key, client_script_key, profile_key, route_order FROM npc_dialogue_bindings WHERE revision = @a
                  EXCEPT SELECT npc_key, client_script_key, profile_key, route_order FROM npc_dialogue_bindings WHERE revision = @b)
                 UNION ALL
                 (SELECT npc_key, client_script_key, profile_key, route_order FROM npc_dialogue_bindings WHERE revision = @b
                  EXCEPT SELECT npc_key, client_script_key, profile_key, route_order FROM npc_dialogue_bindings WHERE revision = @a)) d);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("a", NpgsqlDbType.Varchar, successor);
        command.Parameters.AddWithValue("b", NpgsqlDbType.Varchar, reviewed);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<NpcTextDefinition[]> ReadReleaseTextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var texts = new List<NpcTextDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT npc_key, scene_key, display_name, description
            FROM npc_dialogue_texts
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            texts.Add(new NpcTextDefinition(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return texts.OrderBy(static text => text.NpcKey, StringComparer.Ordinal).ToArray();
    }
}
