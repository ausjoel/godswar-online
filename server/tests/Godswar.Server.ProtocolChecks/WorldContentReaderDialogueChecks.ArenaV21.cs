using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckArenaV21CurrentRelease()
    {
        var spawns = NpcContentBaselineV7.LoadDefinitions();
        var keys = spawns.Select(static npc => npc.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts.Where(text => keys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(text.NpcKey, text.SceneKey,
                text.DisplayName, text.Description)).ToArray();
        var texts = NpcDialogueBaselineV21.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV21.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);
        Check.True(texts.SequenceEqual(NpcDialogueBaselineV20.ApplyTextOverrides(rawTexts)),
            "V21 preserves every V20 text");
        Check.Equal(NpcDialogueBaselineV21.ExpectedRevision, revision.Sha256,
            "V21 NPC-dialogue canonical revision golden vector");
        Check.Equal(422, revision.EntryCount, "V21 pins 390 texts and 32 routes");
        CheckDependencyBoundRelease(revision);
        Check.True(NpcDialogueBaselineV20.CreateRoutes().All(previous =>
                routes.Single(route => route.NpcKey == previous.NpcKey &&
                    route.RouteOrder == previous.RouteOrder) == previous),
            "V21 preserves every V20 route");
        foreach (var route in routes.Where(static route =>
                     route.Behavior == NpcDialogueBehavior.DuelArenaServices))
        {
            Check.True(DuelArenaServiceProtocol.IsCapturedRoute(route),
                $"{route.NpcKey} matches the captured service protocol");
        }
        var physician = routes.Single(static route => route.NpcKey == "Arena_005");
        Check.True(physician.DialogIndex == 32 &&
            physician.InitialMenuSubIds.SequenceEqual([1, 100, 101, 102]),
            "Physician exposes exactly the four captured menu entries");
        foreach (var key in new[] { "Arena_002", "Arena_006" })
        {
            Check.True(routes.Single(route => route.NpcKey == key)
                    .InitialMenuSubIds.SequenceEqual([-1]),
                $"{key} records only its native initial request");
        }
        _ = PinnedWorldContentReader.Create("arena-services-v21", [57],
            spawns.Where(static spawn => spawn.MapId == 57), [], [], FixedLoadTime,
            texts.Where(static text => text.SceneKey == "Arena"),
            routes.Where(static route => route.NpcKey.StartsWith("Arena_",
                StringComparison.Ordinal)));
        var forged = physician with { InitialMenuSubIds = [-1] };
        AssertInvalidDialogue(CaptureUnavailable(() =>
            PinnedWorldContentReader.Create("forged-arena-service", [57],
                spawns.Where(static spawn => spawn.MapId == 57), [], [], FixedLoadTime,
                texts.Where(static text => text.SceneKey == "Arena"), [forged])),
            "service behavior does not allow unobserved negative menu IDs");
    }

    private static void CheckDependencyBoundRelease(WorldContentFamilyRevision payload)
    {
        var spawn = NpcDialogueBaselineV21.ExpectedSpawnRevision;
        var release = WorldContentRevisionHasher.HashNpcDialogueRelease(payload, spawn);
        Check.Equal("0413340B6531086E307459E5931557A05392424857A26AF368F271BA01205D24",
            release.Sha256, "dependency-bound V21 release canonical golden vector");
        Check.True(PostgresNpcDialogueBaselinePublisher.CurrentReleaseRevision !=
            release.Sha256, "current publication has advanced beyond the frozen V21 release");
        Check.True(release.Sha256 != payload.Sha256 &&
            release.Sha256 != WorldContentRevisionHasher.HashNpcDialogueRelease(
                payload, new string('A', 64)).Sha256 &&
            release.Sha256 != WorldContentRevisionHasher.HashNpcDialogueRelease(
                payload, spawn, contractVersion: 2).Sha256,
            "unchanged dialogue payload gets a new release for dependency or contract changes");
        Check.True(WorldContentRevisionHasher.MatchesNpcDialogueRelease(
                payload.Sha256, payload, spawn) &&
            WorldContentRevisionHasher.MatchesNpcDialogueRelease(
                release.Sha256, payload, spawn),
            "loader accepts historical payload releases and dependency-bound releases");
        Check.True(!WorldContentRevisionHasher.MatchesNpcDialogueRelease(
            release.Sha256, payload, new string('B', 64)),
            "dependency-bound release rejects a different spawn dependency");
    }
}
