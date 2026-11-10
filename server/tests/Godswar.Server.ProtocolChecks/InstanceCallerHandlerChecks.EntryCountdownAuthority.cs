using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckEntryChangedAuthorityAsync()
    {
        foreach (var change in new[] { "dead", "distance", "session removed", "cutoff" })
        {
            var daily = new ScriptedLegacyInstanceDailyEntryStore();
            await using var fixture = await CreateFixtureAsync(120, true, daily);
            var clock = new ManualTimeProvider();
            fixture.Handler.InstanceEntryClock = clock;
            fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
            await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId);
            var before = fixture.ReadPackets().Count;
            switch (change)
            {
                case "dead": fixture.Character.CurrentHp = 0; break;
                case "distance": fixture.Character.PositionX += 50; break;
                case "session removed": fixture.Registry.Remove(fixture.Session); break;
                case "cutoff": fixture.Handler.LegacyInstanceScheduleClock = new WonderlandFixedScheduleClock(
                    new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero)); break;
            }
            clock.Advance(TimeSpan.FromSeconds(60));
            await AwaitCountdownAsync(fixture.Handler);
            Check.True(fixture.Character.CurrentMap == fixture.SourceMapId && daily.Claims.Count == 0 &&
                PendingEntrySceneId(fixture.Handler) is null,
                $"{change} during countdown revokes admission before daily claims");
            var emitted = fixture.ReadPackets().Skip(before).ToArray();
            Check.True((change == "session removed" ? emitted.Length == 0 :
                    emitted.Any(packet => packet.SequenceEqual(PacketBuilder.RepetitionReset()))) &&
                emitted.All(packet => !packet.SequenceEqual(EntryGolden(227, 10222, 2))),
                $"{change} silently clears its consumed pending queue without the misleading verification error");
            if (change is not ("cutoff" or "session removed"))
                Check.True(emitted.Any(packet => ReadOpcode(packet) == Opcodes.ServerNote &&
                    System.Text.Encoding.Latin1.GetString(packet).Contains(change == "dead" ? "Revive" :
                        change == "distance" ? "within 12 units" : "party or session changed", StringComparison.Ordinal)),
                    $"{change} explains the action needed for another entry attempt");
        }
    }

    private static async Task CheckEntryPartyRevalidationAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using (var fixture = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: 2))
        {
            var leader = fixture.Leader;
            leader.Handler.InstanceEntryClock = new ManualTimeProvider();
            await OpenCountdownAsync(leader, InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisEnterSubId);
            Check.True(PendingEntrySceneId(leader.Handler) == 224 && daily.Claims.Count == 0,
                "Atlantis captures the party before opening its Enter countdown");
            var follower = fixture.Followers.Single();
            leader.Registry.LeaveParty(follower.Session, follower.Character.Name);
            await InvokeAsync(leader.Handler, CreateRepetitionResponse(224, 0, true), confirmEntry: false);
            Check.True(daily.Claims.Count == 0 && leader.Character.CurrentMap == leader.SourceMapId,
                "a changed Atlantis party cannot silently become a different admission at Enter");
        }
        await using (var fixture = await CreatePartyChoiceFixtureAsync())
        {
            var leader = fixture.Leader;
            leader.Handler.InstanceEntryClock = new ManualTimeProvider();
            await OpenCountdownAsync(leader, InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId);
            var before = fixture.Transport.ReadLegacyPackets().Count;
            leader.Registry.LeaveParty(fixture.MemberSession, fixture.Member.Name);
            await InvokeAsync(leader.Handler, CreateRepetitionResponse(209, 0, true), confirmEntry: false);
            Check.True(leader.Character.CurrentMap == leader.SourceMapId &&
                fixture.Transport.ReadLegacyPackets().Skip(before).All(packet => !IsRepetitionInvitation(packet)),
                "a changed Medusa party cannot acquire a dungeon or invitations from old countdown authority");
        }
    }

    private static async Task CheckEntryOtherDestinationsAsync()
    {
        foreach (var (root, choice, scene, map) in new[]
        {
            (InstanceCallerProtocol.AtlantisRootSubId, InstanceCallerProtocol.AtlantisEnterSubId, 224, 205),
            (InstanceCallerProtocol.MedusaRootSubId, InstanceCallerProtocol.AdvancedDifficultySubId, 209, 200),
            (InstanceCallerProtocol.MedusaRootSubId, InstanceCallerProtocol.NormalDifficultySubId, 223, 204),
            (InstanceCallerProtocol.MedusaRootSubId, InstanceCallerProtocol.MythicDifficultySubId, 209, 200)
        })
        {
            await using var fixture = await CreateFixtureAsync(120, true);
            fixture.Handler.InstanceEntryClock = new ManualTimeProvider();
            await OpenCountdownAsync(fixture, root, choice);
            Check.True(PendingEntrySceneId(fixture.Handler) == scene &&
                fixture.Character.CurrentMap == fixture.SourceMapId,
                $"content map {map} waits on the correct client scene {scene}");
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(scene, 0, true), confirmEntry: false);
            Check.Equal(map, (int)fixture.Character.CurrentMap,
                $"native Enter starts the existing admission path for map {map}");
        }
    }
}
