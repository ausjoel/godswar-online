using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string EntryCountdownCheckName =
        "Instance Caller native sixty-second Enter countdown fences admission and expires without blocking";

    public static async Task RunEntryCountdownAsync()
    {
        Check.Equal(TimeSpan.FromSeconds(60), GameClientHandler.InstanceEntryCountdownDuration,
            "Origin receiver 0x4BEBB8 starts BattleEnterWin at sixty seconds");
        Check.True(PacketBuilder.InstanceEntryQueueState(227).SequenceEqual(
                Convert.FromHexString("0C00EE27E300000000000000")) &&
            PacketBuilder.InstanceEntryNotice(227).SequenceEqual(
                Convert.FromHexString("0C00E827E300000000000000")),
            "Wonderland opening bytes match the 2026-09-11 16:14:40 external capture");
        await CheckEntryEarlyAndMalformedAsync();
        await CheckEntryAutomaticExpiryAsync();
        await CheckEntryCanceledAndReplayedAsync();
        await CheckEntryRepeatedShutdownAsync();
        await CheckEntryChangedAuthorityAsync();
        await CheckEntryDailyRevalidationAsync();
        await CheckEntryRejectedResetScopeAsync();
        await CheckEntryPartyRevalidationAsync();
        await CheckEntryPreparationRaceAsync();
        await CheckEntryCanceledPaymentConsentAsync();
        await CheckEntryOtherDestinationsAsync();
    }

    private static async Task CheckEntryEarlyAndMalformedAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateFixtureAsync(120, true, daily);
        var clock = new ManualTimeProvider();
        fixture.Handler.InstanceEntryClock = clock;
        fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
        var source = GetSourceInstanceId(fixture);
        var runtimeCount = fixture.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount;
        await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
            InstanceCallerProtocol.WonderlandEnterSubId);
        var start = fixture.ReadPackets().TakeLast(2).ToArray();
        Check.True(start[0].SequenceEqual(EntryGolden(227, 10222, 0)) &&
            start[1].SequenceEqual(EntryGolden(227, 10216, 0)) &&
            GetSourceInstanceId(fixture) == source && daily.Claims.Count == 0 &&
            fixture.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount == runtimeCount &&
            clock.ScheduledTimerCount == 1,
            "opening Enter returns promptly without creating a dungeon or reserving a daily attempt");
        var invalid = CreateRepetitionResponse(227, 0, true).Buffer;
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(12), 2);
        var extra = CreateRepetitionResponse(227, 0, true).Buffer.Concat(new byte[] { 0 }).ToArray();
        foreach (var packet in new[] { CreateRepetitionResponse(224, 0, true),
            CreateRepetitionResponse(227, 1234, true), new GamePacket(invalid), new GamePacket(extra) })
            await InvokeAsync(fixture.Handler, packet, confirmEntry: false);
        Check.True(PendingEntrySceneId(fixture.Handler) == 227 && daily.Claims.Count == 0,
            "wrong scene, invitation identity, response value, and frame size cannot activate the leader stage");
        clock.Advance(TimeSpan.FromSeconds(3));
        await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
        await AwaitCountdownAsync(fixture.Handler);
        Check.True(fixture.Character.CurrentMap == 207 && GetSourceInstanceId(fixture) != source &&
            daily.Claims.Count == 1 && PendingEntrySceneId(fixture.Handler) is null &&
            clock.ScheduledTimerCount == 0,
            "the capture's early Enter response consumes one stage and claims exactly one admission");
        var after = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
        clock.Advance(TimeSpan.FromMinutes(1));
        Check.True(daily.Claims.Count == 1 && fixture.ReadPackets().Count == after,
            "duplicate Enter and the canceled timer cannot admit twice");
    }

    private static async Task CheckEntryAutomaticExpiryAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateFixtureAsync(120, true, daily);
        var clock = new ManualTimeProvider();
        fixture.Handler.InstanceEntryClock = clock;
        fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
        await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
            InstanceCallerProtocol.WonderlandEnterSubId);
        clock.Advance(TimeSpan.FromSeconds(59));
        Check.True(daily.Claims.Count == 0 && PendingEntrySceneId(fixture.Handler) == 227,
            "the server does not enter before the complete sixty-second interval");
        var gate = GetHandlerField<SemaphoreSlim>(fixture.Handler, "_characterStateGate")!;
        await gate.WaitAsync();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Check.True(daily.Claims.Count == 0,
                "timer activation queues behind the character gate instead of racing packet mutations");
        }
        finally { gate.Release(); }
        await AwaitCountdownAsync(fixture.Handler);
        Check.True(fixture.Character.CurrentMap == 207 && daily.Claims.Count == 1,
            "expiry automatically admits even when the native UI sends no Enter packet");
    }

    private static async Task CheckEntryCanceledAndReplayedAsync()
    {
        foreach (var disconnect in new[] { false, true })
        {
            var daily = new ScriptedLegacyInstanceDailyEntryStore();
            await using var fixture = await CreateFixtureAsync(120, true, daily);
            var clock = new ManualTimeProvider();
            fixture.Handler.InstanceEntryClock = clock;
            fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
            await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId);
            if (disconnect)
                await StopCountdownAsync(fixture.Handler);
            else
            {
                await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, false), confirmEntry: false);
                Check.True(fixture.ReadPackets().Last().SequenceEqual(EntryGolden(227, 10222, 1)),
                    "decline clears the stock queue/Enter window with its native timeout state");
            }
            clock.Advance(TimeSpan.FromMinutes(2));
            await AwaitCountdownAsync(fixture.Handler);
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
            Check.True(fixture.Character.CurrentMap == fixture.SourceMapId && daily.Claims.Count == 0 &&
                PendingEntrySceneId(fixture.Handler) is null && clock.ScheduledTimerCount == 0,
                "decline and handler shutdown revoke pending entry without charging or later timer admission");
        }
    }

    private static async Task OpenCountdownAsync(InstanceCallerFixture fixture, int root, int choice)
    {
        await InvokeAsync(fixture.Handler, CreateActionPacket(root), confirmEntry: false);
        await InvokeAsync(fixture.Handler, CreateActionPacket(root, choice), confirmEntry: false);
    }

    private static async Task CheckEntryRepeatedShutdownAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateFixtureAsync(120, true, daily);
        var clock = new ManualTimeProvider();
        fixture.Handler.InstanceEntryClock = clock;
        fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
        await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
            InstanceCallerProtocol.WonderlandEnterSubId);
        var first = StopCountdownAsync(fixture.Handler);
        var concurrent = StopCountdownAsync(fixture.Handler);
        Check.True(ReferenceEquals(first, concurrent), "concurrent cleanup shares one countdown stop task");
        await first;
        GetHandlerField<SemaphoreSlim>(fixture.Handler, "_characterStateGate")!.Dispose();
        await StopCountdownAsync(fixture.Handler);
        clock.Advance(TimeSpan.FromMinutes(2));
        Check.True(daily.Claims.Count == 0 && clock.ScheduledTimerCount == 0,
            "cleanup stays idempotent after the connection owner has disposed its character semaphore");
    }
}
