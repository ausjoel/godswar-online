using System.Buffers.Binary;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerProtocolChecks
{
    public const string CheckName =
        "Stock Instance Caller dialogue protocol";

    public static Task RunAsync()
    {
        Check.Equal(9, InstanceCallerProtocol.DialogIndex,
            "Instance Caller uses loaded NpcFunRepetition dialog 9");
        Check.Equal(92, InstanceCallerProtocol.ActionPacketBytes,
            "Instance Caller uses the canonical NPC action frame");
        Check.True(
            InstanceCallerProtocol.InitialMenuSubIds.SequenceEqual(
                [11, 14, 15]) &&
            InstanceCallerProtocol.MedusaPageSubIds.SequenceEqual(
                [206, 204, 205, 207]) &&
            InstanceCallerProtocol.AtlantisPageSubIds.SequenceEqual(
                [208, 209, 210]) &&
            InstanceCallerProtocol.WonderlandPageSubIds.SequenceEqual(
                [211]) &&
            InstanceCallerProtocol.QueueUnavailableResultSubId == 1000,
            "root and destination pages are finite and preserve Medusa order");
        Check.True(
            InstanceCallerProtocol.IsEndpoint("Athens_060", 5198) &&
            InstanceCallerProtocol.IsEndpoint("Sparta_060", 5057) &&
            !InstanceCallerProtocol.IsEndpoint("Sparta_060", 5059) &&
            !InstanceCallerProtocol.IsEndpoint("Athens_061", 5198),
            "only published capital Instance Caller endpoints are accepted");

        CheckNavigationAndDifficultyPaths();
        CheckLegacyPageAndEntryPaths();
        CheckLegacyScheduleBoundaries();
        CheckPublishedCurrentRoutes();
        CheckClientAttemptDialogue();
        CheckLeaderInstancePanelPackets();
        CheckCompletionPackets();
        return Task.CompletedTask;
    }

    private static void CheckCompletionPackets()
    {
        var reward = PacketBuilder.RepetitionReward(2_250);
        const string message =
            "The team earned the title of 'Medusa Challengers'.";
        var notice = PacketBuilder.ServerNote(message);

        Check.True(
            reward.Length == 104 &&
            BinaryPrimitives.ReadUInt16LittleEndian(reward) == 104 &&
            BinaryPrimitives.ReadUInt16LittleEndian(reward.AsSpan(2)) ==
                Opcodes.RepetitionReward &&
            BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(4)) == 17 &&
            BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(92)) ==
                2_250 &&
            reward.AsSpan(8, 84).IndexOfAnyExcept((byte)0) < 0 &&
            reward.AsSpan(96).IndexOfAnyExcept((byte)0) < 0,
            "native repetition reward carries type 17 and Medusa Honor at offset 92");
        Check.True(
            notice.Length == 260 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice) == 260 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) ==
                Opcodes.ServerNote &&
            notice.AsSpan(4, message.Length).SequenceEqual(
                System.Text.Encoding.ASCII.GetBytes(message)) &&
            notice[4 + message.Length] == 0 &&
            Opcodes.Name(Opcodes.RepetitionReward) ==
                nameof(Opcodes.RepetitionReward) &&
            Opcodes.Name(Opcodes.ServerNote) == nameof(Opcodes.ServerNote),
            "native server-note packet carries the exact faction message");
    }

    private static void CheckLeaderInstancePanelPackets()
    {
        var sync = PacketBuilder.RepetitionSync(
            repetitionId: 209,
            repetitionIndex: 0,
            groupIndex: 0,
            state: 5,
            entryLimit: 1);
        var fight = PacketBuilder.RepetitionFightInfo(
            remainingSeconds: 2_399,
            teamScore: 17);
        Check.True(
            sync.Length == 14 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync) == 14 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(2)) ==
                Opcodes.RepetitionSync &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(4)) == 209 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(6)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(10)) == 5 &&
            fight.Length == 20 &&
            BinaryPrimitives.ReadUInt16LittleEndian(fight) == 20 &&
            BinaryPrimitives.ReadUInt16LittleEndian(fight.AsSpan(2)) ==
                Opcodes.RepetitionFightInfo &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(4)) ==
                2_399 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(12)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(16)) == 17,
            "native repetition sync and fight-info frames carry the leader timer state");

        var completion = PacketBuilder.RepetitionCompletionState(
            repetitionId: 209,
            completed: true);
        var countdown = PacketBuilder.RepetitionCountdown(30);
        Check.True(
            completion.Length == 12 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                completion.AsSpan(2)) ==
                Opcodes.RepetitionCompletionState &&
            BinaryPrimitives.ReadInt32LittleEndian(
                completion.AsSpan(4)) == 209 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                completion.AsSpan(8)) == 1 &&
            countdown.Length == 8 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                countdown.AsSpan(2)) == Opcodes.RepetitionReset &&
            BinaryPrimitives.ReadInt32LittleEndian(
                countdown.AsSpan(4)) == 30,
            "captured completion state opens the 30-second terminate dialog");

        var roster = PacketBuilder.RepetitionInstanceMembers(
        [
            new(202, "-Alboz-", 139, RepetitionMemberState.Online, 1),
            new(203, "Perseus", 90, RepetitionMemberState.Offline, 3)
        ]);
        Check.True(
            roster.Length == 96 &&
            BinaryPrimitives.ReadUInt16LittleEndian(roster.AsSpan(2)) ==
                Opcodes.RepetitionInstanceMembers &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(4)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(8)) == 202 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(44)) == 139 &&
            roster[48] == (byte)RepetitionMemberState.Online &&
            roster[49] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(52)) == 203 &&
            roster[92] == 0 &&
            roster[93] == 3,
            "captured instance roster frame carries exact online and profession fields");
    }

    private static void CheckNavigationAndDifficultyPaths()
    {
        Check.True(
            InstanceCallerProtocol.TryGetMedusaPage(
                9,
                11,
                Arguments(),
                out var page) &&
            page.SequenceEqual([206, 204, 205, 207]),
            "root action 11 opens the Medusa difficulty page");
        Check.True(
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(204),
                out var advanced) &&
            advanced == InstanceCallerDifficulty.Advanced &&
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(205),
                out var normal) &&
            normal == InstanceCallerDifficulty.Normal &&
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(207),
                out var mythic) &&
            mythic == InstanceCallerDifficulty.Mythic,
            "nested client paths select Advanced, Normal, and Mythic");

        var polluted = Arguments(204);
        polluted[1] = 0;
        Check.True(
            !InstanceCallerProtocol.TryGetMedusaPage(
                10,
                11,
                Arguments(),
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                204,
                Arguments(),
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                polluted,
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments()[..^1],
                out _),
            "wrong dialog, loose sub-id, polluted paths, and short frames fail");
    }

    private static void CheckLegacyPageAndEntryPaths()
    {
        Check.True(
            InstanceCallerProtocol.TryGetPage(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisRootSubId,
                Arguments(),
                out var atlantisRoot,
                out var atlantisPage) &&
            atlantisRoot == InstanceCallerProtocol.AtlantisRootSubId &&
            atlantisPage.SequenceEqual([208, 209, 210]) &&
            InstanceCallerProtocol.TryGetPage(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.WonderlandRootSubId,
                Arguments(),
                out var wonderlandRoot,
                out var wonderlandPage) &&
            wonderlandRoot == InstanceCallerProtocol.WonderlandRootSubId &&
            wonderlandPage.SequenceEqual([211]),
            "Atlantis and Wonderland roots open their exact stock pages");

        Check.True(
            InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisRootSubId,
                Arguments(InstanceCallerProtocol.AtlantisEnterSubId),
                out var atlantis) &&
            atlantis.Kind == InstanceCallerEntryKind.Atlantis &&
            atlantis.DisplayName == "Atlantis" &&
            atlantis.TargetMapId == 205 &&
            atlantis.TargetX == 171f &&
            atlantis.TargetZ == 24f &&
            atlantis.MinimumLevel == 90 &&
            atlantis.MaximumLevel == int.MaxValue &&
            atlantis.RequiredPartySize is null &&
            atlantis.PaymentMode ==
                InstanceCallerEntryPaymentMode.FreeOnly &&
            InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisRootSubId,
                Arguments(InstanceCallerProtocol.AtlantisOpalSubId),
                out var paidAtlantis) &&
            paidAtlantis.Kind == InstanceCallerEntryKind.Atlantis &&
            paidAtlantis.MinimumLevel == 90 &&
            paidAtlantis.MaximumLevel == int.MaxValue &&
            paidAtlantis.RequiredPartySize is null &&
            paidAtlantis.PaymentMode ==
                InstanceCallerEntryPaymentMode.OpalRetry,
            "Atlantis enter path resolves the reviewed map, arrival, and " +
            "level 90 minimum without an upper cap for solo or parties up to five, while 209 is " +
            "explicit paid retry");
        Check.True(
            InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.WonderlandRootSubId,
                Arguments(InstanceCallerProtocol.WonderlandEnterSubId),
                out var wonderland) &&
            wonderland.Kind == InstanceCallerEntryKind.Wonderland &&
            wonderland.DisplayName == "Wonderland" &&
            wonderland.TargetMapId == 207 &&
            wonderland.TargetX == 120f &&
            wonderland.TargetZ == 209f &&
            wonderland.MinimumLevel == 120 &&
            wonderland.MaximumLevel == int.MaxValue &&
            wonderland.RequiredPartySize is null,
            "Wonderland enter path resolves the reviewed map and arrival");

        var pollutedAtlantis = Arguments(
            InstanceCallerProtocol.AtlantisEnterSubId);
        pollutedAtlantis[1] = 0;
        Check.True(
            !InstanceCallerProtocol.TryGetPage(
                InstanceCallerProtocol.DialogIndex + 1,
                InstanceCallerProtocol.AtlantisRootSubId,
                Arguments(),
                out _,
                out _) &&
            !InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisEnterSubId,
                Arguments(),
                out _) &&
            !InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisRootSubId,
                Arguments(InstanceCallerProtocol.AtlantisDescriptionSubId),
                out _) &&
            !InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisRootSubId,
                pollutedAtlantis,
                out _) &&
            !InstanceCallerProtocol.TryResolveEntry(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.WonderlandRootSubId,
                Arguments(InstanceCallerProtocol.WonderlandEnterSubId)[..^1],
                out _),
            "wrong dialog/root, informational choices, polluted paths, and " +
            "short legacy entry frames fail closed");
    }

    private static void CheckLegacyScheduleBoundaries()
    {
        var calendar = RealmCalendar.CreateForTesting(RealmId.Tempest);
        var saturdayBeforeCutoff = new DateTimeOffset(
            2026,
            8,
            29,
            22,
            59,
            59,
            TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1);
        var saturdayAtCutoff = new DateTimeOffset(
            2026,
            8,
            29,
            23,
            0,
            0,
            TimeSpan.Zero);
        var sundayBeforeCutoff = new DateTimeOffset(
            2026,
            8,
            30,
            22,
            59,
            59,
            TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1);
        var sundayAtCutoff = new DateTimeOffset(
            2026,
            8,
            30,
            23,
            0,
            0,
            TimeSpan.Zero);
        var weekdayBeforeCutoff = new DateTimeOffset(
            2026,
            8,
            28,
            22,
            59,
            0,
            TimeSpan.Zero);
        var weekdayAtCutoff = new DateTimeOffset(
            2026,
            8,
            31,
            23,
            0,
            0,
            TimeSpan.Zero);

        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Atlantis,
                calendar,
                weekdayAtCutoff) == LegacyInstanceScheduleStatus.Open,
            "Atlantis has no Wonderland weekend or 23:00 restriction");
        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                saturdayBeforeCutoff) ==
                    LegacyInstanceScheduleStatus.Open &&
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                sundayBeforeCutoff) ==
                    LegacyInstanceScheduleStatus.Open,
            "Wonderland remains open through the final instant before " +
            "23:00 on both weekend days");
        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                saturdayAtCutoff) ==
                    LegacyInstanceScheduleStatus.DailyCutoffPassed &&
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                sundayAtCutoff) ==
                    LegacyInstanceScheduleStatus.DailyCutoffPassed,
            "Wonderland closes exactly at 23:00 on Saturday and Sunday");
        Check.True(
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                weekdayBeforeCutoff) ==
                    LegacyInstanceScheduleStatus.WrongDay &&
            LegacyInstanceAdmissionPolicy.CheckSchedule(
                InstanceCallerEntryKind.Wonderland,
                calendar,
                weekdayAtCutoff) ==
                    LegacyInstanceScheduleStatus.WrongDay,
            "Wonderland rejects weekdays before applying the daily cutoff");
    }

    private static void CheckPublishedCurrentRoutes()
    {
        var routes = NpcDialogueBaselineV14.CreateRoutes();
        var npcs = NpcContentBaselineV1.LoadDefinitions()
            .Where(npc => npc.NpcKey is "Athens_060" or "Sparta_060")
            .OrderBy(npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            NpcDialogueBaselineV14.ExpectedProfileCount == 16 &&
            NpcDialogueBaselineV14.ExpectedRouteCount == 27 &&
            NpcDialogueBaselineV14.ExpectedMenuEntryCount == 77 &&
            npcs.Length == 2,
            "V14 publishes the reviewed finite dialogue surface");
        foreach (var npc in npcs)
        {
            var route = routes.Single(candidate =>
                candidate.NpcKey == npc.NpcKey);
            Check.True(
                route.Behavior == NpcDialogueBehavior.InstanceCaller &&
                route.DialogIndex == InstanceCallerProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual([11, 14, 15]) &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"{npc.NpcKey} has the allowlisted Instance Caller route");
        }
    }

    private static int[] Arguments(params int[] path)
    {
        var arguments = Enumerable.Repeat(
            -1,
            InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        path.CopyTo(arguments, 0);
        return arguments;
    }
}
