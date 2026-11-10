using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckEntryDailyRevalidationAsync()
    {
        Check.True(PacketBuilder.RepetitionReset().SequenceEqual(Convert.FromHexString("0800F72700000000")),
            "Origin 0x4BEE9F silently clears both waiting and Enter states with native 10231/value0");
        foreach (var size in new[] { 1, 2 })
        foreach (var automatic in new[] { false, true })
        {
            var daily = new ScriptedLegacyInstanceDailyEntryStore { DailyEntryLimit = 3 };
            await using var party = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: size);
            foreach (var character in party.Characters) character.Level = 120;
            var fixture = party.Leader;
            var clock = new ManualTimeProvider();
            fixture.Handler.InstanceEntryClock = clock;
            fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
            var source = GetSourceInstanceId(fixture);
            var runtimeCount = fixture.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount;
            await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId);
            var before = fixture.ReadPackets().Count;
            daily.ClaimStatus = LegacyInstanceDailyEntryClaimStatus.DailyLimitReached;
            if (automatic) clock.Advance(TimeSpan.FromSeconds(60));
            else await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
            await AwaitCountdownAsync(fixture.Handler);
            var message = size == 1
                ? "You have used all 3 Wonderland entries for today. Try again after the daily reset."
                : "A party member has used all 3 Wonderland entries for today. Everyone needs an available entry.";
            var emitted = fixture.ReadPackets().Skip(before).ToArray();
            Check.True(emitted.Length == 2 && emitted[0].SequenceEqual(PacketBuilder.RepetitionReset()) &&
                emitted[1].SequenceEqual(PacketBuilder.ServerNote(message)),
                "daily-limit rejection clears native queue first, then shows exactly one accurate solo/party explanation");
            Check.True(AllSessionsRemainAtSource(party, source) && daily.Claims.Count == 1 &&
                daily.Admissions.Count == 0 && daily.FullReleases.Count == 0 && daily.MemberReleases.Count == 0 &&
                PendingEntrySceneId(fixture.Handler) is null && clock.ScheduledTimerCount == 0 &&
                fixture.Registry.GetWorldInstanceDirectorySnapshot().RuntimeCount == runtimeCount,
                "daily rejection preserves every party member, creates no dungeon, and neither charges nor releases prior entries");
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
            clock.Advance(TimeSpan.FromMinutes(1));
            Check.True(daily.Claims.Count == 1 && fixture.ReadPackets().Count == before + 2,
                "replayed Enter and the expired timer cannot repeat the claim or daily-limit notice");

            // The rejection consumes only its own pending request. A later,
            // freshly authorized NPC interaction can still enter normally.
            daily.ClaimStatus = LegacyInstanceDailyEntryClaimStatus.Claimed;
            await OpenCountdownAsync(fixture, InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId);
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(227, 0, true), confirmEntry: false);
            await AwaitCountdownAsync(fixture.Handler);
            Check.True(fixture.Character.CurrentMap == 207 && daily.Claims.Count == 2 && daily.Admissions.Count > 0,
                "a fresh successful admission remains possible after the earlier pending entry was rejected");
        }
    }

    private static async Task CheckEntryRejectedResetScopeAsync()
    {
        foreach (var (root, choice, scene, map) in new[]
        {
            (InstanceCallerProtocol.WonderlandRootSubId, InstanceCallerProtocol.WonderlandEnterSubId, 227, 207),
            (InstanceCallerProtocol.MedusaRootSubId, InstanceCallerProtocol.NormalDifficultySubId, 223, 204)
        })
        {
            await using var fixture = await CreateFixtureAsync(120, true);
            fixture.Handler.InstanceEntryClock = new ManualTimeProvider();
            fixture.Handler.LegacyInstanceScheduleClock = new WonderlandSaturdayClock();
            await OpenCountdownAsync(fixture, root, choice);
            var pending = GetHandlerField<object>(fixture.Handler, "_pendingInstanceEntry")!;
            await InvokeAsync(fixture.Handler, CreateRepetitionResponse(scene, 0, true), confirmEntry: false);
            await AwaitCountdownAsync(fixture.Handler);
            Check.Equal(map, (int)fixture.Character.CurrentMap, "real entry commits before exercising delayed rejection cleanup");
            var before = fixture.ReadPackets().Count;
            // Exercise the common rejection publication used by both the
            // exception catch and stale-authority path after a real transfer.
            foreach (var reason in new[] { "ActivationFault", "PartyOrSessionChanged" })
                await (Task)FindHandlerMethod("RejectPendingInstanceEntryAsync").Invoke(fixture.Handler,
                    [pending, reason, "A late failure must not clear the active instance.", CancellationToken.None])!;
            Check.Equal(before, fixture.ReadPackets().Count,
                "late failure cannot reset or overwrite an admitted Wonderland or Medusa run's HUD");
        }
    }
}
