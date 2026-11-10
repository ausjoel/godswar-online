using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class NpcContentAuthorityChecks
{
    private static readonly uint[] NativeArenaNpcIds =
    [
        DuelArenaNpcRoster.VendorNpcId,
        DuelArenaTransporterProtocol.DoorkeeperNpcId,
        DuelArenaTransporterProtocol.GatekeeperNpcId,
        DuelArenaNpcRoster.WardNpcId,
        DuelArenaNpcRoster.PhysicianNpcId
    ];

    private static readonly uint[] LegacyArenaNpcIds =
    [
        DuelArenaNpcRoster.LegacyVendorNpcId,
        DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
        DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
        DuelArenaNpcRoster.LegacyWardNpcId,
        DuelArenaNpcRoster.LegacyPhysicianNpcId
    ];

    private static void CheckArenaV6Release()
    {
        var previous = NpcContentBaselineV5.LoadDefinitions();
        var definitions = NpcContentBaselineV6.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(
            NpcContentBaselineV6.ExpectedEntryCount,
            definitions.Length,
            "Arena V6 NPC entry count");
        Check.Equal(
            NpcContentBaselineV6.ExpectedRevision,
            revision.Sha256,
            "Arena V6 NPC golden revision");

        var previousByKey = previous.ToDictionary(SemanticIdentity);
        var currentByKey = definitions.ToDictionary(SemanticIdentity);
        Check.True(
            previousByKey.Keys.ToHashSet().SetEquals(currentByKey.Keys),
            "Arena V6 retains every V5 semantic NPC identity");

        var changed = 0;
        foreach (var (key, current) in currentByKey)
        {
            var prior = previousByKey[key];
            if (current.MapId != DuelArenaTransporterProtocol.MapId)
            {
                CheckDefinitionEqual(
                    prior,
                    current,
                    $"Arena V6 preserves non-Arena NPC {current.NpcKey}");
                continue;
            }

            var nativeId = NativeIdFor(current.NpcKey);
            var legacyId = LegacyIdFor(current.NpcKey);
            Check.True(
                prior.ObjectId == legacyId &&
                prior.InteractionId == legacyId &&
                current.ObjectId == nativeId &&
                current.InteractionId == nativeId,
                $"Arena V6 re-keys {current.NpcKey} from its sealed " +
                "legacy identity to its native-range identity");

            var expectedX = current.NpcKey == DuelArenaNpcRoster.VendorNpcKey
                ? DuelArenaLobbyLayoutV6.VendorSpawnX
                : prior.X;
            var expectedZ = current.NpcKey == DuelArenaNpcRoster.VendorNpcKey
                ? DuelArenaLobbyLayoutV6.VendorSpawnZ
                : prior.Z;
            CheckDefinitionEqual(
                prior,
                current with
                {
                    ObjectId = prior.ObjectId,
                    InteractionId = prior.InteractionId,
                    X = prior.X,
                    Z = prior.Z
                },
                $"Arena V6 changes only the approved identity/placement " +
                $"fields for {current.NpcKey}");
            Check.True(
                current.X == expectedX && current.Z == expectedZ,
                $"Arena V6 places {current.NpcKey} at its approved point");
            changed++;
        }

        Check.Equal(5, changed, "Arena V6 changed actor count");
        Check.True(
            NativeArenaNpcIds.SequenceEqual(
                Enumerable.Range(5_701, 5).Select(static id => (uint)id)) &&
            NativeArenaNpcIds.All(static id => id is >= 1_500 and < 8_000) &&
            NativeArenaNpcIds.All(id => definitions.Count(
                definition => definition.ObjectId == id) == 1) &&
            LegacyArenaNpcIds.All(id => definitions.All(
                definition => definition.ObjectId != id &&
                    definition.InteractionId != id)),
            "Arena V6 uses five unique native-range IDs and removes every " +
            "legacy five-digit ID");

        CheckArenaV6Visibility(definitions);
        CheckArenaV6TerrainEvidence();
    }

    private static void CheckArenaV6Visibility(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        var arena = definitions.Where(static definition =>
                definition.MapId == DuelArenaTransporterProtocol.MapId)
            .ToArray();
        var tracker = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            arena,
            static npc => npc.ObjectId,
            static npc => npc.X,
            static npc => npc.Z,
            "Arena V6 NPC");
        var expectedUpperIds = NativeArenaNpcIds
            .Where(static id =>
                id != DuelArenaTransporterProtocol.DoorkeeperNpcId)
            .Order()
            .ToArray();

        foreach (var (label, x, z) in new[]
                 {
                     ("upper arrival",
                         DuelArenaTransporterProtocol.UpperArrivalX,
                         DuelArenaTransporterProtocol.UpperArrivalZ),
                     ("observed checkpoint",
                         DuelArenaLobbyLayoutV6.ObservedCheckpointX,
                         DuelArenaLobbyLayoutV6.ObservedCheckpointZ)
                 })
        {
            Check.True(
                tracker.TryCalculate(x, z, out var delta) &&
                delta.Entering.Select(static npc => npc.ObjectId)
                    .SequenceEqual(expectedUpperIds),
                $"Arena V6 exposes all four upper actors from the {label}");
        }
    }

    private static void CheckArenaV6TerrainEvidence()
    {
        Check.True(
            DuelArenaLobbyLayoutV6.UpperActorTerrainEvidence.Count == 4 &&
            DuelArenaLobbyLayoutV6.UpperActorTerrainEvidence.All(evidence =>
                evidence.DecodedBlockValue ==
                    DuelArenaLobbyLayoutV6.UnblockedValue &&
                evidence.BlockedCellClearance >=
                    DuelArenaLobbyLayoutV6.MinimumTerrainClearance &&
                DuelArenaLobbyLayoutV6.TryProjectToHmpBlock(
                    evidence.X,
                    evidence.Z,
                    out var block) &&
                block == new DuelArenaHmpBlockCell(
                    evidence.BlockX,
                    evidence.BlockZ)),
            "Arena V6 upper actors pin unblocked, clearance-bounded HMP " +
            "terrain evidence");
    }

    private static void CheckArenaV19DialogueRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV6.LoadDefinitions()
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
        var texts = NpcDialogueBaselineV19.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV19.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.True(
            NpcDialogueBaselineV19.ExpectedSpawnRevision ==
                NpcContentBaselineV6.ExpectedRevision,
            "Arena V19 dialogue pins the V6 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV19.ExpectedHashedEntryCount,
            revision.EntryCount,
            "Arena V19 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV19.ExpectedRevision,
            revision.Sha256,
            "Arena V19 dialogue golden revision");
        Check.True(
            NpcDialogueBaselineV19.CreateRoutes().SequenceEqual(
                NpcDialogueBaselineV18.CreateRoutes()),
            "Arena V19 preserves the complete V18 route surface");
    }

    private static (
        short MapId,
        string NpcKey,
        string TemplateKey) SemanticIdentity(
            NpcSpawnDefinition definition) =>
        (definition.MapId, definition.NpcKey, definition.TemplateKey);

    private static uint NativeIdFor(string npcKey) => npcKey switch
    {
        DuelArenaNpcRoster.VendorNpcKey =>
            DuelArenaNpcRoster.VendorNpcId,
        DuelArenaTransporterProtocol.DoorkeeperNpcKey =>
            DuelArenaTransporterProtocol.DoorkeeperNpcId,
        DuelArenaTransporterProtocol.GatekeeperNpcKey =>
            DuelArenaTransporterProtocol.GatekeeperNpcId,
        DuelArenaNpcRoster.WardNpcKey => DuelArenaNpcRoster.WardNpcId,
        DuelArenaNpcRoster.PhysicianNpcKey =>
            DuelArenaNpcRoster.PhysicianNpcId,
        _ => throw new InvalidDataException(
            $"Unexpected Arena V6 NPC key {npcKey}.")
    };

    private static uint LegacyIdFor(string npcKey) => npcKey switch
    {
        DuelArenaNpcRoster.VendorNpcKey =>
            DuelArenaNpcRoster.LegacyVendorNpcId,
        DuelArenaTransporterProtocol.DoorkeeperNpcKey =>
            DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
        DuelArenaTransporterProtocol.GatekeeperNpcKey =>
            DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
        DuelArenaNpcRoster.WardNpcKey =>
            DuelArenaNpcRoster.LegacyWardNpcId,
        DuelArenaNpcRoster.PhysicianNpcKey =>
            DuelArenaNpcRoster.LegacyPhysicianNpcId,
        _ => throw new InvalidDataException(
            $"Unexpected Arena V5 NPC key {npcKey}.")
    };
}
