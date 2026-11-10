using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckAtlantisV23CurrentRelease()
    {
        var keys = NpcContentBaselineV7.LoadDefinitions()
            .Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => keys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(text.NpcKey, text.SceneKey,
                text.DisplayName, text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal).ToArray();
        var previous = NpcDialogueBaselineV22.ApplyTextOverrides(rawTexts);
        var current = NpcDialogueBaselineV23.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV23.CreateRoutes();
        var payload = WorldContentRevisionHasher.HashNpcDialogues(current, routes);
        var release = WorldContentRevisionHasher.HashNpcDialogueRelease(
            payload, NpcDialogueBaselineV23.ExpectedSpawnRevision);
        Check.Equal(NpcDialogueBaselineV23.ExpectedRevision, payload.Sha256,
            "V23 NPC-dialogue payload canonical golden vector");
        Check.Equal("2533BF0747E36F8EC83C0D5D06A54BF49574C5EFE126AB7AA2A6C841B0B23665",
            release.Sha256, "V23 dependency-bound release canonical golden vector");
        Check.Equal(PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision,
            release.Sha256, "publisher selects the dependency-bound V23 release");
        Check.Equal(422, payload.EntryCount, "V23 retains 390 texts and 32 routes");
        Check.True(routes.SequenceEqual(NpcDialogueBaselineV22.CreateRoutes()) &&
            NpcDialogueBaselineV23.Profiles == NpcDialogueBaselineV22.Profiles &&
            NpcDialogueBaselineV23.Bindings == NpcDialogueBaselineV22.Bindings &&
            NpcDialogueBaselineV23.ExpectedSpawnRevision ==
                NpcDialogueBaselineV22.ExpectedSpawnRevision,
            "V23 preserves every V22 route, profile, binding, and spawn dependency");

        var changed = previous.Zip(current)
            .Where(static pair => pair.First != pair.Second).ToArray();
        Check.True(changed.Select(static pair => pair.First.NpcKey)
                .SequenceEqual(["Athens_060", "Sparta_060"]) &&
            changed.All(static pair => pair.Second == (pair.First with
            {
                Description = pair.First.Description.Replace(
                    "Level 90-140", "Level 90+", StringComparison.Ordinal)
            })),
            "V23 changes only the two capital descriptions' Atlantis level ceiling");
        Check.True(NpcDialogueBaselineV23.InstanceCallerDescription.Contains(
                "welcomes Level 90+ players solo or in parties of 1-5", StringComparison.Ordinal) &&
            !NpcDialogueBaselineV23.InstanceCallerDescription.Contains("90-140", StringComparison.Ordinal),
            "V23 describes minimum level 90 with no upper entry cap");
    }
}
