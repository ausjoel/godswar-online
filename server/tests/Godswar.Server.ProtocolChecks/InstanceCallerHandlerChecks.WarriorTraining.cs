using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// WARRIOR (the admin F-51): the real handler answers root 13 with the client's page
/// when the Warriors' Training Ground switch is on, answers a ground button below
/// its level floor with the level line (1510), and ignores a button without the
/// page context - all without a daily-entry claim or a move.
/// </summary>
internal static partial class InstanceCallerHandlerChecks
{
    public const string WarriorTrainingPagesCheckName =
        "Instance Caller root 13 (Warriors' Training Ground) page, level line and page context";

    public static async Task RunWarriorTrainingPagesAsync()
    {
        using var open = InstanceEntrySwitchScope.Set(false, false, warriorTraining: true);
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: 1);
        var leader = fixture.Leader;
        leader.Character.Level = 50;
        var sourceMap = leader.Character.CurrentMap;
        var source = GetSourceInstanceId(leader);

        async Task<IReadOnlyList<byte[]>> SendAsync(GamePacket packet)
        {
            var before = leader.Transport.ReadLegacyPackets().Count;
            await InvokeAsync(leader.Handler, packet, confirmEntry: false);
            return leader.Transport.ReadLegacyPackets().Skip(before).ToArray();
        }

        // A button with no page context first: nothing at all.
        var stray = await SendAsync(CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId, 200));
        var page = await SendAsync(CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId));
        Check.True(
            stray.Count == 0 &&
            page.Count == 1 &&
            page[0].SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                InstanceCallerProtocol.AthensNpcId,
                InstanceCallerProtocol.DialogIndex,
                [203, 200, 201, 202, 301])),
            "root 13 answers [203, 200, 201, 202, 301]; a button without the page does nothing");

        // Level 50 pressing ground 3 (level 80+): the reference's level line, nothing else.
        var low = await SendAsync(CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId, 202));
        Check.True(
            low.Count == 1 &&
            low[0].SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                InstanceCallerProtocol.AthensNpcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisLevelResultSubId)),
            "below a ground's level floor the button answers 1510 (the client's level line)");
        Check.True(
            daily.Claims.Count == 0 &&
            PendingEntrySceneId(leader.Handler) is null &&
            leader.Character.CurrentMap == sourceMap &&
            GetSourceInstanceId(leader) == source,
            "no daily entry, no Enter window and no move for a refused button");

        // Switch off again: root 13 is refused before anything else.
        using var closed = InstanceEntrySwitchScope.Set(false, false, warriorTraining: false);
        var refused = await SendAsync(CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId));
        Check.True(
            refused.Count == 2 &&
            refused[0].SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                InstanceCallerProtocol.AthensNpcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.ClosedInstanceResultSubId)),
            "with the switch off root 13 gets the closed answer plus one note");
        closed.Dispose();

        await CheckWarriorTrainingRunAsync();
    }

    /// <summary>
    /// A level 65 leader with one member: root 13 -> button 201 (ground 2) opens the
    /// Enter window for scene 220; confirming lands the party in an exact run of map
    /// 202 with one kind-22 daily claim each; kills score 1 / 5 / 10, the 200th point
    /// completes the run, and the leader's end control is the scene-220 one.
    /// </summary>
    private static async Task CheckWarriorTrainingRunAsync()
    {
        using var open = InstanceEntrySwitchScope.Set(false, false, warriorTraining: true);
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: 2);
        foreach (var character in fixture.Characters)
        {
            character.Level = 65;
        }
        var leader = fixture.Leader;
        var member = fixture.Followers[0];
        leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
            leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        var source = GetSourceInstanceId(leader);

        await InvokeAsync(leader.Handler, CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId));
        await InvokeAsync(leader.Handler,
            CreateActionPacket(InstanceCallerProtocol.WarriorTrialRootSubId, 201), confirmEntry: false);
        Check.True(
            leader.Character.CurrentMap == leader.SourceMapId &&
            PendingEntrySceneId(leader.Handler) == 220,
            "button 201 opens the shared Enter window for ground 2 (scene 220)");

        await InvokeAsync(member.Handler, CreateRepetitionResponse(220, 0, accepted: true), confirmEntry: false);
        await InvokeAsync(leader.Handler, CreateRepetitionResponse(220, 0, accepted: true), confirmEntry: false);
        var runInstanceId = GetSourceInstanceId(leader);
        Check.True(
            leader.Character.CurrentMap == 202 && runInstanceId != source &&
            member.Character.CurrentMap == 202 &&
            leader.Registry.TryGetSessionWorldInstanceId(member.Session, out var memberInstance) &&
            memberInstance == runInstanceId &&
            leader.Registry.IsTrainingGroundAdmission(runInstanceId),
            "both confirm and land in one exact run of map 202 (a Training Ground admission)");
        Check.True(
            daily.Claims.Count > 0 &&
            daily.Claims.All(static claim => claim.InstanceKind == InstanceCallerEntryKind.WarriorTrainingGround2),
            "the daily entry is claimed under ground 2's own kind (22)");

        var at = DateTimeOffset.UtcNow;
        leader.Registry.RecordTrainingGroundKill(runInstanceId, 49201, 1, "A_normalE_boar_006", at);
        leader.Registry.RecordTrainingGroundKill(runInstanceId, 49273, 1, "A_eliteE_wasp_001", at);
        Check.Equal(0, leader.Registry.LastWarriorScrollCandidates.Length,
            "WARRIOR scrolls: a normal or elite kill completes no Magma Scorpion task");
        leader.Registry.RecordTrainingGroundKill(runInstanceId, 49283, 1, "A_bossE_scorpion_008", at);
        Check.Equal(16, leader.Registry.TrainingGroundScore(runInstanceId), "1 + 5 + 10 = 16 points");
        var candidates = leader.Registry.LastWarriorScrollCandidates;
        Check.True(candidates.Length == 2 && candidates.Contains(leader.Character.Id) &&
                   candidates.Contains(member.Character.Id),
            "WARRIOR scrolls: the Magma Scorpion completes the armed task of both members in the run (party credit)");
        await CheckWarriorScrollUseAsync(leader.Handler, leader.Character);
        for (var kill = 0; kill < 184; kill++)
        {
            leader.Registry.RecordTrainingGroundKill(runInstanceId, 49202u + (uint)(kill % 60), 10u + (uint)kill,
                "A_normalE_lion_004", at.AddSeconds(1));
        }
        Check.Equal(200, leader.Registry.TrainingGroundScore(runInstanceId), "the 200th point clears ground 2");

        // One world tick: the cleared run takes the completion branch (reward rows
        // through ground 2's table; no store in this fixture) and stays admitted for
        // the shared 30 s leave window. The fixture has no pinned content for map
        // 202, so the members are not world-ready and the panels are not asserted.
        await leader.Registry.AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Check.True(
            leader.Registry.IsTrainingGroundAdmission(runInstanceId) &&
            leader.Registry.TrainingGroundScore(runInstanceId) == 200,
            "after the clear, a world tick keeps the run for its leave window with the final score");
        Check.True(
            !leader.Registry.TryTerminateTrainingGroundRunFromLeader(leader.Session, 256, 0),
            "Heracles' scene (256) is not ground 2's end control");
    }

    /// <summary>WARRIOR scrolls (F-146 b): the use path's switch and faction gates, reached through the handler.</summary>
    private static async Task CheckWarriorScrollUseAsync(Godswar.Server.Game.GameClientHandler handler, Godswar.Server.State.GameCharacter character)
    {
        var use = typeof(Godswar.Server.Game.GameClientHandler).GetMethod("TryUseWarriorScrollAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        async Task<bool> UseAsync(int item) =>
            await (Task<bool>)use.Invoke(handler, [item, CancellationToken.None])!;
        var own = character.Camp == 1 ? 4838 : 4845;
        var other = character.Camp == 1 ? 4845 : 4838;
        Check.True(!await UseAsync(4837) && !await UseAsync(4852), "items next to the range are not Warriors' scrolls");
        using (InstanceEntrySwitchScope.Set(false, false, warriorTraining: false))
        {
            Check.True(!await UseAsync(own), "switch off: the scroll falls through to the old quest-scroll path");
        }
        using (InstanceEntrySwitchScope.Set(false, false, warriorTraining: true))
        {
            Check.True(await UseAsync(other), "switch on: the other faction's scroll is handled (refused, kept)");
            Check.True(await UseAsync(own), "switch on: the own faction's scroll is handled (no store here: kept)");
        }
    }
}
