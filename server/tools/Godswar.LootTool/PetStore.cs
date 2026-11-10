using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>The six editable numbers of one aptitude tier.</summary>
internal sealed record PetAptitudeEdit(
    short Aptitude,
    decimal MinimumTotalGrowth,
    decimal MaximumTotalGrowth,
    int MinimumInitialSavvy,
    int MaximumInitialSavvy,
    int MinimumAddedSavvy,
    int MaximumAddedSavvy);

internal sealed record PetAptitudeRow(
    short Aptitude,
    string DisplayName,
    decimal MinimumTotalGrowth,
    decimal MaximumTotalGrowth,
    int MinimumInitialSavvy,
    int MaximumInitialSavvy,
    int MinimumAddedSavvy,
    int MaximumAddedSavvy,
    short InnateTalentMask);

internal sealed record PetSpeciesRow(
    short SpeciesId,
    string DisplayName,
    short FoodKind,
    int StarterSkillId,
    string StarterSkillName,
    string LifetimeValues,
    int? EggItemId,
    int? MagicJadeItemId,
    short MinimumAptitude,
    short MaximumAptitude,
    bool BoundsSaved);

internal sealed record PetStatRow(
    string Stat,
    decimal InitialSavvy,
    decimal AddedSavvy,
    decimal GrowthRate);

internal sealed record OwnedPetRow(
    long PetId,
    int UserId,
    string OwnerName,
    short SpeciesId,
    string PetName,
    short Aptitude,
    short TalentMask,
    short Level,
    decimal Rank,
    int OpenedSkillSlots,
    decimal InitialSavvyBaseline,
    decimal RemainingLifetime,
    long Revision);

/// <summary>
/// Direct PostgreSQL access to the pet configuration tables and to the two pet
/// writes this tool is allowed to make: a species' hatch aptitude bounds, and an
/// owned pet's aptitude. Everything is read from the connected database, so the
/// tool follows whichever of <c>godswar</c> / <c>godswar_local</c> it points at.
/// </summary>
internal sealed class PetStore : IAsyncDisposable
{
    /// <summary>
    /// GM-side policy table. The game server never reads it; it records the bounds
    /// the operator wants and drives the egg re-roll below.
    /// </summary>
    private const string BoundsTable = "public.gm_pet_species_aptitude_bounds";

    private NpgsqlDataSource? _dataSource;

    /// <summary>
    /// Every content table keeps rows for all historical revisions, so each read
    /// has to be scoped to the published one or tiers appear duplicated.
    /// </summary>
    private string? _publishedRevision;

    /// <summary>
    /// Every content query must be scoped to the published revision: once the
    /// tool publishes an edited version, the tables hold one row set per revision
    /// and an unfiltered read would return each tier twice.
    /// </summary>
    private async Task<string> RevisionAsync(CancellationToken cancellationToken)
    {
        if (_publishedRevision is null)
        {
            _publishedRevision = (await LoadPublishedRevisionAsync(cancellationToken)).Revision;
        }

        return _publishedRevision;
    }

    private NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException("尚未连接数据库。");

    public bool IsConnected => _dataSource is not null;

    public string DatabaseName { get; private set; } = string.Empty;

    public void Connect(string connectionString)
    {
        Disconnect();
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        DatabaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? string.Empty;
    }

    public void Disconnect()
    {
        _dataSource?.Dispose();
        _dataSource = null;
        _publishedRevision = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    public async Task EnsureBoundsTableAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = $$"""
            CREATE TABLE IF NOT EXISTS {{BoundsTable}} (
                species_id smallint PRIMARY KEY,
                minimum_aptitude smallint NOT NULL,
                maximum_aptitude smallint NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT gm_pet_species_bounds_range
                    CHECK (minimum_aptitude BETWEEN 1 AND 16
                           AND maximum_aptitude BETWEEN 1 AND 16
                           AND minimum_aptitude <= maximum_aptitude)
            );
            """;

        await using var command = Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PetAptitudeRow>> LoadAptitudesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT aptitude, display_name, minimum_total_growth, maximum_total_growth,
                   minimum_initial_savvy, maximum_initial_savvy,
                   minimum_added_savvy, maximum_added_savvy, innate_talent_mask
              FROM public.pet_content_aptitude_definitions
             WHERE revision = @revision
             ORDER BY aptitude;
            """;

        await using var command = Source.CreateCommand(sql);
        AddText(command, "revision", await RevisionAsync(cancellationToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PetAptitudeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PetAptitudeRow(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt16(8)));
        }

        return rows;
    }

    public async Task<List<PetSpeciesRow>> LoadSpeciesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT s.species_id,
                   s.display_name,
                   s.food_kind,
                   s.starter_skill_id,
                   s.starter_skill_name,
                   COALESCE(array_to_string(s.lifetime_values, ','), ''),
                   s.egg_item_id,
                   s.magic_jade_item_id,
                   COALESCE(b.minimum_aptitude, 1)::smallint,
                   COALESCE(b.maximum_aptitude, 16)::smallint,
                   (b.species_id IS NOT NULL)
              FROM public.pet_content_species_definitions s
              LEFT JOIN {BoundsTable} b ON b.species_id = s.species_id
             WHERE s.revision = @revision
             ORDER BY s.species_id;
            """;

        await using var command = Source.CreateCommand(sql);
        AddText(command, "revision", await RevisionAsync(cancellationToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PetSpeciesRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PetSpeciesRow(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt16(8),
                reader.GetInt16(9),
                reader.GetBoolean(10)));
        }

        return rows;
    }

    public async Task SaveSpeciesBoundsAsync(
        IEnumerable<(short SpeciesId, short Minimum, short Maximum)> bounds,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            INSERT INTO {BoundsTable}
                (species_id, minimum_aptitude, maximum_aptitude, updated_at)
            VALUES (@speciesId, @minimum, @maximum, now())
            ON CONFLICT (species_id) DO UPDATE
               SET minimum_aptitude = EXCLUDED.minimum_aptitude,
                   maximum_aptitude = EXCLUDED.maximum_aptitude,
                   updated_at = now();
            """;

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (speciesId, minimum, maximum) in bounds)
        {
            ValidateRange(speciesId, minimum, maximum);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddInt16(command, "speciesId", speciesId);
            AddInt16(command, "minimum", minimum);
            AddInt16(command, "maximum", maximum);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteSpeciesBoundsAsync(
        short speciesId,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"DELETE FROM {BoundsTable} WHERE species_id = @speciesId;";
        await using var command = Source.CreateCommand(sql);
        AddInt16(command, "speciesId", speciesId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<OwnedPetRow>> LoadOwnedPetsAsync(
        string ownerName = "",
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT p.id,
                   p.user_id,
                   COALESCE(c.name, ''),
                   p.species_id,
                   p.name,
                   p.aptitude,
                   p.talent_mask,
                   p.level,
                   p.rank,
                   p.opened_skill_slots,
                   p.initial_savvy_baseline_total,
                   p.remaining_lifetime,
                   p.revision
              FROM public.character_pets p
              LEFT JOIN public.character_base c ON c.id = p.user_id
             WHERE @ownerName = '' OR LOWER(c.name) LIKE '%' || LOWER(@ownerName) || '%'
             ORDER BY p.user_id, p.id;
            """;

        await using var command = Source.CreateCommand(sql);
        AddText(command, "ownerName", ownerName.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OwnedPetRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OwnedPetRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt16(3),
                reader.GetString(4),
                reader.GetInt16(5),
                reader.GetInt16(6),
                reader.GetInt16(7),
                reader.GetDecimal(8),
                reader.GetInt16(9),
                reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetInt64(12)));
        }

        return rows;
    }

    public async Task<List<PetStatRow>> LoadPetStatsAsync(
        long petId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT stat_code, initial_savvy, added_savvy, base_growth_rate
              FROM public.character_pet_stat_values
             WHERE pet_id = @petId
             ORDER BY stat_code;
            """;

        await using var command = Source.CreateCommand(sql);
        AddInt64(command, "petId", petId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PetStatRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PetStatRow(
                PetContent.StatName(reader.GetInt16(0)),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3)));
        }

        return rows;
    }

    /// <summary>
    /// Rewrites one owned pet's aptitude. The database enforces the talent mask
    /// against the aptitude (<c>ck_character_pets_quality_innate_talents</c>) and
    /// pins the birth rank to a row of the new tier's own hatch table, so both
    /// travel with the change. The six savvy vectors are deliberately untouched.
    /// </summary>
    public async Task<string> UpdatePetAptitudeAsync(
        long petId,
        short target,
        CancellationToken cancellationToken = default)
    {
        if (target is < PetContent.MinimumAptitude or > PetContent.MaximumAptitude)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                $"档位必须在 {PetContent.MinimumAptitude}-{PetContent.MaximumAptitude} 之间。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var pet = await LockPetAsync(connection, transaction, petId, cancellationToken);
        var mask = PetContent.TalentMaskFor(target);
        var birthRank = pet.BirthRank;
        var outcomeOrder = pet.OutcomeOrder;
        if (pet.HatchRevision is not null)
        {
            var resolved = await ResolveHatchRankAsync(
                    connection,
                    transaction,
                    pet.HatchRevision,
                    target,
                    outcomeOrder ?? 0,
                    cancellationToken)
                ?? (outcomeOrder == 0
                    ? null
                    : await ResolveHatchRankAsync(
                        connection,
                        transaction,
                        pet.HatchRevision,
                        target,
                        0,
                        cancellationToken));
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    $"本库缺少 {target} 档的孵化等级倍率数据" +
                    "（pet_content_hatch_rank_steps），为保持外键完整已放弃本次改档。");
            }

            birthRank = resolved.Value.Rank;
            outcomeOrder = resolved.Value.OutcomeOrder;
        }

        const string updateSql = """
            UPDATE public.character_pets
               SET aptitude = @aptitude,
                   talent_mask = @talentMask,
                   has_owner_merge_talent = (@talentMask::integer & 16) = 16,
                   birth_rank = @birthRank,
                   hatch_rank_outcome_order = @outcomeOrder,
                   revision = revision + 1,
                   updated_at = now()
             WHERE id = @petId;
            """;

        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddInt64(update, "petId", petId);
            AddInt16(update, "aptitude", target);
            AddInt16(update, "talentMask", mask);
            AddNullableDecimal(update, "birthRank", birthRank);
            AddNullableInt16(update, "outcomeOrder", outcomeOrder);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("更新失败：影响行数不是 1，已回滚。");
            }
        }

        const string auditSql = """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot, pet_id, pet_id_snapshot,
                operation, outcome, before_state, after_state, reason_code)
            VALUES (@requestId, @userId, @userId, @petId, @petId,
                    'gm_tool_set_aptitude', 'Committed',
                    @before::jsonb, @after::jsonb, 'gm-tool');
            """;

        await using (var audit = new NpgsqlCommand(auditSql, connection, transaction))
        {
            AddGuid(audit, "requestId", Guid.NewGuid());
            AddInt32(audit, "userId", pet.UserId);
            AddInt64(audit, "petId", petId);
            AddJson(audit, "before", BuildPetStateJson(
                pet.UserId, pet.Aptitude, pet.TalentMask, pet.Revision));
            AddJson(audit, "after", BuildPetStateJson(
                pet.UserId, target, mask, pet.Revision + 1));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return $"{pet.Name}：{PetContent.DescribeAptitude(pet.Aptitude)} → " +
            $"{PetContent.DescribeAptitude(target)}，天赋掩码 {pet.TalentMask} → {mask}" +
            $"（{PetContent.DescribeTalentMask(mask)}）";
    }

    /// <summary>
    /// Applies each species' bounds to the eggs a character carries by rewriting
    /// the egg instances' <c>item_quality</c>. This is the only lever the hatch
    /// path actually reads: hatching takes the egg's quality verbatim as the
    /// pet's aptitude, so future pets follow these bounds with no server change.
    /// </summary>
    public async Task<int> RandomizeEggAptitudesAsync(
        IReadOnlyList<PetSpeciesRow> species,
        string ownerName,
        CancellationToken cancellationToken = default)
    {
        var bounds = species
            .Where(row => row.EggItemId is not null)
            .ToDictionary(
                row => row.EggItemId!.Value,
                row => (row.MinimumAptitude, row.MaximumAptitude));
        if (bounds.Count == 0)
        {
            throw new InvalidOperationException("没有任何物种配了蛋道具 id，无法定位要改的蛋。");
        }

        const string selectSql = """
            SELECT i.id, i.prop_id
              FROM public.character_items i
              JOIN public.character_base c ON c.id = i.user_id
             WHERE i.prop_id = ANY(@eggItemIds)
               AND (@ownerName = '' OR LOWER(c.name) LIKE '%' || LOWER(@ownerName) || '%');
            """;

        var targets = new List<(long Id, int PropId)>();
        await using (var connection = await Source.OpenConnectionAsync(cancellationToken))
        {
            await using var select = new NpgsqlCommand(selectSql, connection);
            select.Parameters.Add(new NpgsqlParameter<int[]>("eggItemIds", bounds.Keys.ToArray()));
            AddText(select, "ownerName", ownerName.Trim());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add((reader.GetInt64(0), reader.GetInt32(1)));
            }
        }

        if (targets.Count == 0)
        {
            return 0;
        }

        var rng = Random.Shared;
        const string updateSql = """
            UPDATE public.character_items
               SET item_quality = @quality
             WHERE id = @id;
            """;

        await using var connection2 = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection2.BeginTransactionAsync(cancellationToken);
        var changed = 0;
        foreach (var (id, propId) in targets)
        {
            var (minimum, maximum) = bounds[propId];
            await using var update = new NpgsqlCommand(updateSql, connection2, transaction);
            AddInt64(update, "id", id);
            AddInt16(update, "quality", (short)rng.Next(minimum, maximum + 1));
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    /// <summary>The hatch-rank content version this database publishes, if any.</summary>
    public async Task<string?> LoadHatchRankRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT @revision FROM public.pet_content_hatch_rank_steps
             WHERE revision = @revision
             LIMIT 1;
            """;

        await using var command = Source.CreateCommand(sql);
        AddText(command, "revision", await RevisionAsync(cancellationToken));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<bool> HasHatchRankAsync(
        string revision,
        short aptitude,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT 1 FROM public.pet_content_hatch_rank_steps
             WHERE revision = @revision AND aptitude = @aptitude
             LIMIT 1;
            """;

        await using var command = Source.CreateCommand(sql);
        AddText(command, "revision", revision);
        AddInt16(command, "aptitude", aptitude);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private sealed record PetLock(
        int UserId,
        string Name,
        short Aptitude,
        short TalentMask,
        long Revision,
        string? HatchRevision,
        short? OutcomeOrder,
        decimal? BirthRank);

    private static async Task<PetLock> LockPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_id, name, aptitude, talent_mask, revision,
                   hatch_rank_content_revision, hatch_rank_outcome_order, birth_rank
              FROM public.character_pets
             WHERE id = @petId
             FOR UPDATE;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddInt64(command, "petId", petId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"宠物 id {petId} 不存在。");
        }

        var pet = new PetLock(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt16(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7));
        return pet;
    }

    private static async Task<(decimal Rank, short OutcomeOrder)?> ResolveHatchRankAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        short aptitude,
        short outcomeOrder,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT rank FROM public.pet_content_hatch_rank_steps
             WHERE revision = @revision AND aptitude = @aptitude
               AND outcome_order = @outcomeOrder;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "revision", revision);
        AddInt16(command, "aptitude", aptitude);
        AddInt16(command, "outcomeOrder", outcomeOrder);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : ((decimal)value, outcomeOrder);
    }

    private static string BuildPetStateJson(
        int userId,
        short aptitude,
        int mask,
        long revision) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["aptitude"] = aptitude,
            ["talentMask"] = mask,
            ["revision"] = revision,
            ["source"] = "Godswar.LootTool 宠物档位工具"
        });

    private static void ValidateRange(short speciesId, short minimum, short maximum)
    {
        if (minimum is < 1 or > 16 || maximum is < 1 or > 16 || minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speciesId),
                $"种族 {speciesId} 的档位上下限非法：{minimum}-{maximum}" +
                "（必须在 1-16 且下限不大于上限）。");
        }
    }

    /// <summary>The published pet-content revision this database currently serves.</summary>
    public async Task<(string Revision, string Source)> LoadPublishedRevisionAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT publication.revision, revision.source
              FROM public.pet_content_publication publication
              JOIN public.pet_content_revisions revision
                ON revision.revision = publication.revision
             WHERE publication.family = 'pets';
            """;

        await using var command = Source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("本库没有已发布的宠物内容版本。");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Publishes the edited aptitude brackets as a <b>new</b> revision.
    ///
    /// <c>pet_content_aptitude_definitions</c> (and the five other content tables)
    /// carry <c>reject_pet_content_mutation()</c>, which raises on any UPDATE or
    /// DELETE of published content - by design. The only supported way to change a
    /// number is therefore to copy the whole published set under a fresh revision
    /// id, inserting the edited values into the new copy, and then re-point
    /// <c>pet_content_publication</c>. The publication trigger seals the new
    /// revision and rejects it if any declared count does not match, so a partial
    /// copy can never be published. The previous revision stays in place, which
    /// keeps every <c>character_pets</c> foreign key resolvable.
    /// </summary>
    public async Task<string> PublishEditedAptitudesAsync(
        IReadOnlyList<PetAptitudeEdit> edits,
        CancellationToken cancellationToken = default)
    {
        if (edits.Count != PetContent.MaximumAptitude)
        {
            throw new InvalidOperationException(
                $"必须一次给满 1-{PetContent.MaximumAptitude} 档，本次收到 {edits.Count} 档。");
        }

        foreach (var edit in edits)
        {
            ValidateEdit(edit);
        }

        var (current, _) = await LoadPublishedRevisionAsync(cancellationToken);
        var byTier = edits.ToDictionary(edit => edit.Aptitude);
        var source = $"gm-tool-aptitude-{DateTime.Now:yyyyMMdd-HHmmss}";

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var aptitudes = await LoadAptitudesAsync(cancellationToken);
        var counts = await LoadContentCountsAsync(connection, transaction, current, cancellationToken);
        var revision = ComputeRevisionId(current, edits, source);

        const string insertRevision = """
            INSERT INTO public.pet_content_revisions (
                revision, species_count, aptitude_count, native_profile_count,
                experience_step_count, rebirth_step_count, merge_savvy_step_count,
                merge_savvy_lookup_count, hatch_rank_step_count, merge_rank_lookup_count,
                merge_rank_species_factor_count, merge_rank_spirit_step_count, source)
            VALUES (@revision, @speciesCount, @aptitudeCount, @nativeProfileCount,
                    @experienceStepCount, @rebirthStepCount, @mergeSavvyStepCount,
                    @mergeSavvyLookupCount, @hatchRankStepCount, @mergeRankLookupCount,
                    @mergeRankSpeciesFactorCount, @mergeRankSpiritStepCount, @source);
            """;

        await using (var command = new NpgsqlCommand(insertRevision, connection, transaction))
        {
            AddText(command, "revision", revision);
            AddInt32(command, "speciesCount", counts["pet_content_species_definitions"]);
            AddInt32(command, "aptitudeCount", counts["pet_content_aptitude_definitions"]);
            AddInt32(command, "nativeProfileCount", counts["pet_content_native_profiles"]);
            AddInt32(command, "experienceStepCount", counts["pet_content_experience_steps"]);
            AddInt32(command, "rebirthStepCount", counts["pet_content_rebirth_steps"]);
            AddInt32(command, "mergeSavvyStepCount", counts["pet_content_merge_savvy_steps"]);
            AddInt32(command, "mergeSavvyLookupCount", counts["pet_content_merge_savvy_lookup"]);
            AddInt32(command, "hatchRankStepCount", counts["pet_content_hatch_rank_steps"]);
            AddInt32(command, "mergeRankLookupCount", counts["pet_content_merge_rank_lookup"]);
            AddInt32(command, "mergeRankSpeciesFactorCount",
                counts["pet_content_merge_rank_species_factors"]);
            AddInt32(command, "mergeRankSpiritStepCount",
                counts["pet_content_merge_rank_spirit_steps"]);
            AddText(command, "source", source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in aptitudes)
        {
            await InsertEditedAptitudeAsync(
                connection, transaction, revision, row, byTier[row.Aptitude], cancellationToken);
        }

        foreach (var table in CopyTables)
        {
            await CopyTableAsync(connection, transaction, table, current, revision, cancellationToken);
        }

        const string publish = """
            INSERT INTO public.pet_content_publication (family, revision, published_at)
            VALUES ('pets', @revision, now())
            ON CONFLICT (family) DO UPDATE
               SET revision = EXCLUDED.revision,
                   published_at = EXCLUDED.published_at;
            """;

        await using (var command = new NpgsqlCommand(publish, connection, transaction))
        {
            AddText(command, "revision", revision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _publishedRevision = null;
        return revision;
    }

    private static void ValidateEdit(PetAptitudeEdit edit)
    {
        static bool Invalid(decimal value) => value < 0m || value > 1000m;

        if (edit.Aptitude is < PetContent.MinimumAptitude or > PetContent.MaximumAptitude)
        {
            throw new ArgumentOutOfRangeException(nameof(edit), $"档位 {edit.Aptitude} 非法。");
        }

        if (Invalid(edit.MinimumTotalGrowth) || Invalid(edit.MaximumTotalGrowth) ||
            edit.MinimumTotalGrowth > edit.MaximumTotalGrowth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"{edit.Aptitude} 档成长率区间非法：" +
                $"{edit.MinimumTotalGrowth}-{edit.MaximumTotalGrowth}。");
        }

        if (edit.MinimumInitialSavvy < 1 || edit.MinimumAddedSavvy < 1 ||
            edit.MinimumInitialSavvy > edit.MaximumInitialSavvy ||
            edit.MinimumAddedSavvy > edit.MaximumAddedSavvy)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"{edit.Aptitude} 档资质/附加值区间非法：{edit.MinimumInitialSavvy}-" +
                $"{edit.MaximumInitialSavvy} 与 {edit.MinimumAddedSavvy}-" +
                $"{edit.MaximumAddedSavvy}。库里 ck_pet_content_aptitudes_savvy 要求" +
                "两者下限至少为 1、且下限不大于上限。");
        }
    }

    private static async Task InsertEditedAptitudeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        PetAptitudeRow row,
        PetAptitudeEdit edit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.pet_content_aptitude_definitions (
                revision, aptitude, name_key, display_name, is_server_extension,
                minimum_total_growth, maximum_total_growth,
                maximum_growth_stat_deviation, minimum_initial_savvy, maximum_initial_savvy,
                maximum_initial_savvy_stat_deviation, minimum_added_savvy,
                maximum_added_savvy, innate_talent_mask)
            VALUES (@revision, @aptitude, @nameKey, @displayName, @extension,
                    @minimumGrowth, @maximumGrowth, @deviation, @minimumSavvy, @maximumSavvy,
                    @deviation, @minimumAdded, @maximumAdded, @mask);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "revision", revision);
        AddInt16(command, "aptitude", row.Aptitude);
        AddText(command, "nameKey", $"PET_APTITUDE_{row.Aptitude}");
        AddText(command, "displayName", row.DisplayName);
        command.Parameters.Add(new NpgsqlParameter<bool>("extension", row.Aptitude >= 15));
        AddDecimal(command, "minimumGrowth", edit.MinimumTotalGrowth);
        AddDecimal(command, "maximumGrowth", edit.MaximumTotalGrowth);
        AddDecimal(command, "deviation", 0.12m);
        AddInt32(command, "minimumSavvy", edit.MinimumInitialSavvy);
        AddInt32(command, "maximumSavvy", edit.MaximumInitialSavvy);
        AddInt32(command, "minimumAdded", edit.MinimumAddedSavvy);
        AddInt32(command, "maximumAdded", edit.MaximumAddedSavvy);
        AddInt16(command, "mask", PetContent.TalentMaskFor(row.Aptitude));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Every other revision-keyed pet table is copied verbatim. Column lists come
    /// from <c>information_schema</c> so a future content column cannot be dropped
    /// silently by this tool.
    /// </summary>
    private static readonly string[] CopyTables =
    [
        "pet_content_settings",
        "pet_content_species_definitions",
        "pet_content_native_profiles",
        "pet_content_experience_steps",
        "pet_content_rebirth_steps",
        "pet_content_merge_savvy_steps",
        "pet_content_merge_savvy_lookup",
        "pet_content_hatch_rank_steps",
        "pet_content_merge_rank_lookup",
        "pet_content_merge_rank_species_factors",
        "pet_content_merge_rank_spirit_steps"
    ];

    private static async Task CopyTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        var columns = await LoadContentColumnsAsync(connection, transaction, table, cancellationToken);
        var list = string.Join(", ", columns);
        var sql = $"""
            INSERT INTO public.{table} ({list}, revision)
            SELECT {list}, @to FROM public.{table} WHERE revision = @from;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "from", from);
        AddText(command, "to", to);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> LoadContentColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = @table
               AND column_name <> 'revision'
             ORDER BY ordinal_position;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"内容表 {table} 没有可复制的列。");
        }

        return columns;
    }

    private static async Task<Dictionary<string, int>> LoadContentCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in new[] { "pet_content_aptitude_definitions" }
            .Concat(CopyTables))
        {
            var sql = $"SELECT count(*)::int FROM public.{table} WHERE revision = @revision;";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddText(command, "revision", revision);
            counts[table] = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        return counts;
    }

    private static string ComputeRevisionId(
        string parent,
        IReadOnlyList<PetAptitudeEdit> edits,
        string source)
    {
        var payload = new StringBuilder()
            .Append(parent)
            .Append('|')
            .Append(source);
        foreach (var edit in edits.OrderBy(row => row.Aptitude))
        {
            payload.Append('|')
                .Append(edit.Aptitude).Append(',')
                .Append(edit.MinimumTotalGrowth).Append(',')
                .Append(edit.MaximumTotalGrowth).Append(',')
                .Append(edit.MinimumInitialSavvy).Append(',')
                .Append(edit.MaximumInitialSavvy).Append(',')
                .Append(edit.MinimumAddedSavvy).Append(',')
                .Append(edit.MaximumAddedSavvy);
        }

        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload.ToString()))).ToUpperInvariant();
    }

    private static void AddDecimal(NpgsqlCommand command, string name, decimal value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = value });

    private static void AddInt16(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, value));

    private static void AddInt32(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, value));

    private static void AddInt64(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, value));

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter<string>(name, value));

    private static void AddGuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid>(name, value));

    private static void AddJson(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = value });

    private static void AddNullableDecimal(
        NpgsqlCommand command,
        string name,
        decimal? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Numeric)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddNullableInt16(
        NpgsqlCommand command,
        string name,
        short? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
}
