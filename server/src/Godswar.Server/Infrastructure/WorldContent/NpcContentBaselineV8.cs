using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Places the Lelantine Farm roster on map 42.
/// </summary>
/// <remarks>
/// <para>
/// The farm is the one activity map whose actors the published V7 release never
/// carried: no map-42 row exists in <c>npc_spawn_definitions</c>, no captured
/// page has a <c>Lelantine_Farm_*</c> world object, and the September 26 2026
/// reference capture only ever answered the farm dialogue for ids that were
/// never placed on the map the client stood on. The npc keys, template keys and
/// faction split are published data (<c>007_npcs.sql</c> rows 542-549 and the
/// appearance rows 1681-1688); the ids and coordinates are this server's
/// authored placement, declared with their provenance in
/// <see cref="LelantineFarmProtocol.Npcs"/>.
/// </para>
/// <para>
/// The reviewed V7 release and every earlier one stay immutable, so the farm
/// only ever appears through this release.
/// </para>
/// </remarks>
internal static class NpcContentBaselineV8
{
    public const int AddedEntryCount = 8;
    public const int ExpectedEntryCount =
        NpcContentBaselineV7.ExpectedEntryCount + AddedEntryCount;
    public const string Source = "reviewed-published-npc-baseline-v8";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical definition set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "C088E365A7D41B41663986237000FEC636CD31C34938D71CFD8A57F9D5BDA93C";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var previous = NpcContentBaselineV7.LoadDefinitions();
        if (previous.Any(static definition =>
                definition.MapId == LelantineFarmProtocol.MapId))
        {
            throw new InvalidDataException(
                "The V8 farm release requires a V7 set with no farm map " +
                "rows.");
        }

        var definitions = previous
            .Concat(CreateFarmDefinitions())
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
                "The V8 farm release requires " +
                $"{ExpectedEntryCount} unique map-scoped actors with " +
                $"{AddedEntryCount} farm additions.");
        }

        return definitions;
    }

    private static IEnumerable<NpcSpawnDefinition> CreateFarmDefinitions() =>
        LelantineFarmProtocol.Npcs.Select(static npc => new NpcSpawnDefinition(
            LelantineFarmProtocol.MapId,
            LelantineFarmProtocol.SceneKey,
            npc.NpcKey,
            npc.TemplateKey,
            npc.ObjectId,
            npc.X,
            npc.Z,
            npc.ObjectId,
            LelantineFarmProtocol.AppearanceType,
            npc.Facing,
            Detail10077: [],
            Detail10080: []));
}
