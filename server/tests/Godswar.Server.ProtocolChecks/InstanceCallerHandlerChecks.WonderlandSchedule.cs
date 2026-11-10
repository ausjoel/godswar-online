using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandScheduleCheckName =
        "Wonderland admits weekdays and preserves the realm cutoff without consuming rejected entries";

    public static async Task RunWonderlandScheduleAsync()
    {
        foreach (var instant in new[]
        {
            new DateTimeOffset(2026, 9, 11, 0, 33, 0, TimeSpan.Zero), // Friday in Manila.
            new DateTimeOffset(2026, 9, 13, 15, 0, 0, TimeSpan.Zero).AddTicks(-1), // Sunday 22:59:59.9999999.
            new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero) // Monday midnight, still Sunday in UTC.
        })
        {
            await using var fixture = await CreateWonderlandHandlerFixtureAsync();
            fixture.Party.Leader.Handler.LegacyInstanceScheduleClock = new WonderlandFixedScheduleClock(instant);
            await EnterWonderlandHandlerAsync(fixture);
            Check.True(fixture.Daily.Claims.Count == 1 && fixture.Daily.Admissions.Count == 1,
                "weekday and weekend admission still reserves exactly one normal daily attempt");
        }

        foreach (var day in new[] { 11, 13 })
            await CheckWonderlandScheduleRejectedAsync(
                new DateTimeOffset(2026, 9, day, 15, 0, 0, TimeSpan.Zero),
                InstanceCallerProtocol.WonderlandCutoffResultSubId,
                "DailyCutoffPassed");
    }

    private static async Task CheckWonderlandScheduleRejectedAsync(
        DateTimeOffset instant, int expectedSubId, string expectedStatus)
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        leader.Handler.LegacyInstanceScheduleClock = new WonderlandFixedScheduleClock(instant);
        var origin = GetSourceInstanceId(leader);
        var originalMap = leader.Character.CurrentMap;
        var runtimeCount = leader.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount;
        await InvokeAsync(leader.Handler, CreateActionPacket(InstanceCallerProtocol.WonderlandRootSubId));
        var packetCount = leader.ReadPackets().Count;
        using var output = new StringWriter();
        var previousOutput = Console.Out;
        try
        {
            Console.SetOut(output);
            await InvokeAsync(leader.Handler, CreateActionPacket(
                InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId));
        }
        finally
        {
            Console.SetOut(previousOutput);
        }

        var packets = leader.ReadPackets().Skip(packetCount).ToArray();
        var expected = PacketBuilder.NpcFunctionActionResponse(
            InstanceCallerProtocol.AthensNpcId,
            InstanceCallerProtocol.WonderlandResultDialogIndex,
            expectedSubId);
        Check.True(packets.Count(packet => packet.SequenceEqual(expected)) == 1 &&
            packets.All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            $"{expectedStatus} sends the native explanation without scene travel");
        Check.True(GetSourceInstanceId(leader) == origin && leader.Character.CurrentMap == originalMap &&
            leader.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount == runtimeCount &&
            fixture.Daily.Claims.Count == 0 && fixture.Daily.Admissions.Count == 0,
            $"{expectedStatus} neither creates a dungeon nor reserves a daily attempt");
        var log = output.ToString();
        Check.True(log.Contains("[instance-caller] schedule rejected", StringComparison.Ordinal) &&
            log.Contains($"status={expectedStatus}", StringComparison.Ordinal) &&
            log.Contains("time_zone=Asia/Manila", StringComparison.Ordinal) &&
            log.Contains($"realm_time={instant.ToOffset(TimeSpan.FromHours(8)):O}", StringComparison.Ordinal),
            "the rejection log identifies the exact decision and pinned realm time");
    }

    private sealed class WonderlandFixedScheduleClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
