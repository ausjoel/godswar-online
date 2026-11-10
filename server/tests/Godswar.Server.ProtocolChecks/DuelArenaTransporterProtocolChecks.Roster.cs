using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterProtocolChecks
{
    private static void CheckV5PublishedRosterAndGeometry()
    {
        var arena = NpcContentBaselineV6.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId)
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            arena.Select(static npc => npc.NpcKey).SequenceEqual(new[]
            {
                "Arena_001",
                "Arena_002",
                "Arena_003",
                "Arena_004",
                "Arena_005"
            }),
            "Arena V6 publishes the complete five-actor native roster");
        Check.True(
            arena.Select(static npc => npc.TemplateKey).SequenceEqual(new[]
            {
                DuelArenaNpcRoster.VendorTemplateKey,
                "Arena_002_Male18",
                "Arena_003_Male18",
                DuelArenaNpcRoster.WardTemplateKey,
                DuelArenaNpcRoster.PhysicianTemplateKey
            }),
            "Arena V6 uses all five exact stock appearance templates");

        var supportActors = arena
            .Where(static npc => !DuelArenaTransporterProtocol.IsEndpoint(
                npc.NpcKey,
                npc.InteractionId))
            .ToArray();
        Check.Equal(3, supportActors.Length, "Arena V6 support actor count");
        Check.True(
            supportActors.All(static npc =>
                npc.ObjectId == npc.InteractionId),
            "Arena V6 support actor identities remain stable");
        Check.True(
            PairwiseDistances(supportActors).All(static distance =>
                distance >=
                    DuelArenaLobbyLayoutV6.MinimumUpperActorSeparation &&
                distance <=
                    DuelArenaLobbyLayoutV6.MaximumUpperActorSeparation),
            "Arena V6 support actors form a visible, non-overlapping group");
    }

    private static IEnumerable<float> PairwiseDistances(
        IReadOnlyList<NpcSpawnDefinition> actors)
    {
        for (var first = 0; first < actors.Count; first++)
        {
            for (var second = first + 1; second < actors.Count; second++)
            {
                var deltaX = actors[first].X - actors[second].X;
                var deltaZ = actors[first].Z - actors[second].Z;
                yield return MathF.Sqrt(
                    (deltaX * deltaX) + (deltaZ * deltaZ));
            }
        }
    }
}
