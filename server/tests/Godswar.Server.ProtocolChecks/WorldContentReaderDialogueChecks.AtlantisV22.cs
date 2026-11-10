using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckAtlantisV22CurrentRelease()
    {
        var keys = NpcContentBaselineV7.LoadDefinitions()
            .Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => keys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(text.NpcKey, text.SceneKey,
                text.DisplayName, text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal).ToArray();
        var previous = NpcDialogueBaselineV21.ApplyTextOverrides(rawTexts);
        var current = NpcDialogueBaselineV22.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV22.CreateRoutes();
        var payload = WorldContentRevisionHasher.HashNpcDialogues(current, routes);
        var release = WorldContentRevisionHasher.HashNpcDialogueRelease(
            payload, NpcDialogueBaselineV22.ExpectedSpawnRevision);
        Check.Equal(NpcDialogueBaselineV22.ExpectedRevision, payload.Sha256,
            "V22 NPC-dialogue payload canonical golden vector");
        Check.Equal("8BDD4615AC328B0DE51762EC9AF95AE2220CA5D589EBC6943AA109DAAEC821D2",
            release.Sha256, "V22 dependency-bound release canonical golden vector");
        Check.Equal(422, payload.EntryCount, "V22 retains 390 texts and 32 routes");
        Check.True(routes.SequenceEqual(NpcDialogueBaselineV21.CreateRoutes()) &&
            NpcDialogueBaselineV22.Profiles == NpcDialogueBaselineV21.Profiles &&
            NpcDialogueBaselineV22.Bindings == NpcDialogueBaselineV21.Bindings &&
            NpcDialogueBaselineV22.ExpectedSpawnRevision ==
                NpcDialogueBaselineV21.ExpectedSpawnRevision,
            "V22 preserves every V21 route, profile, binding, and spawn dependency");

        var changed = previous.Zip(current)
            .Where(static pair => pair.First != pair.Second).ToArray();
        Check.True(changed.Select(static pair => pair.First.NpcKey)
                .SequenceEqual(["Athens_060", "Sparta_060"]) &&
            changed.All(static pair =>
                pair.Second == (pair.First with
                {
                    Description = NpcDialogueBaselineV22.InstanceCallerDescription
                })),
            "V22 changes only the two capital Instance Caller descriptions");
        var text = NpcDialogueBaselineV22.InstanceCallerDescription;
        Check.True(text.Contains("Level 90-140", StringComparison.Ordinal) &&
            text.Contains("solo or in parties of 1-5", StringComparison.Ordinal) &&
            text.Contains("normal monsters give 1 point, elite monsters 10, and bosses 50",
                StringComparison.Ordinal) &&
            text.Contains("850 points within 40 minutes", StringComparison.Ordinal) &&
            text.Contains("3 free entries", StringComparison.Ordinal) &&
            text.Contains("one additional Atlantis entry costs that player one Opal",
                StringComparison.Ordinal) &&
            text.Contains("Wonderland grants 3 free entries and does not require Opals",
                StringComparison.Ordinal),
            "V22 describes the requested Atlantis rules and unchanged admission allowances");
    }
}
