using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the nine Sparta city actors the September 28 2026 capture placed
/// but no earlier release carried.
/// </summary>
/// <remarks>
/// <para>
/// The capture session <c>c202c633-d9ea-4ea3-8c6b-fdb69e32cc53</c> (local
/// 01:52-02:09) sent opcode-10020 world objects for these nine keys on map 0,
/// while <c>npc_spawn_definitions</c> held no row for any of them. Because a
/// captured map is authoritative - <c>CapturedNpcPlacementPolicy</c> drops every
/// published row the capture does not have - the missing direction is the one
/// that matters here: the reference places them and this server published
/// nothing, so the client never saw them at all.
/// </para>
/// <para>
/// Every field is the capture's own: the object id, appearance word, position
/// and facing come from the 10020 frame the reference sent, and the ids were
/// verified free on both axes against the V1-V8 set before being written here.
/// The keys are the only NPCs in that session with no published row; no other
/// map is touched, and no row an earlier release carries is modified.
/// </para>
/// <para>
/// The reviewed V8 release and every earlier one stay immutable, so these actors
/// only ever appear through this release.
/// </para>
/// </remarks>
internal static class NpcContentBaselineV9
{
    public const short SpartaMapId = 0;
    public const string SpartaSceneKey = "Sparta";

    /// <summary>Appearance word the capture sent for all nine.</summary>
    public const uint AppearanceType = 0x11u;

    public const int AddedEntryCount = 9;
    public const int ExpectedEntryCount =
        NpcContentBaselineV8.ExpectedEntryCount + AddedEntryCount;
    public const string Source = "reviewed-published-npc-baseline-v9";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical definition set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "D06157C926B8473C84A8889A4DD7BB985AAE08D84124FD0EE0653939CE53E273";

    /// <summary>
    /// One captured Sparta actor: the key, the appearance template and the
    /// placement the reference's own 10020 frame carried.
    /// </summary>
    internal readonly record struct CapturedSpartaNpc(
        string NpcKey,
        string TemplateKey,
        uint ObjectId,
        float X,
        float Z,
        float Facing);

    /// <summary>
    /// The nine actors, in the order the capture first sent them.
    /// </summary>
    public static readonly CapturedSpartaNpc[] CapturedNpcs =
    [
        new("Sparta_012", "Sparta_012_MaleMerchant3", 5_011u, 64.00f, -161.00f, 3.00f),
        new("Sparta_024", "Sparta_024_Male7", 5_022u, 53.00f, -94.00f, 1.70f),
        new("Sparta_025", "Sparta_025_Sicily3", 5_023u, 64.00f, -182.00f, 0.00f),
        new("Sparta_063", "Sparta_063_FemMale18", 5_060u, 53.00f, -90.00f, 1.70f),
        new("Sparta_065", "Sparta_065_Belle1", 5_062u, 99.00f, -141.00f, 3.00f),
        new("Sparta_066", "Sparta_066_Belle1", 5_063u, 100.50f, -139.00f, 1.70f),
        new("Sparta_067", "Sparta_067_Belle1", 5_064u, 101.00f, -133.00f, 1.70f),
        new("Sparta_117", "Sparta_117_Male32", 5_114u, 64.00f, -152.00f, 3.00f),
        new("Sparta_126", "Sparta_126_FemVillager3", 5_123u, 64.00f, -156.00f, 3.00f)
    ];

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var previous = NpcContentBaselineV8.LoadDefinitions();
        var existing = previous
            .Select(static definition => definition.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        if (CapturedNpcs.Any(npc => existing.Contains(npc.NpcKey)))
        {
            throw new InvalidDataException(
                "The V9 Sparta release requires nine npc keys no earlier " +
                "release publishes.");
        }

        var definitions = previous
            .Concat(CapturedNpcs.Select(Create))
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        if (definitions.Length != ExpectedEntryCount ||
            definitions.Select(static definition =>
                    (definition.MapId, definition.ObjectId))
                .Distinct().Count() != definitions.Length ||
            definitions.Select(static definition =>
                    (definition.MapId, definition.InteractionId))
                .Distinct().Count() != definitions.Length)
        {
            throw new InvalidDataException(
                "The V9 Sparta release requires " +
                $"{ExpectedEntryCount} unique map-scoped actors with " +
                $"{AddedEntryCount} Sparta additions.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition Create(CapturedSpartaNpc npc) =>
        new(
            SpartaMapId,
            SpartaSceneKey,
            npc.NpcKey,
            npc.TemplateKey,
            npc.ObjectId,
            npc.X,
            npc.Z,
            // The client echoes the object id back as the interaction id, which
            // is what every captured shop and dialogue binding is keyed by.
            npc.ObjectId,
            AppearanceType,
            npc.Facing,
            Detail10077: [],
            Detail10080: []);
}
