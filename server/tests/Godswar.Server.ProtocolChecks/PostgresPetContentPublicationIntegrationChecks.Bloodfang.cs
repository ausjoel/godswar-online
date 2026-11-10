using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetContentPublicationIntegrationChecks
{
    internal static async Task AssertBloodfangV10UpgradeAsync(NpgsqlDataSource source,
        IItemTemplateCatalog items, string currentRevision)
    {
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var manifest = new NpgsqlCommand(
                """
                INSERT INTO pet_content_revisions (
                    revision, species_count, aptitude_count, native_profile_count,
                    experience_step_count, rebirth_step_count, merge_savvy_step_count,
                    merge_savvy_lookup_count, hatch_rank_step_count, merge_rank_lookup_count,
                    merge_rank_species_factor_count, merge_rank_spirit_step_count, source)
                SELECT @old, 45, aptitude_count, 540, experience_step_count, rebirth_step_count,
                       merge_savvy_step_count, merge_savvy_lookup_count, hatch_rank_step_count,
                       merge_rank_lookup_count, 45, merge_rank_spirit_step_count,
                       'reviewed-pet-baseline-v10'
                FROM pet_content_revisions WHERE revision=@current
                ON CONFLICT (revision) DO NOTHING;
                """, connection, transaction);
            manifest.Parameters.AddWithValue("old", BloodfangPetContentChecks.V10Revision);
            manifest.Parameters.AddWithValue("current", currentRevision);
            if (await manifest.ExecuteNonQueryAsync() == 1)
            {
                foreach (var (table, _) in GuardedPetContentTables)
                {
                    // Only the three species-keyed tables differ in V11.
                    await using var copy = new NpgsqlCommand($"""
                        INSERT INTO public.{table}
                        SELECT (jsonb_populate_record(NULL::public.{table},
                            to_jsonb(row) || jsonb_build_object('revision', @old))).*
                        FROM public.{table} row
                        WHERE row.revision=@current
                          AND COALESCE((to_jsonb(row)->>'species_id')::integer, 0) <= 45;
                        """, connection, transaction);
                    copy.Parameters.AddWithValue("old", BloodfangPetContentChecks.V10Revision);
                    copy.Parameters.AddWithValue("current", currentRevision);
                    await copy.ExecuteNonQueryAsync();
                }
            }
            await using var publish = new NpgsqlCommand(
                "UPDATE pet_content_publication SET revision=@old WHERE family='pets';", connection, transaction);
            publish.Parameters.AddWithValue("old", BloodfangPetContentChecks.V10Revision);
            Check.Equal(1, await publish.ExecuteNonQueryAsync(), "the exact sealed V10 fixture is published");
            await transaction.CommitAsync();
        }
        var historical = await PostgresPetContentReader.LoadAsync(source, items);
        Check.True(historical.Revision.Sha256 == BloodfangPetContentChecks.V10Revision &&
            historical.Species.Count == 45 && historical.NativeProfiles.Count == 540,
            "historical V10 publication remains readable with its unchanged hash");
        var upgraded = await PostgresPetContentBaselinePublisher.EnsurePublishedAsync(source, items);
        Check.True(upgraded.Created && upgraded.Revision == currentRevision,
            "exact V10 publication advances to Bloodfang V11 without editing old rows");
        var repeated = await PostgresPetContentBaselinePublisher.EnsurePublishedAsync(source, items);
        Check.True(!repeated.Created && repeated.Revision == currentRevision,
            "Bloodfang publication is idempotent after the V10 upgrade");
    }
}
