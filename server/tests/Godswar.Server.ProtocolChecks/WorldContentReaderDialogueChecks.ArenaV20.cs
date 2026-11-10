using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderDialogueChecks
{
    private static void CheckArenaV20CurrentRelease()
    {
        var spawns = NpcContentBaselineV7.LoadDefinitions();
        var publishedNpcKeys = spawns.Select(static npc => npc.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        var rawTexts = NpcTemplateSeeds.Texts
            .Where(text => publishedNpcKeys.Contains(text.NpcKey))
            .Select(static text => new NpcTextDefinition(
                text.NpcKey, text.SceneKey, text.DisplayName, text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var texts = NpcDialogueBaselineV20.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV20.CreateRoutes();
        var revision = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);
        Check.Equal(NpcContentBaselineV7.ExpectedRevision,
            NpcDialogueBaselineV20.ExpectedSpawnRevision,
            "V20 pins the captured V7 Arena spawn release");
        Check.Equal(NpcDialogueBaselineV20.ExpectedTextCount, texts.Length,
            "V20 covers all 390 published NPCs");
        Check.Equal(NpcDialogueBaselineV20.ExpectedHashedEntryCount,
            revision.EntryCount, "V20 dialogue hashed-entry count");
        Check.Equal(NpcDialogueBaselineV20.ExpectedRevision, revision.Sha256,
            "V20 NPC-dialogue canonical revision golden vector");
        Check.True(texts.SequenceEqual(
                NpcDialogueBaselineV20.ApplyTextOverrides(texts)),
            "V20 reviewed text additions are idempotent");

        foreach (var original in rawTexts.Where(static text =>
                     text.SceneKey == "Arena"))
        {
            Check.Equal(original,
                texts.Single(text => text.NpcKey == original.NpcKey),
                $"V20 restores stock Arena text for {original.NpcKey}");
        }

        Check.True(texts.Single(static text => text.NpcKey == "DuelArena_001")
                .DisplayName == "[Warehouse] Akou" &&
            texts.Single(static text => text.NpcKey == "Arena_006")
                .Description == "I offer the best airdrop for the community.",
            "V20 appends the captured warehouse and airdrop texts");
        var captured = routes.Where(static route =>
            route.Behavior == NpcDialogueBehavior.DuelArenaTransporter).ToArray();
        Check.True(captured.Select(static route => route.NpcKey)
                .SequenceEqual(["Arena_003", "Arena_004"]),
            "Gatekeeper and Ward replace the old Doorkeeper route");
        foreach (var route in captured)
        {
            var spawn = spawns.Single(npc => npc.NpcKey == route.NpcKey);
            Check.True(DuelArenaCapturedTransportProtocol.IsCapturedRoute(route) &&
                NpcDialogueBehaviorRegistry.IsAllowed(spawn, route),
                $"{route.NpcKey} permits its captured direct action only");
        }

        var gatekeeper = captured.Single(static route =>
            route.NpcKey == "Arena_003");
        Check.True(gatekeeper.ClientScriptKey == "Arena_002" &&
            gatekeeper.DialogIndex == 87 &&
            captured.Single(static route => route.NpcKey == "Arena_004")
                .DialogIndex == 88 &&
            captured.All(static route =>
                route.InitialMenuSubIds.SequenceEqual([-1])),
            "captured endpoints preserve dialogs 87/88 and initial action -1");

        _ = PinnedWorldContentReader.Create("captured-arena-v20", [57],
            spawns.Where(static spawn => spawn.MapId == 57), [], [], FixedLoadTime,
            texts.Where(static text => text.SceneKey == "Arena"), captured);
        var forged = gatekeeper with { ClientScriptKey = "Arena_004" };
        AssertInvalidDialogue(CaptureUnavailable(() =>
            PinnedWorldContentReader.Create("forged-arena-alias", [57],
                spawns.Where(static spawn => spawn.MapId == 57), [], [], FixedLoadTime,
                texts.Where(static text => text.SceneKey == "Arena"), [forged])),
            "the captured Gatekeeper alias does not allow other script keys");
        Check.True(!DuelArenaCapturedTransportProtocol.IsAllowedClientScriptKey(
                "Arena_004", "Arena_002") &&
            !DuelArenaCapturedTransportProtocol.IsAllowedClientScriptKey(
                "Athens_070", "Arena_002"),
            "the captured script alias cannot be reused by another NPC");
    }
}
