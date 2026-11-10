using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private static async Task<CapturedMonsterSpawn[]>
        LoadPublishedMonsterSpawnsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlySet<short> publishedMapIds,
            CancellationToken cancellationToken)
    {
        string revision;
        int expectedEntryCount;
        await using (var headerCommand = new NpgsqlCommand(
                         """
                         SELECT publication.revision,
                                release.entry_count
                         FROM monster_content_publication publication
                         JOIN monster_content_revisions release
                           ON release.revision = publication.revision
                         WHERE publication.family = 'monsters';
                         """,
                         connection,
                         transaction))
        await using (var header =
                     await headerCommand.ExecuteReaderAsync(
                         cancellationToken))
        {
            if (!await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "monsters",
                    WorldContentFailureReason.Missing,
                    "No official monster content revision is published.");
            }

            revision = header.GetString(0);
            expectedEntryCount = header.GetInt32(1);
            if (expectedEntryCount is < 0 or
                > MonsterContentLimits.MaximumDefinitions)
            {
                throw new WorldContentUnavailableException(
                    "monsters",
                    WorldContentFailureReason.Invalid,
                    "The official monster content entry count is outside " +
                    "the supported bounds.");
            }

            if (await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "monsters",
                    WorldContentFailureReason.Invalid,
                    "More than one official monster content revision is " +
                    "published.");
            }
        }

        var definitions = new List<CapturedMonsterSpawn>(
            expectedEntryCount);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT map_id,
                       scene_key,
                       template_key,
                       display_name,
                       object_id,
                       pos_x,
                       pos_z,
                       clear_bytes
                FROM monster_spawn_definitions
                WHERE revision = @revision;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mapId = reader.GetInt16(0);
                if (!publishedMapIds.Contains(mapId))
                {
                    throw new InvalidDataException(
                        $"Monster definition references unpublished map {mapId}.");
                }

                var definition = new CapturedMonsterSpawn(
                    mapId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    checked((uint)reader.GetInt64(4)),
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    (byte[])reader["clear_bytes"]);
                definition.Validate(mapId);
                definitions.Add(definition);
            }
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                OverflowException or
                InvalidCastException)
        {
            throw new WorldContentUnavailableException(
                "monsters",
                WorldContentFailureReason.Invalid,
                "The published monster content contains a malformed " +
                "definition.",
                ex);
        }

        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(static definition => definition.ObjectId)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ToArray();
        var computed = WorldContentRevisionHasher.HashMonsters(canonical);
        if (computed.EntryCount != expectedEntryCount ||
            !string.Equals(
                computed.Sha256,
                revision,
                StringComparison.Ordinal))
        {
            throw new WorldContentUnavailableException(
                "monsters",
                WorldContentFailureReason.RevisionMismatch,
                "The published monster content does not match its declared " +
                "revision and entry count.");
        }

        return canonical;
    }

    // ========================================================================
    // Authored Thermopylae (map 8) population.
    //
    // The reviewed monster baseline only ever captured map 0 (Sparta), so map 8
    // has no published spawns at all. Its population is therefore authored here
    // from the same region table the offline planner
    // ("Godswar Origin/generate_spawns.ps1") uses to render the approved
    // coordinate preview. This generator is a faithful port of that planner so
    // the preview, the exported CSV, and the running server never diverge.
    //
    // The rows are additive: the published revision above still describes
    // exactly the captured set it declares.
    // ========================================================================

    /// <summary>
    /// Reviewed size of the authored Thermopylae population: 410 ordinary
    /// monsters, one field elite, and the map's single world boss.
    /// </summary>
    internal const int ThermopylaeSpawnCount = 412;

    private const short ThermopylaeMapId = 8;
    private const string ThermopylaeSceneKey = "Thermopylae_All";
    private const uint FirstThermopylaeObjectId = 43_000;
    private const ushort WorldObjectAppearanceOpcode = 10020;
    private const int AppearancePacketLength = 108;

    /// <summary>
    /// The monster discriminator Origin's 10020 handler requires. The map id
    /// travels in the packed high word at packet bytes 6-7, and leaving it zero
    /// makes the object invisible to the client.
    /// </summary>
    private const uint MonsterAppearanceObjectType = 0x212;

    /// <summary>
    /// The appearance word the authored plans use. A published captured frame
    /// carries the reference's own word instead: 0x0012 for humanoids, 0x0212
    /// for beasts.
    /// </summary>
    private const ushort AuthoredMonsterAppearanceWord = 0x212;

    /// <summary>
    /// The 32-unit world cell both the server and the client use for
    /// visibility. No authored cell holds more than its region's per-cell cap,
    /// so a player standing in one cell can never be shown more than nine times
    /// the cap at once.
    /// </summary>
    private const double ThermopylaeCellSize = 32d;

    private const ulong XorshiftMask = 0xFFFF_FFFFUL;

    /// <summary>
    /// One marked spawn area on the Thermopylae map, in the map's X/Z range.
    /// A non-zero cell cap splits the rectangle into 32-unit cells and seeds
    /// that many monsters in each cell. A zero cap marks a single authored
    /// encounter placed at the rectangle's centre.
    /// </summary>
    private readonly record struct ThermopylaeSpawnRegion(
        string TemplateKey,
        uint Tier,
        uint MaximumHealth,
        double MinX,
        double MaxX,
        double MinZ,
        double MaxZ,
        int CellCap);

    /// <summary>
    /// The reviewed Thermopylae spawn areas, in the order their object IDs are
    /// issued. Every template is a published map-8 gameplay template; the
    /// generator fails loudly if the gameplay catalogue stops publishing one.
    /// </summary>
    private static readonly ThermopylaeSpawnRegion[] ThermopylaeSpawnRegions =
    [
        new("B_normalB_stymphalianbird_002", 113, 5_200, 192, 224, -96, -32, 5), // Rot Hide Cooer
        new("B_normalB_stymphalianbird_002", 113, 5_200, 160, 192, -96, 0, 5), // Rot Hide Cooer
        new("B_normalB_stymphalianbird_002", 113, 5_200, 128, 160, -96, 0, 5), // Rot Hide Cooer
        new("B_normalB_stymphalianbird_004", 113, 5_200, -160, 0, -96, -64, 5), // White Cooer
        new("B_normalB_wolf_006", 113, 5_200, -32, 32, -192, -128, 5), // Flaming Hound
        new("B_normalB_wolf_006", 113, 5_200, -64, 32, -128, -96, 5), // Flaming Hound
        new("B_normalC_satyr_006", 113, 5_200, 32, 96, -192, -96, 5), // Persian Clergy
        new("B_normalC_satyr_006", 113, 5_200, 96, 128, -128, -64, 5), // Persian Clergy
        new("B_eliteB_stymphalianbird_001", 123, 26_000, 0, 64, -96, -64, 0), // [Elite]White Cooer
        new("B_normalC_mage_016", 113, 5_200, 96, 128, -64, 64, 5), // Persian Mage
        new("B_normalB_mage_010", 113, 5_200, 64, 224, 128, 192, 5), // Persian Archmage
        new("B_normalB_mage_008", 113, 5_200, 0, 32, -64, 64, 5), // Persian Whiterobe Mage
        new("B_normalB_amazon_001", 113, 5_200, 32, 96, 32, 128, 5), // Persian Huntress
        new("B_normalB_robber_003", 113, 5_200, 128, 224, 64, 128, 5), // Persian Bandit
        new("B_normalB_robber_003", 113, 5_200, 96, 128, 64, 96, 5), // Persian Bandit
        new("B_normalB_robber_003", 113, 5_200, 96, 128, 96, 128, 5), // Persian Bandit
        new("B_normalC_persiawarrior_006", 113, 5_200, -32, 64, 128, 192, 5), // Persian Paladin
        new("B_normalB_persiawarrior_006", 113, 5_200, -64, 0, 0, 96, 5), // Persian Vanguard
        new("B_normalB_persiawarrior_005", 113, 5_200, -128, -64, 96, 160, 5), // Persian Bowyer
        new("B_normalB_persiawarrior_001", 113, 5_200, -160, -128, 128, 160, 5), // Persian Soldier
        new("B_normalB_persiawarrior_001", 113, 5_200, -192, -32, 160, 192, 5), // Persian Soldier
        new("B_bossB_xerxes_001", 130, 180_000, -160, -128, 0, 32, 0) // Mardonius (world boss)
    ];

    /// <summary>
    /// Seeds one cell with a symmetric figure that keeps clear of the cell
    /// border, so the authored spacing survives the per-cell ceiling.
    /// </summary>
    private static readonly (double X, double Z)[] ThermopylaeCellPattern =
    [
        (0.18d, 0.18d),
        (0.82d, 0.18d),
        (0.50d, 0.50d),
        (0.18d, 0.82d),
        (0.82d, 0.82d)
    ];

    internal static CapturedMonsterSpawn[] AppendAuthoredSpawns(
        IReadOnlyList<CapturedMonsterSpawn> captured,
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var spawns = new List<CapturedMonsterSpawn>(
            captured.Count +
            ThermopylaeSpawnCount +
            SpartaNewbieSpawnCount);
        spawns.AddRange(captured);
        spawns.AddRange(CreateAuthoredThermopylaeSpawns(gameplay));
        spawns.AddRange(CreateAuthoredSpartaNewbieSpawns(gameplay));
        return [.. spawns];
    }

    /// <summary>
    /// Expands the reviewed regions into the exact spawns the offline planner
    /// previewed. Positions are deterministic: the same regions always produce
    /// the same coordinates, which is what makes the approved preview and the
    /// running server the same plan.
    /// </summary>
    internal static CapturedMonsterSpawn[] CreateAuthoredThermopylaeSpawns(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        var templates = ResolveAuthoredTemplates(gameplay, ThermopylaeMapId);
        var spawns = new List<CapturedMonsterSpawn>(ThermopylaeSpawnCount);
        var objectId = FirstThermopylaeObjectId;
        foreach (var region in ThermopylaeSpawnRegions)
        {
            if (region.CellCap > ThermopylaeCellPattern.Length)
            {
                throw AuthoredContentInvalid(
                    $"Thermopylae region '{region.TemplateKey}' requests " +
                    $"{region.CellCap} monsters per cell, but the authored " +
                    $"pattern holds only {ThermopylaeCellPattern.Length}.");
            }

            var template = RequireAuthoredTemplate(
                templates,
                ThermopylaeMapId,
                region.TemplateKey);
            foreach (var candidate in EnumerateThermopylaeCandidates(region))
            {
                objectId++;
                // A single authored encounter keeps its exact point. A capped
                // cell is only nudged, because a larger offset would push the
                // monster into a neighbouring cell and break the ceiling.
                var jitterScale = region.CellCap == 0
                    ? 0d
                    : (ThermopylaeCellSize / 3d) * 0.10d;
                var x = ThermopylaeRound(Math.Clamp(
                    candidate.X +
                        Jitter((ulong)objectId * 2654435761UL, jitterScale),
                    region.MinX,
                    region.MaxX));
                var z = ThermopylaeRound(Math.Clamp(
                    candidate.Z +
                        Jitter(
                            ((ulong)objectId * 40503UL) + 12345UL,
                            jitterScale),
                    region.MinZ,
                    region.MaxZ));
                var spawn = CreateAuthoredSpawn(
                    ThermopylaeMapId,
                    ThermopylaeSceneKey,
                    objectId,
                    template,
                    region.Tier,
                    region.MaximumHealth,
                    x,
                    z);
                spawn.Validate(ThermopylaeMapId);
                spawns.Add(spawn);
            }
        }

        if (spawns.Count != ThermopylaeSpawnCount)
        {
            throw AuthoredContentInvalid(
                $"The authored Thermopylae regions produced {spawns.Count} " +
                $"monsters instead of the reviewed {ThermopylaeSpawnCount}.");
        }

        return [.. spawns];
    }

    private static IEnumerable<(double X, double Z)>
        EnumerateThermopylaeCandidates(ThermopylaeSpawnRegion region)
    {
        if (region.CellCap == 0)
        {
            yield return (
                (region.MinX + region.MaxX) / 2d,
                (region.MinZ + region.MaxZ) / 2d);
            yield break;
        }

        var columns = Math.Max(
            1,
            (int)Math.Round(
                (region.MaxX - region.MinX) / ThermopylaeCellSize));
        var rows = Math.Max(
            1,
            (int)Math.Round(
                (region.MaxZ - region.MinZ) / ThermopylaeCellSize));
        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var index = 0; index < region.CellCap; index++)
                {
                    var (offsetX, offsetZ) = ThermopylaeCellPattern[index];
                    yield return (
                        region.MinX +
                            ((column + offsetX) * ThermopylaeCellSize),
                        region.MinZ +
                            ((row + offsetZ) * ThermopylaeCellSize));
                }
            }
        }
    }

    /// <summary>
    /// The offline planner's deterministic xorshift32 jitter. Reusing its exact
    /// multipliers and rounding keeps regenerated previews reproducible.
    /// </summary>
    private static double Jitter(ulong seed, double scale)
    {
        var value = seed & XorshiftMask;
        if (value == 0)
        {
            value = 0x6D2B79F5UL;
        }

        value = (value ^ ((value << 13) & XorshiftMask)) & XorshiftMask;
        value = (value ^ (value >> 17)) & XorshiftMask;
        value = (value ^ ((value << 5) & XorshiftMask)) & XorshiftMask;
        return (((double)value / 4294967296d) - 0.5d) * 2d * scale;
    }

    private static double ThermopylaeRound(double value) =>
        Math.Round(value, 2);

    /// <summary>
    /// Builds the 108-byte opcode-10020 appearance packet every authored spawn
    /// shares. The map id travels in the packed high word at bytes 6-7, and
    /// leaving it zero makes the object invisible to the client.
    /// </summary>
    private static CapturedMonsterSpawn CreateAuthoredSpawn(
        short mapId,
        string sceneKey,
        uint objectId,
        GameplayMonsterTemplateDefinition template,
        uint tier,
        uint maximumHealth,
        double x,
        double z,
        ushort appearance = AuthoredMonsterAppearanceWord,
        float facing = 1f)
    {
        var packet = new byte[AppearancePacketLength];
        var templateBytes = Encoding.ASCII.GetBytes(template.TemplateKey);
        if (templateBytes.Length >= packet.Length - 44)
        {
            throw AuthoredContentInvalid(
                $"Gameplay template '{template.TemplateKey}' does not fit " +
                "its appearance packet.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            WorldObjectAppearanceOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            appearance | ((uint)mapId << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tier);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            maximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), (float)x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), (float)z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), facing);
        templateBytes.CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            mapId,
            sceneKey,
            template.TemplateKey,
            template.DisplayName,
            objectId,
            (float)x,
            (float)z,
            packet);
    }

    /// <summary>
    /// Indexes the published gameplay templates of one map so an authored row
    /// can resolve its display name and fail loudly when the publication stops
    /// carrying the template.
    /// </summary>
    private static Dictionary<string, GameplayMonsterTemplateDefinition>
        ResolveAuthoredTemplates(
            GameplayContentCatalog gameplay,
            short mapId)
    {
        var templates =
            new Dictionary<string, GameplayMonsterTemplateDefinition>(
                StringComparer.Ordinal);
        foreach (var template in gameplay.MonsterTemplates)
        {
            if (template.SourceMapId == mapId)
            {
                templates[template.TemplateKey] = template;
            }
        }

        return templates;
    }

    private static GameplayMonsterTemplateDefinition RequireAuthoredTemplate(
        IReadOnlyDictionary<string, GameplayMonsterTemplateDefinition> templates,
        short mapId,
        string templateKey) =>
        templates.TryGetValue(templateKey, out var template)
            ? template
            : throw AuthoredContentInvalid(
                "An authored spawn references gameplay template " +
                $"'{templateKey}', which map {mapId} does not publish.");

    private static WorldContentUnavailableException AuthoredContentInvalid(
        string message) =>
        new("monsters", WorldContentFailureReason.Invalid, message);

    // ========================================================================
    // Authored Sparta outskirts (map 4, Sparta_Newbie) population.
    //
    // The reviewed baseline only ever captured map 0 (Sparta city), so the
    // outskirts carry no published spawns either. This population is generated
    // from the quest chain that lives out there and compiled in rather than
    // captured: every kill objective contributes its own Quest.xml coordinate
    // as a spawn centre, the range of a centre is half the distance to the
    // nearest other centre (12..30), and each centre holds the population map 0
    // keeps around a quest point for the same kill requirement. The table is
    // generated by tools/gen_suburb_monsters.py.
    // ========================================================================

    internal const int SpartaNewbieSpawnCount = SpartaNewbieSpawnPlan.SpawnCount;

    /// <summary>
    /// The first monster object id of Sparta's authored newbie population. Kept
    /// clear of the published NPC object IDs of map 4 (35117, 38410, 43272,
    /// 46565, 49858, 64595) and inside the stock client's 8000..49999 monster
    /// registry. Athens needs no such base: it replays the reference's own
    /// captured object ids, which travel in the published monster revision.
    /// </summary>
    private const uint FirstSpartaNewbieObjectId = 44_000;

    /// <summary>
    /// Expands the generated Sparta outskirts plan into spawns. The plan deals
    /// each quest's species evenly inside that quest point's own range, so no
    /// species clusters on the objective and none of them lands outside the
    /// ground the quest sends the player to.
    /// </summary>
    internal static CapturedMonsterSpawn[] CreateAuthoredSpartaNewbieSpawns(
        GameplayContentCatalog gameplay) =>
        CreateAuthoredPlanSpawns(
            gameplay,
            SpartaNewbieSpawnPlan.MapId,
            SpartaNewbieSpawnPlan.SceneKey,
            FirstSpartaNewbieObjectId,
            SpartaNewbieSpawnCount,
            [.. SpartaNewbieSpawnPlan.Spawns.Select(static row => (
                row.TemplateKey,
                row.Tier,
                row.MaximumHealth,
                row.X,
                row.Z))]);

    private static CapturedMonsterSpawn[] CreateAuthoredPlanSpawns(
        GameplayContentCatalog gameplay,
        short mapId,
        string sceneKey,
        uint firstObjectId,
        int expectedCount,
        (string TemplateKey, uint Tier, uint MaximumHealth, float X, float Z)[]
            plan)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        var templates = ResolveAuthoredTemplates(gameplay, mapId);
        var spawns = new List<CapturedMonsterSpawn>(plan.Length);
        var objectId = firstObjectId;
        foreach (var row in plan)
        {
            var template = RequireAuthoredTemplate(
                templates,
                mapId,
                row.TemplateKey);
            objectId++;
            var spawn = CreateAuthoredSpawn(
                mapId,
                sceneKey,
                objectId,
                template,
                row.Tier,
                row.MaximumHealth,
                row.X,
                row.Z);
            spawn.Validate(mapId);
            spawns.Add(spawn);
        }

        if (spawns.Count != expectedCount)
        {
            throw AuthoredContentInvalid(
                $"The map {mapId} plan produced {spawns.Count} monsters " +
                $"instead of the reviewed {expectedCount}.");
        }

        return [.. spawns];
    }
}
