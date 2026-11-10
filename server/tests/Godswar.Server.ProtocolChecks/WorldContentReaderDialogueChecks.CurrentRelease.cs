using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckReviewedCurrentRelease()
    {
        CheckArenaV19CurrentRelease();
        CheckArenaV20CurrentRelease();
        CheckArenaV21CurrentRelease();
        CheckAtlantisV22CurrentRelease();
        CheckAtlantisV23CurrentRelease();

        var frozenNpcKeys = NpcContentBaselineV1.LoadDefinitions()
            .Select(static npc => npc.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var frozenRawTexts = NpcTemplateSeeds.Texts
            .Where(text => frozenNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var frozenV11 = WorldContentRevisionHasher.HashNpcDialogues(
            NpcDialogueBaselineV11.ApplyTextOverrides(frozenRawTexts),
            NpcDialogueBaselineV11.CreateRoutes());
        Check.True(
            frozenV11.Sha256 == NpcDialogueBaselineV11.ExpectedRevision &&
            NpcDialogueBaselineV11.CreateRoutes()
                .Where(static route => route.NpcKey is
                    "Athens_060" or "Sparta_060")
                .All(static route =>
                    route.InitialMenuSubIds.SequenceEqual(
                        [InstanceCallerProtocol.MedusaRootSubId])),
            "V11 remains frozen to its Medusa-only Instance Caller menu");
        var frozenV12 = WorldContentRevisionHasher.HashNpcDialogues(
            NpcDialogueBaselineV12.ApplyTextOverrides(frozenRawTexts),
            NpcDialogueBaselineV12.CreateRoutes());
        Check.Equal(
            NpcDialogueBaselineV12.ExpectedRevision,
            frozenV12.Sha256,
            "V12 remains frozen to its Battlefield and Instance expansion");
        var frozenV13 = WorldContentRevisionHasher.HashNpcDialogues(
            NpcDialogueBaselineV13.ApplyTextOverrides(frozenRawTexts),
            NpcDialogueBaselineV13.CreateRoutes());
        Check.Equal(
            NpcDialogueBaselineV13.ExpectedRevision,
            frozenV13.Sha256,
            "V13 remains frozen to its original Instance-attempt wording");
        var frozenV14 = WorldContentRevisionHasher.HashNpcDialogues(
            NpcDialogueBaselineV14.ApplyTextOverrides(frozenRawTexts),
            NpcDialogueBaselineV14.CreateRoutes());
        Check.Equal(
            NpcDialogueBaselineV14.ExpectedRevision,
            frozenV14.Sha256,
            "V14 remains frozen to its three-free-entry wording");

        var v15PublishedNpcKeys = NpcContentBaselineV2.LoadDefinitions()
            .Select(static npc => npc.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var v15RawTexts = NpcTemplateSeeds.Texts
            .Where(text => v15PublishedNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var v15Texts =
            NpcDialogueBaselineV15.ApplyTextOverrides(v15RawTexts);
        var v15Routes = NpcDialogueBaselineV15.CreateRoutes();
        var v15Revision = WorldContentRevisionHasher.HashNpcDialogues(
            v15Texts,
            v15Routes);

        Check.Equal(
            NpcDialogueBaselineV15.ExpectedTextCount,
            v15Texts.Length,
            "V15 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV15.ExpectedHashedEntryCount,
            v15Revision.EntryCount,
            "V15 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV15.ExpectedRevision,
            v15Revision.Sha256,
            "V15 NPC-dialogue canonical revision golden vector");
        Check.True(
            v15RawTexts.Zip(v15Texts)
                .Where(static pair => pair.First != pair.Second)
                .Select(static pair => pair.First.NpcKey)
                .SequenceEqual(
                    [
                        "Athens_056",
                        "Athens_060",
                        "Athens_142",
                        "Sparta_056",
                        "Sparta_060",
                        "Sparta_142"
                    ]),
            "V15 remains frozen without Arena text overrides");

        var publishedNpcKeys = NpcContentBaselineV3.LoadDefinitions()
            .Select(static npc => npc.NpcKey)
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
        var texts = NpcDialogueBaselineV16.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV16.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);
        Check.True(
            NpcDialogueBaselineV16.ExpectedSpawnRevision ==
                NpcContentBaselineV3.ExpectedRevision,
            "V16 dialogue pins the Arena V3 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedTextCount,
            texts.Length,
            "V16 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedHashedEntryCount,
            revision.EntryCount,
            "V16 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV16.ExpectedRevision,
            revision.Sha256,
            "V16 NPC-dialogue canonical revision golden vector");
        var changedTexts = rawTexts
            .Zip(texts)
            .Where(static pair => pair.First != pair.Second)
            .ToArray();
        Check.True(
            changedTexts.Select(static pair => pair.First.NpcKey)
                .SequenceEqual(
                    [
                        "Arena_002",
                        "Arena_003",
                        "Athens_056",
                        "Athens_060",
                        "Athens_142",
                        "Sparta_056",
                        "Sparta_060",
                        "Sparta_142"
                    ]) &&
            changedTexts.All(static pair =>
                pair.First.NpcKey == pair.Second.NpcKey &&
                pair.First.SceneKey == pair.Second.SceneKey &&
                pair.First.DisplayName == pair.Second.DisplayName) &&
            changedTexts.Single(static pair =>
                pair.First.NpcKey ==
                    DuelArenaTransporterProtocol.DoorkeeperNpcKey)
                .Second.Description ==
                    NpcDialogueBaselineV16.DoorkeeperDescription &&
            changedTexts.Single(static pair =>
                pair.First.NpcKey ==
                    DuelArenaTransporterProtocol.GatekeeperNpcKey)
                .Second.Description ==
                    NpcDialogueBaselineV16.GatekeeperDescription &&
            changedTexts
                .Where(static pair => pair.First.NpcKey.EndsWith(
                    "_056", StringComparison.Ordinal))
                .All(static pair => pair.Second.Description ==
                    NpcDialogueBaselineV12.BattlefieldTransporterDescription) &&
            changedTexts
                .Where(static pair => pair.First.NpcKey.EndsWith(
                    "_060", StringComparison.Ordinal))
                .All(static pair => pair.Second.Description ==
                    NpcDialogueBaselineV14.InstanceCallerDescription) &&
            changedTexts
                .Where(static pair => pair.First.NpcKey.EndsWith(
                    "_142", StringComparison.Ordinal))
                .All(static pair => pair.Second.Description ==
                    NpcDialogueBaselineV10.LevelSealerDescription),
            "V16 carries forward reviewed overlays and pins both Arena " +
            "endpoint descriptions");
        foreach (var npcKey in new[] { "Athens_086", "Sparta_086" })
        {
            var artisanRoute = routes.Single(
                route => route.NpcKey == npcKey);
            Check.True(
                artisanRoute.Behavior ==
                    NpcDialogueBehavior.HolyStone &&
                artisanRoute.InitialMenuSubIds.SequenceEqual(
                    [101, 201, 301, 401, 501, 601, 701, 801]),
                $"{npcKey} publishes Mount Gear Drilling action 801");
        }
        foreach (var npcKey in new[] { "Athens_088", "Sparta_088" })
        {
            var petManagerRoutes = routes
                .Where(route => route.NpcKey == npcKey)
                .ToArray();
            var petManagerNpc = NpcSpawnDefinitionFactory
                .Create(
                    npcKey.StartsWith("Athens", StringComparison.Ordinal)
                        ? (short)1
                        : (short)0,
                    [],
                    [],
                    [])
                .Single(npc => npc.NpcKey == npcKey);
            Check.True(
                petManagerRoutes.Length == 2 &&
                petManagerRoutes[0].RouteOrder == 0 &&
                petManagerRoutes[0].Behavior ==
                    NpcDialogueBehavior.PetManager &&
                petManagerRoutes[0].DialogIndex ==
                    PetManagerProtocol.DialogIndex &&
                petManagerRoutes[0].InitialMenuSubIds.SequenceEqual(
                    Enumerable.Range(1, 11)) &&
                NpcDialogueBehaviorRegistry.IsAllowed(
                    petManagerNpc,
                    petManagerRoutes[0]) &&
                petManagerRoutes[1].RouteOrder == 1 &&
                petManagerRoutes[1].Behavior ==
                    NpcDialogueBehavior.PetPointReset &&
                petManagerRoutes[1].DialogIndex ==
                    PetManagerProtocol.PointResetDialogIndex &&
                petManagerRoutes[1].InitialMenuSubIds.SequenceEqual(
                    PetManagerProtocol.PointResetInitialMenuSubIds) &&
                NpcDialogueBehaviorRegistry.IsAllowed(
                    petManagerNpc,
                    petManagerRoutes[1]),
                $"{npcKey} publishes both stock Pet Manager functions");
        }
        foreach (var npcKey in new[] { "Athens_055", "Sparta_055" })
        {
            var factionCrierRoute = routes.Single(
                route => route.NpcKey == npcKey);
            var mapId = npcKey.StartsWith(
                "Athens",
                StringComparison.Ordinal) ? (short)1 : (short)0;
            var factionCrierNpc = NpcSpawnDefinitionFactory
                .Create(mapId, [], [], [])
                .Single(npc => npc.NpcKey == npcKey);
            Check.True(
                factionCrierRoute.Behavior ==
                    NpcDialogueBehavior.FactionCrier &&
                factionCrierRoute.DialogIndex ==
                    FactionCrierProtocol.DialogIndex &&
                factionCrierRoute.InitialMenuSubIds.SequenceEqual(
                    FactionCrierProtocol.InitialMenuSubIds) &&
                NpcDialogueBehaviorRegistry.IsAllowed(
                    factionCrierNpc,
                    factionCrierRoute),
                $"{npcKey} publishes the bounded Faction Crier route");
        }

        var transporterEndpoints = new[]
        {
            (NpcKey: "Athens_041",
                Menu: TransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Mycenae_All_013",
                Menu: TransporterProtocol.MycenaeInitialMenuSubIds),
            (NpcKey: "Sparta_042",
                Menu: TransporterProtocol.SpartaInitialMenuSubIds)
        };
        foreach (var endpoint in transporterEndpoints)
        {
            var route = routes.Single(
                candidate => candidate.NpcKey == endpoint.NpcKey);
            var npc = NpcContentBaselineV6.LoadDefinitions()
                .Single(candidate => candidate.NpcKey == endpoint.NpcKey);
            Check.True(
                route.RouteOrder == 0 &&
                route.ClientScriptKey == endpoint.NpcKey &&
                route.Behavior == NpcDialogueBehavior.Transporter &&
                route.DialogIndex == TransporterProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual(endpoint.Menu) &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"{endpoint.NpcKey} publishes its finite Transporter route");
        }

        foreach (var npcKey in new[] { "Athens_060", "Sparta_060" })
        {
            var route = routes.Single(candidate => candidate.NpcKey == npcKey);
            Check.True(
                route.Behavior == NpcDialogueBehavior.InstanceCaller &&
                route.DialogIndex == InstanceCallerProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual(
                    InstanceCallerProtocol.InitialMenuSubIds),
                $"{npcKey} publishes Medusa, Atlantis, and Wonderland");
        }

        var battlefieldEndpoints = new[]
        {
            (NpcKey: "Athens_056",
                Menu: BattlefieldTransporterProtocol.AthensInitialMenuSubIds),
            (NpcKey: "Sparta_056",
                Menu: BattlefieldTransporterProtocol.SpartaInitialMenuSubIds)
        };
        foreach (var endpoint in battlefieldEndpoints)
        {
            var route = routes.Single(
                candidate => candidate.NpcKey == endpoint.NpcKey);
            Check.True(
                route.RouteOrder == 0 &&
                route.ClientScriptKey == endpoint.NpcKey &&
                route.Behavior ==
                    NpcDialogueBehavior.BattlefieldTransporter &&
                route.DialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual(endpoint.Menu),
                $"{endpoint.NpcKey} publishes its Battlefield menu");
        }

        foreach (var npcKey in new[]
                 {
                     DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                     DuelArenaTransporterProtocol.GatekeeperNpcKey
                 })
        {
            var route = routes.Single(candidate => candidate.NpcKey == npcKey);
            var npc = NpcContentBaselineV6.LoadDefinitions()
                .Single(candidate => candidate.NpcKey == npcKey);
            Check.True(
                route.RouteOrder == 0 &&
                route.ClientScriptKey == npcKey &&
                route.Behavior ==
                    NpcDialogueBehavior.DuelArenaTransporter &&
                route.DialogIndex ==
                    DuelArenaTransporterProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual(
                    DuelArenaTransporterProtocol.InitialMenuSubIds) &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"{npcKey} publishes the bounded same-scene Arena route");
        }
    }
}
