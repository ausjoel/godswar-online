using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>
/// One faction's derived total, and what the two ledgers hold behind it.
/// </summary>
internal sealed record FarmFactionTotals(
    long SpartaPoints,
    long AthensPoints,
    int Members,
    int DonationRows,
    int KillRows);

/// <summary>One character's standing on the activity's score board.</summary>
internal sealed record FarmCharacterRow(
    int Id,
    string Name,
    short Camp,
    byte Faction,
    long Donated,
    long Kills,
    long Personal,
    long Rank)
{
    public string CampName => Camp == LelantineFarmRules.AthensCamp ? "雅典" : "斯巴达";
}

/// <summary>One row of the donation ledger.</summary>
internal sealed record FarmDonationRow(
    long Id,
    short Faction,
    int ItemId,
    int EggCount,
    int Points,
    DateTime DonatedAt);

/// <summary>One row of the credited-kill ledger.</summary>
internal sealed record FarmKillRow(
    short Faction,
    long KillPoints,
    long CreditedKills,
    DateTime UpdatedAt);

/// <summary>One farm actor exactly as the published revision places it.</summary>
internal sealed record FarmNpcRow(
    short MapId,
    string NpcKey,
    string TemplateKey,
    long ObjectId,
    float X,
    float Z,
    float Facing,
    long AppearanceType);

/// <summary>One kit-bag stack of an item the activity deals in.</summary>
internal sealed record FarmBagRow(
    int Slot,
    int ItemId,
    short Quality,
    short Stack,
    string DisplayName);

/// <summary>Where one granted stack lands.</summary>
internal sealed record FarmPlacement(
    int Slot,
    int Before,
    int After,
    int Added,
    bool NewRow);

/// <summary>A planned or committed item handout.</summary>
internal sealed record FarmGrantPlan(
    string CharacterName,
    int ItemId,
    string DisplayName,
    int Quantity,
    int StackCap,
    short Quality,
    int SlotsBeyondUnlockedPages,
    bool DryRun,
    IReadOnlyList<FarmPlacement> Placements);

/// <summary>What a reset removed.</summary>
internal sealed record FarmResetReport(
    long DonationRows,
    long KillRows,
    long Points);

/// <summary>One rule value the server holds in code rather than in the database.</summary>
internal sealed record FarmRuleConstant(
    string Name,
    string Value,
    string Meaning,
    string Source);

/// <summary>
/// The Lelantine Farm's own data layer: the two score ledgers the server writes,
/// the items the activity deals in, and read-only views of the rules and the
/// published roster.
/// </summary>
/// <remarks>
/// <para>
/// The faction total is <b>derived</b>: the server computes it as the sum of the
/// same-faction personal scores in <c>lelantine_farm_personal_points</c>, so this
/// tool never writes a faction total. Adding, changing or deleting a ledger row
/// moves both the character's personal score and their camp's total in the same
/// breath, which is what makes the tool safe to use on live data.
/// </para>
/// <para>
/// Score edits are ledger rows. A manual adjustment is written as a donation row
/// with <c>item_id = 0</c>, which no real egg can be, so an operator can always
/// tell an adjustment from a donation; the donation ledger's own checks still
/// hold (count 1..99, points &gt; 0). Item handouts and removals record a row in
/// <c>character_item_audit</c> with <c>source = 'gm-tool'</c>, the same trail the
/// web GM tool leaves.
/// </para>
/// </remarks>
internal sealed class FarmStore : IAsyncDisposable
{
    private const int KitBagLocation = 1;
    private const int KitBagSlots = 96;
    private const int KitBagSlotsPerPage = 24;
    private const int MaximumGrantQuantity = 100000;

    private NpgsqlDataSource? _dataSource;

    public string DatabaseName { get; private set; } = string.Empty;

    public bool IsConnected => _dataSource is not null;

    public void Connect(string connectionString)
    {
        _dataSource?.Dispose();
        _dataSource = NpgsqlDataSource.Create(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        DatabaseName = builder.Database ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    private NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException("尚未连接数据库。");

    /// <summary>
    /// Whether this database has the farm score schema at all. A database that
    /// has not run the server's 20260926_210 migration answers false.
    /// </summary>
    public async Task<bool> HasFarmSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('public.lelantine_farm_personal_points') IS NOT NULL
               AND to_regclass('public.lelantine_farm_donations') IS NOT NULL
               AND to_regclass('public.lelantine_farm_kill_points') IS NOT NULL;
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    /// <summary>Both camps' totals and the size of the ledgers behind them.</summary>
    public async Task<FarmFactionTotals> LoadTotalsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                COALESCE(SUM(points) FILTER (WHERE faction = 0), 0)::bigint,
                COALESCE(SUM(points) FILTER (WHERE faction = 1), 0)::bigint,
                COUNT(*)::integer,
                (SELECT COUNT(*)::integer FROM lelantine_farm_donations),
                (SELECT COUNT(*)::integer FROM lelantine_farm_kill_points)
            FROM lelantine_farm_personal_points;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new FarmFactionTotals(0, 0, 0, 0, 0);
        }

        return new FarmFactionTotals(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    /// <summary>
    /// The score board: one row per fighter, ordered by score, with the rank the
    /// activity's own ranking page prints.
    /// </summary>
    public async Task<IReadOnlyList<FarmCharacterRow>> LoadCharactersAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH personal AS (
                SELECT character_id, faction, points
                FROM public.lelantine_farm_personal_points
            ),
            totals AS (
                SELECT character_id, SUM(points)::bigint AS points
                FROM personal
                GROUP BY character_id
            ),
            ranked AS (
                SELECT
                    totals.character_id,
                    totals.points,
                    RANK() OVER (ORDER BY totals.points DESC)::bigint AS rank
                FROM totals
            )
            SELECT
                cb.id,
                cb.name,
                cb.camp,
                COALESCE(own.faction, cb.camp)::smallint AS faction,
                COALESCE((
                    SELECT SUM(score.points) FROM personal score
                    WHERE score.character_id = cb.id
                ), 0)::bigint AS personal_points,
                COALESCE((
                    SELECT SUM(donation.points)
                    FROM public.lelantine_farm_donations donation
                    WHERE donation.character_id = cb.id
                ), 0)::bigint AS donated_points,
                COALESCE((
                    SELECT SUM(kill.kill_points)
                    FROM public.lelantine_farm_kill_points kill
                    WHERE kill.character_id = cb.id
                ), 0)::bigint AS kill_points,
                COALESCE(ranked.rank, 0)::bigint AS rank
            FROM character_base cb
            LEFT JOIN ranked ON ranked.character_id = cb.id
            LEFT JOIN LATERAL (
                SELECT score.faction
                FROM personal score
                WHERE score.character_id = cb.id
                ORDER BY score.points DESC, score.faction
                LIMIT 1
            ) AS own ON true
            WHERE @query = ''
               OR cb.name ILIKE '%' || @query || '%'
               OR cb.id::text = @query
            ORDER BY COALESCE(ranked.rank, 2147483647), cb.name
            LIMIT @limit;
            """,
            connection);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 2000));

        var rows = new List<FarmCharacterRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var camp = reader.GetInt16(2);
            rows.Add(new FarmCharacterRow(
                reader.GetInt32(0),
                reader.GetString(1),
                camp,
                checked((byte)reader.GetInt16(3)),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(4),
                reader.GetInt64(7)));
        }

        return rows;
    }

    /// <summary>One character's donation ledger, newest first.</summary>
    public async Task<IReadOnlyList<FarmDonationRow>> LoadDonationsAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, faction, item_id, egg_count, points, donated_at
            FROM lelantine_farm_donations
            WHERE character_id = @id
            ORDER BY donated_at DESC, id DESC;
            """,
            connection);
        command.Parameters.AddWithValue("id", characterId);

        var rows = new List<FarmDonationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FarmDonationRow(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDateTime(5)));
        }

        return rows;
    }

    /// <summary>One character's credited-kill ledger.</summary>
    public async Task<IReadOnlyList<FarmKillRow>> LoadKillsAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT faction, kill_points, credited_kills, updated_at
            FROM lelantine_farm_kill_points
            WHERE character_id = @id
            ORDER BY faction;
            """,
            connection);
        command.Parameters.AddWithValue("id", characterId);

        var rows = new List<FarmKillRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FarmKillRow(
                reader.GetInt16(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetDateTime(3)));
        }

        return rows;
    }

    /// <summary>The activity's items the character carries: eggs and tuck nets.</summary>
    public async Task<IReadOnlyList<FarmBagRow>> LoadFarmBagAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                ci.slot_index,
                ci.prop_id,
                ci.item_quality,
                ci.stack,
                COALESCE(it.display_name, '')
            FROM character_items ci
            LEFT JOIN item_templates it ON it.id = ci.prop_id
            WHERE ci.user_id = @id
              AND ci.item_location = 1
              AND ci.prop_id = ANY(@items)
            ORDER BY ci.prop_id, ci.item_quality, ci.slot_index;
            """,
            connection);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("items", LelantineFarmRules.FarmItemIds);

        var rows = new List<FarmBagRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FarmBagRow(
                reader.GetInt16(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt16(3),
                reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>
    /// The farm actors exactly as the currently published revision places them.
    /// </summary>
    public async Task<IReadOnlyList<FarmNpcRow>> LoadPublishedNpcsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                spawn.map_id,
                spawn.npc_key,
                spawn.template_key,
                spawn.object_id,
                spawn.pos_x,
                spawn.pos_z,
                spawn.facing,
                spawn.appearance_type
            FROM npc_spawn_definitions spawn
            JOIN npc_content_publication publication
              ON publication.revision = spawn.revision
            WHERE publication.family = 'npcs'
              AND spawn.map_id = @map
              AND spawn.npc_key LIKE 'Lelantine_Farm_%'
            ORDER BY spawn.object_id;
            """,
            connection);
        command.Parameters.AddWithValue("map", LelantineFarmRules.MapId);

        var rows = new List<FarmNpcRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FarmNpcRow(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetFloat(4),
                reader.GetFloat(5),
                reader.GetFloat(6),
                reader.GetInt64(7)));
        }

        return rows;
    }

    /// <summary>
    /// Adds points to a character's personal score for one camp as a manual
    /// adjustment row.
    /// </summary>
    public async Task AddAdjustmentAsync(
        int characterId,
        short faction,
        int points,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (points is < 1 or > 999999999)
        {
            throw new FarmToolException("调整分值必须在 1 ~ 999999999 之间。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO lelantine_farm_donations (
                character_id, faction, item_id, egg_count, points
            )
            VALUES (@id, @faction, 0, 1, @points);
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            command.Parameters.AddWithValue("faction", faction);
            command.Parameters.AddWithValue("points", points);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Rewrites one donation row's points.</summary>
    public async Task SetDonationPointsAsync(
        long rowId,
        int points,
        CancellationToken cancellationToken = default)
    {
        if (points < 1)
        {
            throw new FarmToolException("分值必须大于 0；要清零请删除该行。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE lelantine_farm_donations
            SET points = @points
            WHERE id = @id;
            """,
            connection);
        command.Parameters.AddWithValue("points", points);
        command.Parameters.AddWithValue("id", rowId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new FarmToolException($"捐赠流水 {rowId} 不存在。");
        }
    }

    /// <summary>Deletes one donation row.</summary>
    public async Task DeleteDonationAsync(
        long rowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM lelantine_farm_donations WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", rowId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new FarmToolException($"捐赠流水 {rowId} 不存在。");
        }
    }

    /// <summary>Sets one character's credited-kill row for one camp.</summary>
    public async Task SetKillPointsAsync(
        int characterId,
        short faction,
        long killPoints,
        long creditedKills,
        CancellationToken cancellationToken = default)
    {
        if (killPoints < 0 || creditedKills < 0)
        {
            throw new FarmToolException("击杀积分与击杀数不能为负。");
        }

        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO lelantine_farm_kill_points (
                character_id, faction, kill_points, credited_kills
            )
            VALUES (@id, @faction, @points, @kills)
            ON CONFLICT (character_id, faction) DO UPDATE
            SET kill_points = EXCLUDED.kill_points,
                credited_kills = EXCLUDED.credited_kills,
                updated_at = now();
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            command.Parameters.AddWithValue("faction", faction);
            command.Parameters.AddWithValue("points", killPoints);
            command.Parameters.AddWithValue("kills", creditedKills);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes one character's credited-kill row for one camp.</summary>
    public async Task DeleteKillRowAsync(
        int characterId,
        short faction,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM lelantine_farm_kill_points
            WHERE character_id = @id AND faction = @faction;
            """,
            connection);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("faction", faction);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new FarmToolException("该角色在这个阵营没有击杀积分行。");
        }
    }

    /// <summary>Removes every farm score row a character owns.</summary>
    public async Task<FarmResetReport> ResetCharacterAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        long donationRows;
        await using (var command = new NpgsqlCommand(
            "DELETE FROM lelantine_farm_donations WHERE character_id = @id;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            donationRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long killRows;
        await using (var command = new NpgsqlCommand(
            "DELETE FROM lelantine_farm_kill_points WHERE character_id = @id;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            killRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FarmResetReport(donationRows, killRows, 0);
    }

    /// <summary>
    /// Removes every farm score row of one camp. This zeroes that camp's total
    /// and the personal score of every fighter who scored for it.
    /// </summary>
    public async Task<FarmResetReport> ResetFactionAsync(
        short faction,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long points;
        await using (var command = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(points), 0)::bigint
            FROM lelantine_farm_personal_points
            WHERE faction = @faction;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("faction", faction);
            points = checked((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L));
        }

        long donationRows;
        await using (var command = new NpgsqlCommand(
            "DELETE FROM lelantine_farm_donations WHERE faction = @faction;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("faction", faction);
            donationRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long killRows;
        await using (var command = new NpgsqlCommand(
            "DELETE FROM lelantine_farm_kill_points WHERE faction = @faction;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("faction", faction);
            killRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FarmResetReport(donationRows, killRows, points);
    }

    /// <summary>
    /// Hands an activity item to a character's kit bag, following the server's
    /// own stacking rules: fill partial stacks first, then take free slots.
    /// </summary>
    /// <param name="quality">
    /// The stack's <c>item_quality</c>, which for a hound egg is the pet aptitude
    /// it will hatch as - the value the donation scores by.
    /// </param>
    public async Task<FarmGrantPlan> GrantItemAsync(
        int characterId,
        int itemId,
        int quantity,
        short quality,
        string? note,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (quantity < 1)
        {
            throw new FarmToolException("数量必须至少为 1。");
        }

        quantity = Math.Clamp(quantity, 1, MaximumGrantQuantity);
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var character = await ReadCharacterAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var item = await ReadItemAsync(
            connection,
            transaction,
            itemId,
            cancellationToken);

        var plan = new List<FarmPlacement>();
        var claimedSlots = new HashSet<int>();
        var remaining = quantity;
        if (item.StackCap > 1)
        {
            foreach (var (slot, stack) in await ReadPartialStacksAsync(
                         connection,
                         transaction,
                         characterId,
                         itemId,
                         item.StackCap,
                         cancellationToken))
            {
                if (remaining <= 0)
                {
                    break;
                }

                var add = Math.Min(item.StackCap - stack, remaining);
                plan.Add(new FarmPlacement(slot, stack, stack + add, add, NewRow: false));
                remaining -= add;
            }
        }

        while (remaining > 0)
        {
            var freeSlot = await ReadFreeKitBagSlotAsync(
                connection,
                transaction,
                characterId,
                claimedSlots,
                cancellationToken);
            if (freeSlot is null)
            {
                throw new FarmToolException(
                    $"角色 {character.Name} 的背包已满（{KitBagSlots} 格全部占用），" +
                    "本次未写入任何数据。");
            }

            var add = Math.Min(item.StackCap, remaining);
            plan.Add(new FarmPlacement(freeSlot.Value, 0, add, add, NewRow: true));
            claimedSlots.Add(freeSlot.Value);
            remaining -= add;
        }

        var outcome = new FarmGrantPlan(
            character.Name,
            itemId,
            item.DisplayName,
            quantity,
            item.StackCap,
            quality,
            plan.Count(placement =>
                placement.NewRow &&
                placement.Slot >= character.BagPages * KitBagSlotsPerPage),
            dryRun,
            plan);

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
            return outcome;
        }

        foreach (var placement in plan)
        {
            if (placement.NewRow)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO character_items (
                        user_id, item_location, slot_index, prop_id,
                        item_quality, item_grade, bound, stack, item_exp, holy_suit_code)
                    VALUES (@user, @location, @slot, @item, @quality, 1, 0, @stack, 0, 0);
                    """,
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("user", characterId);
                insert.Parameters.AddWithValue("location", KitBagLocation);
                insert.Parameters.AddWithValue("slot", placement.Slot);
                insert.Parameters.AddWithValue("item", itemId);
                insert.Parameters.AddWithValue("quality", quality);
                insert.Parameters.AddWithValue("stack", checked((short)placement.After));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = new NpgsqlCommand(
                    """
                    UPDATE character_items
                    SET stack = @stack, updated_at = now()
                    WHERE user_id = @user AND item_location = @location
                      AND slot_index = @slot AND prop_id = @item;
                    """,
                    connection,
                    transaction);
                update.Parameters.AddWithValue("stack", checked((short)placement.After));
                update.Parameters.AddWithValue("user", characterId);
                update.Parameters.AddWithValue("location", KitBagLocation);
                update.Parameters.AddWithValue("slot", placement.Slot);
                update.Parameters.AddWithValue("item", itemId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await WriteAuditAsync(
                connection,
                transaction,
                characterId,
                placement,
                itemId,
                quality,
                note,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Removes an activity item from a character's kit bag, optionally only the
    /// stacks of one quality.
    /// </summary>
    public async Task<long> DeleteBagStacksAsync(
        int characterId,
        int itemId,
        short? quality,
        string? note,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var stacks = new List<(int Slot, short Quality, short Stack)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT slot_index, item_quality, stack
            FROM character_items
            WHERE user_id = @user
              AND item_location = @location
              AND prop_id = @item
              AND (@quality IS NULL OR item_quality = @quality)
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("user", characterId);
            command.Parameters.AddWithValue("location", KitBagLocation);
            command.Parameters.AddWithValue("item", itemId);
            command.Parameters.AddWithValue(
                "quality",
                quality is { } value ? value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stacks.Add((reader.GetInt16(0), reader.GetInt16(1), reader.GetInt16(2)));
            }
        }

        if (stacks.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        if (!dryRun)
        {
            foreach (var (slot, stackQuality, stack) in stacks)
            {
                await using (var delete = new NpgsqlCommand(
                    """
                    DELETE FROM character_items
                    WHERE user_id = @user AND item_location = @location
                      AND slot_index = @slot AND prop_id = @item
                      AND item_quality = @quality;
                    """,
                    connection,
                    transaction))
                {
                    delete.Parameters.AddWithValue("user", characterId);
                    delete.Parameters.AddWithValue("location", KitBagLocation);
                    delete.Parameters.AddWithValue("slot", slot);
                    delete.Parameters.AddWithValue("item", itemId);
                    delete.Parameters.AddWithValue("quality", stackQuality);
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var audit = new NpgsqlCommand(
                    """
                    INSERT INTO character_item_audit (
                        source, action, user_id, item_location, slot_index,
                        prop_id, item_quality, item_grade, item_exp, old_item)
                    VALUES ('gm-tool', 'gm-delete-stack', @user, @location, @slot,
                            @item, @quality, 1, 0,
                            jsonb_build_object('note', @note, 'deletedStack', @stack));
                    """,
                    connection,
                    transaction);
                audit.Parameters.AddWithValue("user", characterId);
                audit.Parameters.AddWithValue("location", KitBagLocation);
                audit.Parameters.AddWithValue("slot", slot);
                audit.Parameters.AddWithValue("item", itemId);
                audit.Parameters.AddWithValue("quality", stackQuality);
                audit.Parameters.AddWithValue("stack", stack);
                audit.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(note) ? DBNull.Value : note
                });
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return stacks.Count;
    }

    /// <summary>
    /// The activity's rules as the server's own source states them, read from the
    /// repository so the display can never drift from the code.
    /// </summary>
    public static IReadOnlyList<FarmRuleConstant> LoadRuleConstants()
    {
        var root = LootToolSettings.FindRepositoryRoot();
        var constants = new List<FarmRuleConstant>();
        foreach (var rule in LelantineFarmRules.Rules)
        {
            var value = ReadConstant(root, rule.SourceFile, rule.ConstantName);
            constants.Add(new FarmRuleConstant(
                rule.ConstantName,
                value ?? "（未找到源码，内置值：" + rule.Fallback + "）",
                rule.Meaning,
                value is null
                    ? rule.SourceFile
                    : $"{rule.SourceFile} → {value}"));
        }

        return constants;
    }

    private static string? ReadConstant(
        string? repositoryRoot,
        string relativePath,
        string constantName)
    {
        if (repositoryRoot is null)
        {
            return null;
        }

        var path = Path.Combine(
            repositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return null;
        }

        var pattern =
            @"^\s*public\s+(?:const|static\s+readonly)\s+\w+\s+" +
            Regex.Escape(constantName) +
            @"\s*=\s*([^;]+);";
        var match = Regex.Match(File.ReadAllText(path), pattern, RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static async Task LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT true FROM character_base WHERE id = @id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new FarmToolException($"角色不存在：id={characterId}");
        }
    }

    private static async Task<CharacterRow> ReadCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT cb.name, cb.bag_num
            FROM character_base cb
            WHERE cb.id = @id
            FOR UPDATE OF cb;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new FarmToolException($"角色不存在：id={characterId}");
        }

        return new CharacterRow(reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<ItemRow> ReadItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT display_name,
                   COALESCE(NULLIF(stats ->> 'Overlap', '')::integer, 1) AS stack_cap
            FROM item_templates
            WHERE id = @id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new FarmToolException(
                $"物品 {itemId} 不在 item_templates 中（外键目标缺失），无法发放。" +
                "如果这是新补齐的物品，请先让服务器完成一次内容发布。");
        }

        return new ItemRow(reader.GetString(0), Math.Max(1, reader.GetInt32(1)));
    }

    private static async Task<List<(int Slot, int Stack)>> ReadPartialStacksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        int stackCap,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT slot_index, stack
            FROM character_items
            WHERE user_id = @user AND item_location = @location
              AND prop_id = @item AND stack < @cap
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user", characterId);
        command.Parameters.AddWithValue("location", KitBagLocation);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("cap", stackCap);

        var candidates = new List<(int Slot, int Stack)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add((reader.GetInt16(0), reader.GetInt16(1)));
        }

        return candidates;
    }

    private static async Task<int?> ReadFreeKitBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        HashSet<int> claimedSlots,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT candidate.slot_index
            FROM generate_series(0, @lastSlot) AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM character_items item
                WHERE item.user_id = @user
                  AND item.item_location = @location
                  AND item.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("lastSlot", KitBagSlots - 1);
        command.Parameters.AddWithValue("user", characterId);
        command.Parameters.AddWithValue("location", KitBagLocation);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(0);
            if (claimedSlots.Add(slot))
            {
                return slot;
            }
        }

        return null;
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        FarmPlacement placement,
        int itemId,
        short quality,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand(
            """
            INSERT INTO character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item)
            VALUES ('gm-tool', @action, @user, @location, @slot,
                    @item, @quality, 1, 0,
                    jsonb_build_object(
                        'note', @note,
                        'previousStack', @previousStack,
                        'grantedStack', @grantedStack));
            """,
            connection,
            transaction);
        audit.Parameters.AddWithValue(
            "action",
            placement.NewRow ? "gm-grant-insert" : "gm-grant-stack");
        audit.Parameters.AddWithValue("user", characterId);
        audit.Parameters.AddWithValue("location", KitBagLocation);
        audit.Parameters.AddWithValue("slot", placement.Slot);
        audit.Parameters.AddWithValue("item", itemId);
        audit.Parameters.AddWithValue("quality", quality);
        audit.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(note) ? DBNull.Value : note
        });
        audit.Parameters.Add(new NpgsqlParameter("previousStack", NpgsqlDbType.Smallint)
        {
            Value = placement.NewRow ? DBNull.Value : (short)placement.Before
        });
        audit.Parameters.Add(new NpgsqlParameter("grantedStack", NpgsqlDbType.Smallint)
        {
            Value = checked((short)placement.After)
        });
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CharacterRow(string Name, int BagPages);

    private sealed record ItemRow(string DisplayName, int StackCap);
}

/// <summary>A farm GM operation the operator has to fix before it can run.</summary>
internal sealed class FarmToolException : Exception
{
    public FarmToolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The activity's own numbers, as the tool needs them: the map, the two camps,
/// the items it deals in, and where each rule lives in the server source.
/// </summary>
/// <remarks>
/// The values here mirror the server's constants and exist so the panel can show
/// them; <see cref="FarmStore.LoadRuleConstants"/> reads the authoritative values
/// back out of the source files, and the panel shows both, so a rule that changes
/// in the server is visible instead of silently stale.
/// </remarks>
internal static class LelantineFarmRules
{
    public const short MapId = 42;
    public const short SpartaCamp = 0;
    public const short AthensCamp = 1;

    /// <summary>忠犬卵 - the egg the captain's donation page takes.</summary>
    public const int HoundEggItemId = 10159;

    /// <summary>The tuck nets the quartermaster sells and the activity hands out.</summary>
    public static readonly int[] NetItemIds = [10080, 10081, 10082, 10083, 10084];

    /// <summary>Every item the farm tab shows in a character's bag.</summary>
    public static readonly int[] FarmItemIds = [10159, 10080, 10081, 10082, 10083, 10084];

    /// <summary>The three egg aptitudes the donation scores, and their points.</summary>
    public static readonly (short Aptitude, string Name, int Points)[] EggRungs =
    [
        (1, "懦弱的 (Weak)", 1),
        (5, "理智的 (Rational)", 10),
        (8, "热情的 (Zealous)", 100)
    ];

    public static string AptitudeName(short aptitude) =>
        EggRungs.FirstOrDefault(rung => rung.Aptitude == aptitude) is
            { Name: { Length: > 0 } name }
            ? name
            : $"{aptitude}（活动不计分）";

    /// <summary>One rule constant and where the server keeps it.</summary>
    internal sealed record Rule(
        string ConstantName,
        string Fallback,
        string Meaning,
        string SourceFile);

    private const string ProtocolFile =
        "src/Godswar.Server/Domain/World/Content/LelantineFarmProtocol.cs";

    private const string PolicyFile =
        "src/Godswar.Server/Application/WorldInstances/LelantineFarmPointsPolicy.cs";

    internal static readonly Rule[] Rules =
    [
        new("MapId", "42", "农场地图 ID（Lelantine_Farm，客户端场景 226）", ProtocolFile),
        new("FarmDialogIndex", "47", "活动窗口的对话号（NpcFunFarm.lua）", ProtocolFile),
        new("DonationBroadcastNoteType", "33",
            "捐卵广播的消息类型（客户端 SrvMsg_NOTE_181）", ProtocolFile),
        new("DonationBroadcastChannel", "0",
            "捐卵广播的频道（0 = 屏幕中间）", ProtocolFile),
        new("DonationBroadcastMinimumPoints", "100",
            "单次捐卵超过这个分数才广播（用户指定，抓包无实例）", ProtocolFile),
        new("MaximumDonationQuantity", "99",
            "单次捐卵数量上限（客户端脚本 1~99）", PolicyFile),
        new("NormalKillPoints", "1", "普通怪击杀积分", PolicyFile),
        new("EliteKillPoints", "10", "精英怪击杀积分", PolicyFile),
        new("BossKillPoints", "1000", "地狱犬 BOSS 击杀积分", PolicyFile),
        new("NormalMonsterMaximumLowerLevelGap", "10",
            "普通怪允许低于自身多少级仍计分", PolicyFile),
        new("WeakEggAptitude", "1", "懦弱的卵资质（1 分）", PolicyFile),
        new("RationalEggAptitude", "5", "理智的卵资质（10 分）", PolicyFile),
        new("ZealousEggAptitude", "8", "热情的卵资质（100 分）", PolicyFile),
        new("WeakEggPoints", "1", "懦弱的卵每个分值", PolicyFile),
        new("RationalEggPoints", "10", "理智的卵每个分值", PolicyFile),
        new("ZealousEggPoints", "100", "热情的卵每个分值", PolicyFile)
    ];

    /// <summary>The header a value must start with to be treated as a point count.</summary>
    public static bool TryParsePoints(string text, out int points) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out points);
}
