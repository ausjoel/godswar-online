using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckEntryPreparationRaceAsync()
    {
        foreach (var paid in new[] { false, true })
        {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: 2);
        var leader = fixture.Leader;
        var follower = fixture.Followers.Single();
        leader.Handler.InstanceEntryClock = new ManualTimeProvider();
        var clock = new CountdownPreparationRaceClock(() =>
            leader.Registry.LeaveParty(follower.Session, follower.Character.Name));
        leader.Handler.LegacyInstanceScheduleClock = clock;
        if (paid)
            await ConsentAtlantisRetryAsync(follower);
        await OpenCountdownAsync(leader, InstanceCallerProtocol.AtlantisRootSubId,
            paid ? InstanceCallerProtocol.AtlantisOpalSubId : InstanceCallerProtocol.AtlantisEnterSubId);
        await InvokeAsync(leader.Handler, CreateRepetitionResponse(224, 0, true), confirmEntry: false);
        Check.True(clock.Reads == 2 && daily.Claims.Count == 0 &&
            leader.Character.CurrentMap == leader.SourceMapId,
            "party mutation between countdown validation and fresh preparation cannot substitute a new roster");
        if (paid)
        {
            var destination = ResolveAtlantisOpalDestination();
            Check.True(leader.Registry.TryCaptureLegacyInstanceParty(leader.Session, destination,
                leader.Character.PositionX, leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance, out var current) == LegacyInstanceEntryStatus.Ready,
                "race fixture is now a different solo party");
            var result = leader.Registry.TryConsumeLegacyInstanceOpalRetryConsents(current, destination,
                leader.Character.PositionX, leader.Character.PositionZ, InstanceCallerProtocol.MaximumInteractionDistance,
                current.Members.Select(member => member.CharacterId).ToHashSet(), DateTimeOffset.UtcNow, out var missing);
            Check.True(result == LegacyInstanceOpalConsentValidationStatus.MissingConsent && missing.Length == 1,
                "rejecting a newly captured paid roster also revokes consent recorded by its fresh preparation");
        }
        }
    }

    private static async Task CheckEntryCanceledPaymentConsentAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var leader = fixture.Leader;
        leader.Handler.InstanceEntryClock = new ManualTimeProvider();
        foreach (var follower in fixture.Followers)
            await ConsentAtlantisRetryAsync(follower);
        await OpenCountdownAsync(leader, InstanceCallerProtocol.AtlantisRootSubId,
            InstanceCallerProtocol.AtlantisOpalSubId);
        var destination = ResolveAtlantisOpalDestination();
        Check.True(leader.Registry.TryCaptureLegacyInstanceParty(leader.Session, destination,
            leader.Character.PositionX, leader.Character.PositionZ,
            InstanceCallerProtocol.MaximumInteractionDistance, out var party) == LegacyInstanceEntryStatus.Ready,
            "paid countdown captures the explicitly consenting party");
        await InvokeAsync(leader.Handler, CreateRepetitionResponse(224, 0, false), confirmEntry: false);
        var result = leader.Registry.TryConsumeLegacyInstanceOpalRetryConsents(party, destination,
            leader.Character.PositionX, leader.Character.PositionZ, InstanceCallerProtocol.MaximumInteractionDistance,
            party.Members.Select(member => member.CharacterId).ToHashSet(), DateTimeOffset.UtcNow, out var missing);
        Check.True(result == LegacyInstanceOpalConsentValidationStatus.MissingConsent &&
            missing.Length == party.Members.Count,
            "canceling a paid countdown revokes its entire captured party consent instead of leaving reusable authorization");
    }

    private sealed class CountdownPreparationRaceClock(Action onSecondRead) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (++Reads == 2)
                onSecondRead();
            return new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        }
    }
}
