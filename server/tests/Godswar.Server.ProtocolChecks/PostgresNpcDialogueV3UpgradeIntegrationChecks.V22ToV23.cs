using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    private static async Task CheckSealedV22ReleaseUpgradeAsync(
        NpgsqlDataSource dataSource, string connectionString)
    {
        var payload = new WorldContentFamilyRevision("npc-dialogues",
            NpcDialogueBaselineV22.ExpectedRevision,
            NpcDialogueBaselineV22.ExpectedHashedEntryCount);
        var dependencyBound = WorldContentRevisionHasher.HashNpcDialogueRelease(
            payload, NpcDialogueBaselineV22.ExpectedSpawnRevision).Sha256;
        Check.Equal("8BDD4615AC328B0DE51762EC9AF95AE2220CA5D589EBC6943AA109DAAEC821D2",
            dependencyBound, "upgrade fixture reproduces the sealed live V22 release identity");
        foreach (var previousRevision in new[] { payload.Sha256, dependencyBound })
        {
            await SeedPreviousDialogueReleaseAsync(dataSource, previousRevision,
                NpcDialogueBaselineV22.Source, NpcDialogueBaselineV22.InstanceCallerDescription);
            var legacy = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
            Check.Equal(NpcDialogueBaselineV22.ExpectedRevision,
                legacy.Manifest.NpcDialogues.Sha256,
                "loader verifies the real V22 predecessor payload before promotion");
            foreach (var key in new[] { "Athens_060", "Sparta_060" })
            {
                var dialogue = await legacy.ReadNpcDialogueAsync(key);
                Check.Equal(NpcDialogueBaselineV22.InstanceCallerDescription,
                    dialogue.Text!.Description, "sealed V22 still describes its historical level ceiling");
            }

            var before = await ReadDialogueReleaseSnapshotAsync(dataSource, previousRevision);
            var upgraded = await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
            Check.True(upgraded.Created && upgraded.Revision ==
                PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision,
                "V22 publication promotes to dependency-bound V23");
            Check.Equal(before, await ReadDialogueReleaseSnapshotAsync(dataSource, previousRevision),
                "V23 promotion leaves every immutable V22 row and release header unchanged");
            var current = await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
            Check.Equal(NpcDialogueBaselineV23.ExpectedRevision,
                current.Manifest.NpcDialogues.Sha256, "upgraded loader pins the new V23 payload");
            foreach (var key in new[] { "Athens_060", "Sparta_060" })
            {
                var dialogue = await current.ReadNpcDialogueAsync(key);
                Check.Equal(NpcDialogueBaselineV23.InstanceCallerDescription,
                    dialogue.Text!.Description, "V23 loader advertises Atlantis level 90+ without an upper cap");
            }
            await AssertV22ToV23StoredDeltaAsync(dataSource, previousRevision);
            var repeated = await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(connectionString);
            Check.True(!repeated.Created, "V23 publication is idempotent after either V22 predecessor");
        }
    }

    private static async Task AssertV22ToV23StoredDeltaAsync(
        NpgsqlDataSource dataSource, string previousRevision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*) FILTER (WHERE c->>'description' <> p->>'description'),
                   count(*) FILTER (WHERE c = (p || jsonb_build_object('description',
                       replace(p->>'description', 'Level 90-140', 'Level 90+'))))
            FROM (
                SELECT to_jsonb(t) - 'revision' AS c FROM npc_dialogue_texts t
                WHERE revision = @current
            ) current_rows JOIN (
                SELECT to_jsonb(t) - 'revision' AS p FROM npc_dialogue_texts t
                WHERE revision = @previous
            ) previous_rows ON c->>'npc_key' = p->>'npc_key';
            WITH previous_rows AS (
                SELECT to_jsonb(t) - 'revision' AS value FROM npc_dialogue_profiles t WHERE revision=@previous
                UNION ALL
                SELECT to_jsonb(t) - 'revision' FROM npc_dialogue_profile_entries t WHERE revision=@previous
                UNION ALL
                SELECT to_jsonb(t) - 'revision' FROM npc_dialogue_bindings t WHERE revision=@previous
            ), current_rows AS (
                SELECT to_jsonb(t) - 'revision' AS value FROM npc_dialogue_profiles t WHERE revision=@current
                UNION ALL
                SELECT to_jsonb(t) - 'revision' FROM npc_dialogue_profile_entries t WHERE revision=@current
                UNION ALL
                SELECT to_jsonb(t) - 'revision' FROM npc_dialogue_bindings t WHERE revision=@current
            )
            SELECT count(*) FROM (
                (TABLE previous_rows EXCEPT ALL TABLE current_rows)
                UNION ALL (TABLE current_rows EXCEPT ALL TABLE previous_rows)
            ) delta;
            """);
        command.Parameters.AddWithValue("previous", previousRevision);
        command.Parameters.AddWithValue("current", PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() && reader.GetInt64(0) == 2 &&
            reader.GetInt64(1) == NpcDialogueBaselineV23.ExpectedTextCount,
            "V23 stored texts differ from V22 only by the two Atlantis level phrases");
        Check.True(await reader.NextResultAsync() && await reader.ReadAsync() && reader.GetInt64(0) == 0,
            "V23 stores exactly the same profiles, menus, and bindings as V22");
    }
}
