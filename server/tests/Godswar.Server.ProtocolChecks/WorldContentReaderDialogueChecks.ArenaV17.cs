using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckArenaV19CurrentRelease()
    {
        var publishedNpcKeys = NpcContentBaselineV6.LoadDefinitions()
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
        var texts = NpcDialogueBaselineV19.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV19.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(
            texts,
            routes);

        Check.True(
            NpcDialogueBaselineV19.ExpectedSpawnRevision ==
                NpcContentBaselineV6.ExpectedRevision,
            "V19 dialogue pins the native-ID Arena V6 spawn revision");
        Check.Equal(
            NpcDialogueBaselineV19.ExpectedTextCount,
            texts.Length,
            "V19 dialogue text count");
        Check.Equal(
            NpcDialogueBaselineV19.ExpectedHashedEntryCount,
            revision.EntryCount,
            "V19 dialogue hashed-entry count");
        Check.Equal(
            NpcDialogueBaselineV19.ExpectedRevision,
            revision.Sha256,
            "V19 NPC-dialogue canonical revision golden vector");

        var supportActors = new[]
        {
            (DuelArenaNpcRoster.VendorNpcKey, "Arena Vendor"),
            (DuelArenaNpcRoster.WardNpcKey, "Arena Ward"),
            (DuelArenaNpcRoster.PhysicianNpcKey, "Physician")
        };
        foreach (var (npcKey, displayName) in supportActors)
        {
            var raw = rawTexts.Single(text => text.NpcKey == npcKey);
            var published = texts.Single(text => text.NpcKey == npcKey);
            Check.True(
                raw == published &&
                published.DisplayName == displayName &&
                routes.All(route => route.NpcKey != npcKey),
                $"V19 retains {npcKey}'s stock text without inventing a " +
                "service protocol");
        }

        foreach (var npcKey in new[]
                 {
                     DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                     DuelArenaTransporterProtocol.GatekeeperNpcKey
                 })
        {
            var route = routes.Single(candidate =>
                candidate.NpcKey == npcKey);
            var npc = NpcContentBaselineV6.LoadDefinitions()
                .Single(candidate => candidate.NpcKey == npcKey);
            Check.True(
                route.Behavior ==
                    NpcDialogueBehavior.DuelArenaTransporter &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"V19 retains the bounded transport route for {npcKey}");
        }
    }
}
