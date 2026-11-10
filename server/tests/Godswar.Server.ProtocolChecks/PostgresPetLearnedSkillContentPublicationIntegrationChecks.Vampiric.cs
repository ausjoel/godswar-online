using System.Text.Json;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLearnedSkillContentPublicationIntegrationChecks
{
    public const string VampiricUpgradeCheckName =
        "PostgreSQL Vampiric learned-content upgrade preserves installed publication";

    public static async Task RunVampiricUpgradeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new CheckSkippedException($"{VampiricUpgradeCheckName} ({ConnectionStringVariable} is not set)");
        await using var source = NpgsqlDataSource.Create(connectionString);
        if (!await IsDisposableDatabaseAsync(source))
            throw new CheckSkippedException("Vampiric upgrade requires a disposable PostgreSQL database.");
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await SeedInstalledPublicationIfEmptyAsync(source);
        string beforeRevision;
        await using (var pointer = source.CreateCommand(
            "SELECT revision FROM public.pet_skill_content_publication WHERE singleton;"))
            beforeRevision = (string)(await pointer.ExecuteScalarAsync())!;
        var before = await ReadInstalledContentSnapshotAsync(source);
        if (before is null)
            throw new CheckSkippedException("Upgrade rehearsal requires the installed predecessor or an empty disposable database.");
        Check.True(beforeRevision == PetLearnedSkillContentBaseline.InstalledRevision ||
            beforeRevision == PetLearnedSkillContentBaseline.ExpectedRevision,
            "upgrade rehearsal starts from a reviewed installed or already-upgraded publication");
        var pinnedInstalled = PetLearnedSkillContentBaseline.CreateInstalled();
        var catalogs = await PublishConcurrentlyAsync(source, connectionString);
        Check.True(catalogs.All(static catalog =>
                catalog.Revision.Sha256 == PetLearnedSkillContentBaseline.ExpectedRevision),
            "concurrent old-publication upgrades and repeated loads converge on Vampiric content");
        var after = await ReadInstalledContentSnapshotAsync(source) ??
            throw new InvalidDataException("The installed predecessor disappeared during upgrade.");
        Check.Equal(before, after,
            "upgrade preserves all predecessor headers, curves, and steps byte for byte");
        Check.True(pinnedInstalled.Revision.Sha256 == PetLearnedSkillContentBaseline.InstalledRevision &&
            !pinnedInstalled.TryGetCurve(428, 1, out _),
            "an already-pinned installed catalog does not acquire new skills after publication advances");
        await AssertPublishedRowsAsync(source, PetLearnedSkillContentBaseline.ExpectedRevision);
        await AssertInstalledRowsCopiedAsync(source);
        var reloaded = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        Check.Equal(catalogs[0].Revision, reloaded.Revision,
            "the upgraded publication is stable on reload");
        await AssertRejectedAsync(source,
            "UPDATE public.pet_skill_curve_steps SET absolute_value = absolute_value + 1 WHERE revision = @revision AND family_type = 428;",
            reloaded.Revision.Sha256, "published Vampiric fractions remain immutable");
    }

    private static async Task<string?> ReadInstalledContentSnapshotAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand(
            """
            SELECT jsonb_build_object(
                'header', to_jsonb(header),
                'curves', (SELECT jsonb_agg(to_jsonb(curve) ORDER BY family_type, priority)
                           FROM public.pet_skill_curve_definitions curve WHERE curve.revision = header.revision),
                'steps', (SELECT jsonb_agg(to_jsonb(step) ORDER BY family_type, priority, step_order)
                          FROM public.pet_skill_curve_steps step WHERE step.revision = header.revision))::text
            FROM public.pet_skill_content_revisions header WHERE revision = @revision;
            """);
        command.Parameters.AddWithValue("revision", PetLearnedSkillContentBaseline.InstalledRevision);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task AssertInstalledRowsCopiedAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM public.pet_skill_curve_definitions WHERE revision = @old) = 384,
                (SELECT count(*) FROM public.pet_skill_curve_steps WHERE revision = @old) = 1655,
                NOT EXISTS (
                    (SELECT to_jsonb(curve) - 'revision' FROM public.pet_skill_curve_definitions curve WHERE revision = @old
                     EXCEPT SELECT to_jsonb(curve) - 'revision' FROM public.pet_skill_curve_definitions curve WHERE revision = @new AND family_type <> 428)
                    UNION ALL
                    (SELECT to_jsonb(curve) - 'revision' FROM public.pet_skill_curve_definitions curve WHERE revision = @new AND family_type <> 428
                     EXCEPT SELECT to_jsonb(curve) - 'revision' FROM public.pet_skill_curve_definitions curve WHERE revision = @old)),
                NOT EXISTS (
                    (SELECT to_jsonb(step) - 'revision' FROM public.pet_skill_curve_steps step WHERE revision = @old
                     EXCEPT SELECT to_jsonb(step) - 'revision' FROM public.pet_skill_curve_steps step WHERE revision = @new AND family_type <> 428)
                    UNION ALL
                    (SELECT to_jsonb(step) - 'revision' FROM public.pet_skill_curve_steps step WHERE revision = @new AND family_type <> 428
                     EXCEPT SELECT to_jsonb(step) - 'revision' FROM public.pet_skill_curve_steps step WHERE revision = @old));
            """);
        command.Parameters.AddWithValue("old", PetLearnedSkillContentBaseline.InstalledRevision);
        command.Parameters.AddWithValue("new", PetLearnedSkillContentBaseline.ExpectedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync() && Enumerable.Range(0, 4).All(reader.GetBoolean),
            "all 384 installed curves and 1655 steps survive unchanged in both immutable revisions");
    }

    private static async Task SeedInstalledPublicationIfEmptyAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM public.pet_skill_content_publication);", connection, transaction))
            if ((bool)(await check.ExecuteScalarAsync())!) return;
        var baseline = PetLearnedSkillContentBaseline.CreateInstalled();
        var curves = baseline.Curves.Select(curve => new
        {
            family = curve.FamilyType, priority = curve.Priority, genre = curve.Genre, effect = curve.Effect,
            add = curve.OpaqueAdd, flag = curve.OpaqueFlag, first = curve.FirstRuntimeSkillId,
            agility = curve.LearnTraitRequirement.Agility, strength = curve.LearnTraitRequirement.Strength,
            accuracy = curve.LearnTraitRequirement.Accuracy, technique = curve.LearnTraitRequirement.Technique,
            wisdom = curve.LearnTraitRequirement.Wisdom, luck = curve.LearnTraitRequirement.Luck
        });
        var steps = baseline.Curves.SelectMany(curve => curve.Steps.Select(step => new
        {
            family = curve.FamilyType, priority = curve.Priority, order = step.StepOrder,
            runtime = step.RuntimeSkillId, rank = step.MinimumPetRank, value = step.AbsoluteValue
        }));
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.pet_skill_content_revisions (revision, curve_count, step_count, source, source_sha256)
            VALUES (@revision, 384, 1655, @source, @sourceSha);
            INSERT INTO public.pet_skill_curve_definitions
                (revision, family_type, priority, genre, effect, opaque_add, opaque_flag,
                 required_agility, required_strength, required_accuracy, required_technique, required_wisdom,
                 required_luck, first_runtime_skill_id)
            SELECT @revision, family, priority, genre, effect, "add", flag,
                   agility, strength, accuracy, technique, wisdom, luck, first
            FROM jsonb_to_recordset(@curves) AS r(family integer, priority smallint, genre integer, effect integer,
                "add" integer, flag integer, first integer, agility numeric, strength numeric, accuracy numeric,
                technique numeric, wisdom numeric, luck numeric);
            INSERT INTO public.pet_skill_curve_steps
                (revision, family_type, priority, step_order, runtime_skill_id, minimum_pet_rank, absolute_value)
            SELECT @revision, family, priority, "order", runtime, rank, value
            FROM jsonb_to_recordset(@steps) AS r(family integer, priority smallint, "order" smallint,
                runtime integer, rank smallint, value numeric);
            UPDATE public.pet_skill_content_revisions SET sealed_at = transaction_timestamp() WHERE revision = @revision;
            INSERT INTO public.pet_skill_content_publication(singleton, revision) VALUES (true, @revision);
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", baseline.Revision.Sha256);
        command.Parameters.AddWithValue("source", baseline.Revision.Source);
        command.Parameters.AddWithValue("sourceSha", baseline.Revision.SourceSha256);
        command.Parameters.AddWithValue("curves", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(curves));
        command.Parameters.AddWithValue("steps", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(steps));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
