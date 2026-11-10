using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class NpcContentAuthorityChecks
{
    private static void CheckArenaV5Release()
    {
        var previous = NpcContentBaselineV4.LoadDefinitions();
        var definitions = NpcContentBaselineV5.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(
            NpcContentBaselineV5.ExpectedEntryCount,
            definitions.Length,
            "Arena V5 NPC entry count");
        Check.Equal(
            NpcContentBaselineV5.ExpectedRevision,
            revision.Sha256,
            "Arena V5 NPC golden revision");

        var previousByIdentity = previous.ToDictionary(Identity);
        var changed = 0;
        foreach (var definition in definitions)
        {
            Check.True(
                previousByIdentity.TryGetValue(
                    Identity(definition),
                    out var prior),
                $"Arena V5 retains V4 identity {definition.NpcKey}");
            var isSupportActor = definition.NpcKey is
                DuelArenaNpcRoster.VendorNpcKey or
                DuelArenaNpcRoster.WardNpcKey or
                DuelArenaNpcRoster.PhysicianNpcKey;
            if (!isSupportActor)
            {
                CheckDefinitionEqual(
                    prior!,
                    definition,
                    $"Arena V5 preserves {definition.NpcKey}");
                continue;
            }

            CheckDefinitionEqual(
                prior!,
                definition with
                {
                    X = prior!.X,
                    Z = prior.Z,
                    Facing = prior.Facing
                },
                $"Arena V5 changes only {definition.NpcKey} geometry");
            changed++;
        }
        Check.Equal(3, changed, "Arena V5 moved support actor count");

        var arena = definitions
            .Where(static definition =>
                definition.MapId == DuelArenaTransporterProtocol.MapId)
            .ToDictionary(
                static definition => definition.NpcKey,
                StringComparer.Ordinal);
        AssertV5Actor(
            arena[DuelArenaNpcRoster.VendorNpcKey],
            DuelArenaNpcRoster.LegacyVendorNpcId,
            DuelArenaLobbyLayoutV5.VendorSpawnX,
            DuelArenaLobbyLayoutV5.VendorSpawnZ,
            DuelArenaLobbyLayoutV5.VendorFacing);
        AssertV5Actor(
            arena[DuelArenaNpcRoster.WardNpcKey],
            DuelArenaNpcRoster.LegacyWardNpcId,
            DuelArenaLobbyLayoutV5.WardSpawnX,
            DuelArenaLobbyLayoutV5.WardSpawnZ,
            DuelArenaLobbyLayoutV5.WardFacing);
        AssertV5Actor(
            arena[DuelArenaNpcRoster.PhysicianNpcKey],
            DuelArenaNpcRoster.LegacyPhysicianNpcId,
            DuelArenaLobbyLayoutV5.PhysicianSpawnX,
            DuelArenaLobbyLayoutV5.PhysicianSpawnZ,
            DuelArenaLobbyLayoutV5.PhysicianFacing);
        CheckDefinitionEqual(
            previousByIdentity[Identity(
                arena[DuelArenaTransporterProtocol.DoorkeeperNpcKey])],
            arena[DuelArenaTransporterProtocol.DoorkeeperNpcKey],
            "Arena V5 leaves the lower Doorkeeper unchanged");
        CheckDefinitionEqual(
            previousByIdentity[Identity(
                arena[DuelArenaTransporterProtocol.GatekeeperNpcKey])],
            arena[DuelArenaTransporterProtocol.GatekeeperNpcKey],
            "Arena V5 leaves the upper Gatekeeper unchanged");

        var supportActors = arena.Values
            .Where(static actor => actor.NpcKey is
                DuelArenaNpcRoster.VendorNpcKey or
                DuelArenaNpcRoster.WardNpcKey or
                DuelArenaNpcRoster.PhysicianNpcKey)
            .ToArray();
        var separations = PairwiseV5Distances(supportActors).ToArray();
        Check.True(
            separations.All(distance =>
                distance >=
                    DuelArenaLobbyLayoutV5.MinimumSupportActorSeparation) &&
            separations.Max() <= 20f,
            "Arena V5 support actors form a compact, non-overlapping group");
        Check.True(
            supportActors.All(actor =>
                Distance(
                    actor.X,
                    actor.Z,
                    DuelArenaTransporterProtocol.UpperArrivalX,
                    DuelArenaTransporterProtocol.UpperArrivalZ) <=
                DuelArenaLobbyLayoutV5.MaximumDistanceFromUpperArrival),
            "Arena V5 support actors are within one camera-scale group of " +
            "the upper arrival");
        Check.True(
            supportActors.All(actor => IsVisibleFromUpperArrival(actor)),
            "Arena V5 support actors share the upper arrival AOI");
    }

    private static void CheckArenaV18DialogueRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV5.LoadDefinitions()
            .Select(static definition => definition.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => publishedNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var previous = NpcDialogueBaselineV17.ApplyTextOverrides(rawTexts);
        var texts = NpcDialogueBaselineV18.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV18.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.True(
            NpcDialogueBaselineV18.ExpectedSpawnRevision ==
                NpcContentBaselineV5.ExpectedRevision,
            "Arena V18 dialogue pins the V5 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV18.ExpectedTextCount,
            texts.Length,
            "Arena V18 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV18.ExpectedHashedEntryCount,
            revision.EntryCount,
            "Arena V18 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV18.ExpectedRevision,
            revision.Sha256,
            "Arena V18 dialogue golden revision");
        Check.True(
            revision.Sha256 != NpcDialogueBaselineV17.ExpectedRevision,
            "Arena V18 has a unique immutable release identity");

        var previousByKey = previous.ToDictionary(
            static text => text.NpcKey,
            StringComparer.Ordinal);
        var changed = texts.Where(text =>
                text != previousByKey[text.NpcKey])
            .ToArray();
        Check.True(
            changed.Length == 1 &&
            changed[0].NpcKey ==
                DuelArenaTransporterProtocol.GatekeeperNpcKey &&
            changed[0].Description ==
                NpcDialogueBaselineV18.GatekeeperDescription,
            "Arena V18 changes only the Gatekeeper wording needed for a " +
            "unique V5-pinned release");
        Check.True(
            routes.SequenceEqual(
                NpcDialogueBaselineV17.CreateRoutes()),
            "Arena V18 retains the complete V17 route surface");
    }

    private static void AssertV5Actor(
        NpcSpawnDefinition actor,
        uint npcId,
        float x,
        float z,
        float facing) =>
        Check.True(
            actor.ObjectId == npcId &&
            actor.InteractionId == npcId &&
            actor.X == x && actor.Z == z && actor.Facing == facing,
            $"Arena V5 actor {actor.NpcKey} has clustered geometry");

    private static IEnumerable<float> PairwiseV5Distances(
        IReadOnlyList<NpcSpawnDefinition> actors)
    {
        for (var first = 0; first < actors.Count; first++)
        {
            for (var second = first + 1;
                 second < actors.Count;
                 second++)
            {
                yield return Distance(
                    actors[first].X,
                    actors[first].Z,
                    actors[second].X,
                    actors[second].Z);
            }
        }
    }

    private static float Distance(
        float firstX,
        float firstZ,
        float secondX,
        float secondZ)
    {
        var deltaX = firstX - secondX;
        var deltaZ = firstZ - secondZ;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static bool IsVisibleFromUpperArrival(NpcSpawnDefinition actor) =>
        WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(
            DuelArenaTransporterProtocol.UpperArrivalX,
            DuelArenaTransporterProtocol.UpperArrivalZ,
            out var arrivalCell) &&
        WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(
            actor.X,
            actor.Z,
            out var actorCell) &&
        WorldSectorVisibilityTracker<NpcSpawnDefinition>.IsNeighbor(
            arrivalCell,
            actorCell);
}
