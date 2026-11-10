using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    /// <summary>
    /// A member's client resends its confirmation while the first one is still
    /// being admitted. The first confirmation already minted the reservation that
    /// member enters on, so a repeat must not spend a second one - that duplicate
    /// queueing is what pulled one character in repeatedly and charged him the
    /// daily entry again and again.
    /// </summary>
    private static async Task CheckRepeatedConfirmationQueuesOnceAsync()
    {
        using var open = InstanceEntrySwitchScope.Open(); // INSTANCES
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        // 港湾遇袭 admits a party of at least three, so the third member only
        // stays behind to keep the leader's entry legal.
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 3);
        foreach (var character in fixture.Characters)
        {
            character.Level = 55;
        }
        var leader = fixture.Leader;
        var member = fixture.Followers[0];

        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId));
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.HarborAttackRootSubId,
                InstanceCallerProtocol.HarborAttackPageSubId),
            confirmEntry: false);

        // The leader commits the run on his own window; the member who answers
        // afterwards is the one whose confirmation queues an admission.
        await InvokeAsync(
            leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        var runInstanceId = GetSourceInstanceId(leader);
        Check.True(
            leader.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            member.Character.CurrentMap == leader.SourceMapId,
            "the leader's run is committed while the member stays outside");

        var claimsBefore = daily.Claims.Count;
        var confirmation = CreateRepetitionResponse(
            InstanceCallerProtocol.HarborAttackFirstClientSceneId,
            0,
            accepted: true);
        await InvokeAsync(member.Handler, confirmation, confirmEntry: false);
        // A member is queued for the run before his own destination scene is
        // ready; the world tick's admission needs that readiness, exactly as it
        // does for a member who came in with the leader.
        await CompleteAtlantisSceneReadinessAsync(member.Handler);
        Check.True(
            leader.Registry.IsMemberEntryJoinInFlight(
                member.Session,
                runInstanceId),
            "the confirmation that joins the committed run puts the member on " +
            "the way in");

        await InvokeAsync(member.Handler, confirmation, confirmEntry: false);
        await InvokeAsync(member.Handler, confirmation, confirmEntry: false);

        var memberClaims = daily.Claims
            .Skip(claimsBefore)
            .Where(claim => claim.CharacterIds.Contains(member.Character.Id))
            .ToArray();
        Check.True(
            memberClaims.Length == 1 &&
            memberClaims[0].CharacterIds.Count == 1,
            "a repeated confirmation spends exactly one entry reservation for " +
            "the member instead of queueing him once per frame");

        for (var tick = 0;
             tick < 4 &&
             member.Character.CurrentMap !=
                 InstanceCallerProtocol.HarborAttackFirstMapId;
             tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        Check.True(
            member.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                member.Session,
                out var memberInstanceId) &&
            memberInstanceId == runInstanceId &&
            !leader.Registry.IsMemberEntryJoinInFlight(
                member.Session,
                memberInstanceId),
            "the member still enters the committed run exactly once, and the " +
            "admission releases his in-flight claim");
    }
}
