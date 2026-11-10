using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandCompletedLeaveCheckName =
        "Wonderland completion countdown Leave exits only its settled participant and preserves party treasure";

    public static async Task RunWonderlandCompletedLeaveAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var party = fixture.Party;
        var leader = party.Leader;
        var follower = party.Followers.Single();
        var registry = leader.Registry;
        var chests = new RecordingWonderlandChestStore();
        registry.ConfigureWonderlandChests(chests);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        Check.True(!registry.TryResolveInstanceRunEndLeave(leader.Session, 227, 0, WonderlandNow(runtime), out _) &&
            !registry.TryResolveInstanceRunEndLeave(follower.Session, null, 0, WonderlandNow(runtime), out _),
            "completed Leave cannot bypass an active encounter or its leader-only cancellation policy");
        for (var island = 1; island <= 7; island++)
            await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        fixture.Titles.FailuresRemaining = 1;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var completed) &&
            completed.State == WonderlandRunState.Completed && completed.CompletedIslands == 8 &&
            registry.HasPendingWonderlandTitles(runtime.InstanceId),
            "the real final clear starts completion while its injected settlement failure remains pending");
        var pendingAt = AtlantisPacketCounts(party);
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        await InvokeAsync(follower.Handler, CreateRepetitionPanelAction(0, 0));
        Check.True(runtime.Map.Population == 2 && party.Characters.All(character => character.CurrentMap == 207) &&
            party.ReadAllPackets().Select((packets, index) => packets.Skip(pendingAt[index]).All(packet =>
                ReadOpcode(packet) != Opcodes.SceneChange && !IsAtlantisDepartureClear(packet))).All(value => value),
            "neither completed Leave control transfers a member or clears their panel before rewards settle");
        await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId) && fixture.Titles.AppliedReceipts.Count == 8,
            "all eight original milestone receipts settle exactly once before early departure");

        var invalidAt = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(224, 0));
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 1));
        Check.True(GetSourceInstanceId(leader) == runtime.InstanceId &&
            leader.ReadPackets().Skip(invalidAt).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "foreign scene IDs and nonzero run indices cannot leave the current completed instance");
        await using (var replacement = new ClientSession(new FactionCrierCaptureTransport()))
        {
            registry.ReplaceAccountSession(leader.Character.AccountId, replacement);
            Check.True(!registry.TryResolveInstanceRunEndLeave(leader.Session, 227, 0, WonderlandNow(runtime), out _) &&
                !registry.TryResolveInstanceRunEndLeave(replacement, 227, 0, WonderlandNow(runtime), out _),
                "replaced ownership and an unadmitted replacement cannot resolve a completed departure");
            GameHandlerOwnershipTestFences.Bind(registry, leader.Session, leader.Character.AccountId, leader.Character);
        }

        var originalStore = GetHandlerField<IGameStore>(leader.Handler, "_store")!;
        var checkpoints = new WonderlandExitPositionStore(() => throw new IOException("completed Leave checkpoint unavailable"));
        var leaveAt = leader.ReadPackets().Count;
        try
        {
            SetHandlerField<IGameStore>(leader.Handler, "_store", checkpoints);
            await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
            Check.True(checkpoints.Positions.Count == 1 && GetSourceInstanceId(leader) == runtime.InstanceId &&
                runtime.Map.Population == 2 && leader.ReadPackets().Skip(leaveAt).All(packet =>
                    ReadOpcode(packet) != Opcodes.SceneChange && !IsAtlantisDepartureClear(packet)),
                "a failed durable destination checkpoint leaves membership and the countdown panel intact for retry");
            await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        }
        finally { SetHandlerField(leader.Handler, "_store", originalStore); }
        var capital = leader.Character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        Check.True(checkpoints.Positions.Count == 2 && checkpoints.Positions.Last().Map == capital &&
            leader.Character.CurrentMap == capital && GetSourceInstanceId(leader) != runtime.InstanceId &&
            leader.ReadPackets().Skip(leaveAt).Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
            leader.ReadPackets().Skip(leaveAt).Any(IsAtlantisDepartureClear),
            "retrying the exact native Leave request performs one durable capital transition and clears its panel");
        Check.True(runtime.Map.Population == 1 && follower.Character.CurrentMap == 207 &&
            runtime.Map.TryGetWonderlandSnapshot(out var retained) && retained.State == WonderlandRunState.Completed &&
            retained.TerminalAt == completed.TerminalAt && retained.Deadline == completed.Deadline &&
            WonderlandCompletionPolicy.IsTreasureWindowOpen(retained, WonderlandNow(runtime)),
            "the leader's own Leave preserves the completed run and the other finisher's full treasure window");

        var finalChest = registry.AddWonderlandTreasureChests(follower.Session, []).Single(npc =>
            WonderlandTreasureChestPolicy.TryGetIsland(npc, out var island) && island == 8);
        follower.Character.PositionX = finalChest.X;
        follower.Character.PositionZ = finalChest.Z;
        registry.UpdateCharacter(follower.Session, follower.Character, advanceWorldRevision: false);
        await registry.ClaimWonderlandChestAsync(follower.Session, finalChest, WonderlandNow(runtime), CancellationToken.None);
        Check.True(chests.Requests.Count == 1 && chests.Requests[0].WorldInstanceId == runtime.InstanceId &&
            chests.Requests[0].Subject.CharacterId == follower.Character.Id,
            "the remaining finisher retains access to the same exact durable final-chest entitlement");
        var repeatAt = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        Check.True(leader.ReadPackets().Skip(repeatAt).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "replaying Leave after departure cannot produce another relocation");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await FlushWonderlandNoticeAudienceAsync(party);
        Check.True(party.ReadAllPackets().All(packets => !packets.Any(IsWonderlandCenteredNotice)),
            "completion is not announced while any member remains inside collecting treasure");

        follower.Character.CurrentHp = 0;
        follower.Character.MarkVitalsChanged();
        var followerAt = follower.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(follower.Handler, CreateRepetitionPanelAction(0, 0));
        var followerCapital = follower.Character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
        Check.True(follower.Character.CurrentMap == followerCapital && follower.Character.CurrentHp == 0 &&
            runtime.Map.Population == 0 && follower.Transport.ReadLegacyPackets().Skip(followerAt)
                .Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            "the other finisher can use the native panel Leave independently, even while dead, without an invented heal");
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await FlushWonderlandNoticeAudienceAsync(party);
        Check.True(party.ReadAllPackets().All(packets => !packets.Any(IsWonderlandCenteredNotice)),
            "the final departure still waits for that destination scene to finish loading before announcing");
        await CompleteAtlantisSceneReadinessAsync(follower.Handler);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await FlushWonderlandNoticeAudienceAsync(party);
        var announcement = PacketBuilder.CenteredAnnouncement(GameSessionRegistry.BuildWonderlandCompletionAnnouncement(
            leader.Character.Name, solo: false));
        Check.True(party.ReadAllPackets().All(packets => packets.Count(packet => packet.SequenceEqual(announcement)) == 1) &&
            fixture.Titles.AppliedReceipts.Count == 8 && chests.Requests.Count == 1,
            "all-ready completion announces once without replaying title settlement or claiming anyone's leftover loot");
    }
}
