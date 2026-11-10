using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisFollowerConsentIsExplicitAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var follower = fixture.Followers[0];

        var emitted = await ConsentAtlantisRetryAsync(follower);

        Check.True(
            emitted.Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol
                        .AtlantisOpalConsentRecordedResultSubId)) &&
            daily.Claims.Count == 0 &&
            payments.Charges.Count == 0 &&
            fixture.Characters.All(character =>
                character.CurrentMap == fixture.Leader.SourceMapId),
            "a non-leader explicitly presents their own Opal consent " +
            "without claiming an attempt, charging anyone, or launching");
    }

    private static async Task CheckAtlantisLeaderWaitsForPayerConsentAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId,
            consentFollowers: false);
        var destination = ResolveAtlantisOpalDestination();
        var captureStatus = fixture.Leader.Registry
            .TryCaptureLegacyInstanceParty(
                fixture.Leader.Session,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                out var party);
        var leaderConsentStatus = fixture.Leader.Registry
            .TryConsumeLegacyInstanceOpalRetryConsents(
                party,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                new HashSet<int> { fixture.Leader.Character.Id },
                DateTimeOffset.UtcNow,
                out var missingLeaderConsent);

        Check.True(
            ClaimWasFullyReleased(daily) &&
            payments.Charges.Count == 0 &&
            payments.Settlements.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol
                        .AtlantisOpalConsentMissingResultSubId)) &&
            captureStatus == LegacyInstanceEntryStatus.Ready &&
            leaderConsentStatus ==
                LegacyInstanceOpalConsentValidationStatus.Ready &&
            missingLeaderConsent.Length == 0,
            "the leader cannot charge returning members who did not " +
            "explicitly present their own Opals, while already accumulated " +
            "consent remains ready for the retry");
    }

    private static async Task
        CheckAtlantisSuccessfulConsentConsumesPartySnapshotAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        foreach (var follower in fixture.Followers)
        {
            await ConsentAtlantisRetryAsync(follower);
        }

        var destination = ResolveAtlantisOpalDestination();
        var recordStatus = fixture.Leader.Registry
            .TryRecordLegacyInstanceOpalRetryConsent(
                fixture.Leader.Session,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                DateTimeOffset.UtcNow,
                out var party,
                out var requesterIsLeader);
        var firstPayer = new HashSet<int>
        {
            fixture.Followers[0].Character.Id
        };
        var firstStatus = fixture.Leader.Registry
            .TryConsumeLegacyInstanceOpalRetryConsents(
                party,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                firstPayer,
                DateTimeOffset.UtcNow,
                out var firstMissing);

        var formerlyFreePayer = new HashSet<int>
        {
            fixture.Followers[1].Character.Id
        };
        var secondStatus = fixture.Leader.Registry
            .TryConsumeLegacyInstanceOpalRetryConsents(
                party,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                formerlyFreePayer,
                DateTimeOffset.UtcNow,
                out var secondMissing);

        Check.True(
            recordStatus == LegacyInstanceEntryStatus.Ready &&
            requesterIsLeader &&
            firstStatus == LegacyInstanceOpalConsentValidationStatus.Ready &&
            firstMissing.Length == 0 &&
            secondStatus ==
                LegacyInstanceOpalConsentValidationStatus.MissingConsent &&
            secondMissing.SequenceEqual(formerlyFreePayer),
            "a member who was free on one consumed launch cannot reuse " +
            "that launch's stale consent after becoming a payer");
    }

    private static InstanceCallerEntryDestination
        ResolveAtlantisOpalDestination()
    {
        var arguments = Enumerable.Repeat(
            -1,
            InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = InstanceCallerProtocol.AtlantisOpalSubId;
        return InstanceCallerProtocol.TryResolveEntry(
            InstanceCallerProtocol.DialogIndex,
            InstanceCallerProtocol.AtlantisRootSubId,
            arguments,
            out var destination)
                ? destination
                : throw new InvalidOperationException(
                    "The stock Atlantis Opal destination did not resolve.");
    }

    private static async Task
        CheckAtlantisPartyChangeBeforeTransferRefundsAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var chargeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCharge = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var payments = new ScriptedLegacyInstanceOpalPaymentStore
        {
            BeforeChargeCompletesAsync = async _ =>
            {
                chargeEntered.TrySetResult();
                await finishCharge.Task;
            }
        };
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);
        foreach (var follower in fixture.Followers)
        {
            await ConsentAtlantisRetryAsync(follower);
        }
        await OpenAtlantisPageAsync(fixture.Leader);
        var before = fixture.Leader.ReadPackets().Count;

        var launch = InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisOpalSubId));
        await chargeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var leaving = fixture.Leader.Registry.LeaveParty(
            fixture.Followers[1].Session,
            fixture.Followers[1].Character.Name);
        finishCharge.TrySetResult();
        await launch;
        var emitted = fixture.Leader.ReadPackets().Skip(before).ToArray();

        Check.True(
            leaving.Status == PartyOperationStatus.Applied &&
            payments.Charges.Count == 1 &&
            payments.Settlements is [var settlement] &&
            settlement.AdmittedCharacterIds.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Any(packet => packet.SequenceEqual(
                PacketBuilder.ServerNote(
                    "The instance is temporarily unavailable."))),
            "an exact-party change during the durable charge is detected " +
            "before transfer and refunds every charged member");
    }

    private static async Task
        CheckAtlantisFollowerPartyChangeIsRevalidatedAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);
        foreach (var follower in fixture.Followers)
        {
            await ConsentAtlantisRetryAsync(follower);
        }

        var first = fixture.Followers[0];
        var second = fixture.Followers[1];
        fixture.Leader.Registry
            .UnregisterAuthoritativeInstanceTransitionSink(first.Session);
        fixture.Leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
            first.Session,
            async (command, cancellationToken) =>
            {
                var leaving = fixture.Leader.Registry.LeaveParty(
                    second.Session,
                    second.Character.Name);
                Check.True(
                    leaving.Status == PartyOperationStatus.Applied,
                    "second follower leaves during first follower transfer");
                return await InvokeAuthoritativeTransitionAsync(
                    first.Handler,
                    command,
                    cancellationToken);
            });

        await OpenAtlantisPageAsync(fixture.Leader);
        // The application publishes the leader's own Enter window to every
        // admitted member; the members answer it before the leader commits.
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisOpalSubId),
            confirmEntry: false);
        foreach (var follower in fixture.Followers)
        {
            await InvokeAsync(
                follower.Handler,
                CreateRepetitionResponse(
                    InstanceCallerProtocol.AtlantisClientSceneId,
                    0,
                    true),
                confirmEntry: false);
        }
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.AtlantisClientSceneId,
                0,
                true),
            confirmEntry: false);

        var admitted = new HashSet<int>
        {
            fixture.Characters[0].Id,
            fixture.Characters[1].Id
        };
        Check.True(
            payments.Charges.Count == 1 &&
            payments.Settlements is [var settlement] &&
            settlement.AdmittedCharacterIds.SetEquals(admitted) &&
            fixture.Characters[0].CurrentMap == 205 &&
            fixture.Characters[1].CurrentMap == 205 &&
            fixture.Characters[2].CurrentMap == fixture.Leader.SourceMapId &&
            fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                second.Session,
                out var secondInstance) &&
            secondInstance == sourceInstance,
            "each follower is revalidated against the exact party and " +
            "source immediately before transfer");
    }
}
