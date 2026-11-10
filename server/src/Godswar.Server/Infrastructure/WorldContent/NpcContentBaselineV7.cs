using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the seven Arena actors and placements from the September 7
/// external capture. The reviewed V1-V6 releases remain immutable.
/// </summary>
internal static class NpcContentBaselineV7
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV6.ExpectedEntryCount + 2;
    public const string ExpectedRevision =
        "AAA9812754984363A92891077212C30EE5D44EC321C746083EBBF84D3F7063B6";
    public const string Source = "reviewed-published-npc-baseline-v7";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var previous = NpcContentBaselineV6.LoadDefinitions();
        var previousArena = previous.Where(static definition =>
            definition.MapId == DuelArenaCapturedLayout.MapId).ToArray();
        var capturedByKey = DuelArenaCapturedLayout.Npcs.ToDictionary(
            static npc => npc.NpcKey,
            StringComparer.Ordinal);
        if (previousArena.Length != 5 ||
            previousArena.Any(definition =>
                !capturedByKey.TryGetValue(definition.NpcKey, out var npc) ||
                definition.TemplateKey != npc.TemplateKey))
        {
            throw new InvalidDataException(
                "The V7 Arena release requires the five reviewed V6 actors.");
        }

        var definitions = previous
            .Where(static definition =>
                definition.MapId != DuelArenaCapturedLayout.MapId)
            .Concat(DuelArenaCapturedLayout.Npcs.Select(Create))
            .OrderBy(static definition => definition.MapId)
            .ThenBy(static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.TemplateKey,
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
                "The V7 Arena release requires seven unique map-scoped actors.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition Create(DuelArenaCapturedNpc npc) =>
        new(
            DuelArenaCapturedLayout.MapId,
            "Arena",
            npc.NpcKey,
            npc.TemplateKey,
            npc.ObjectId,
            npc.X,
            npc.Z,
            npc.ObjectId,
            DuelArenaCapturedLayout.AppearanceType,
            DuelArenaCapturedLayout.Facing,
            Detail10077: [],
            Detail10080: []);
}
