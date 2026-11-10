using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string LegacyInstanceUnavailableMessage =
        "The instance is temporarily unavailable.";

    private async Task<LegacyEntryResult> HandleLegacyInstanceEntryCoreAsync(
        uint npcId,
        int dialogIndex,
        InstanceCallerEntryDestination destination,
        LegacyInstanceEntryPreparation expectedPreparation,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var preparation = await TryPrepareLegacyInstanceEntryAsync(
            npcId,
            dialogIndex,
            destination,
            cancellationToken);
        if (preparation is null || !_registry.IsLegacyInstancePartySnapshotCurrent(
                expectedPreparation.Party, destination, preparation.Npc.X, preparation.Npc.Z,
                InstanceCallerProtocol.MaximumInteractionDistance))
        {
            if (destination.PaymentMode == InstanceCallerEntryPaymentMode.OpalRetry)
            {
                _registry.ClearLegacyInstanceOpalRetryConsents(expectedPreparation.Party);
                if (preparation is not null)
                    _registry.ClearLegacyInstanceOpalRetryConsents(preparation.Party);
            }
            return new(preparation is null ? LegacyEntryOutcome.PreparationRejected : LegacyEntryOutcome.PreparationChanged);
        }
        var npc = preparation.Npc;
        // Never replace countdown authority with the second preparation's
        // roster. Its schedule/consent checks are fresh; the party stays fixed.
        var party = expectedPreparation.Party;
        var paidIntent = destination.PaymentMode ==
            InstanceCallerEntryPaymentMode.OpalRetry;

        var startedAt = DateTimeOffset.UtcNow;
        LegacyInstanceDailyEntryClaimResult? claimResult;
        try
        {
            claimResult = await TryClaimLegacyInstanceDailyEntryAsync(
                reservationId,
                party.RealmId,
                _realmCalendar.GetDay(startedAt),
                destination.Kind,
                party.Members
                    .Select(static member => member.CharacterId)
                    .ToArray(),
                startedAt,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (paidIntent)
            {
                _registry.ClearLegacyInstanceOpalRetryConsents(party);
            }
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            throw;
        }
        if (claimResult is null)
        {
            if (paidIntent)
            {
                _registry.ClearLegacyInstanceOpalRetryConsents(party);
            }
            await SendLegacyInstanceUnavailableAsync(cancellationToken);
            return new(LegacyEntryOutcome.DailyClaimFailed);
        }
        if (claimResult.Status ==
            LegacyInstanceDailyEntryClaimStatus.AlreadyUsed)
        {
            if (paidIntent)
            {
                _registry.ClearLegacyInstanceOpalRetryConsents(party);
            }
            if (destination.Kind != InstanceCallerEntryKind.Wonderland)
                await SendLegacyInstanceDailyEntryUsedAsync(npcId, destination.Kind, cancellationToken);
            return new(LegacyEntryOutcome.DailyLimitReached, claimResult.DailyEntryLimit);
        }

        var paymentRequired = RequiresOpalPayment(claimResult);
        if (paymentRequired != paidIntent)
        {
            if (paidIntent)
            {
                _registry.ClearLegacyInstanceOpalRetryConsents(party);
            }
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            await SendAtlantisEntryPolicyAsync(
                npcId,
                cancellationToken);
            return new(LegacyEntryOutcome.PaymentModeChanged);
        }
        if (paymentRequired &&
            (_legacyInstanceDailyEntries is null ||
             _legacyInstanceOpalPayments is null))
        {
            _registry.ClearLegacyInstanceOpalRetryConsents(party);
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            await SendLegacyInstanceUnavailableAsync(cancellationToken);
            return new(LegacyEntryOutcome.PaymentStoreUnavailable);
        }
        if (paymentRequired)
        {
            var consentStatus = _registry
                .TryConsumeLegacyInstanceOpalRetryConsents(
                    party,
                    destination,
                    npc.X,
                    npc.Z,
                    InstanceCallerProtocol.MaximumInteractionDistance,
                    claimResult.PaymentRequiredCharacterIds,
                    DateTimeOffset.UtcNow,
                    out _);
            if (consentStatus !=
                LegacyInstanceOpalConsentValidationStatus.Ready)
            {
                await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
                if (consentStatus ==
                    LegacyInstanceOpalConsentValidationStatus
                        .MissingConsent)
                {
                    await SendAtlantisOpalConsentMissingAsync(
                        npcId,
                        cancellationToken);
                }
                else
                {
                    _registry.ClearLegacyInstanceOpalRetryConsents(party);
                    await SendLegacyInstanceUnavailableAsync(
                        cancellationToken);
                }
                return new(LegacyEntryOutcome.ConsentRejected);
            }
        }

        WorldInstanceRuntimeDirectoryResult creation;
        try
        {
            creation = await _registry.CreateLocalWorldInstanceAsync(
                party.RealmId,
                new WorldMapId(checked((short)destination.TargetMapId)),
                InstanceKind.Dungeon,
                // Room for a whole party, not just the characters who registered.
                // A member who confirms their own Enter window after the run was
                // sealed - or a member invited by name from inside it - is pulled
                // into this instance later, and an instance sized to the original
                // headcount refuses them as InstanceFull.
                Math.Max(
                    party.Members.Count,
                    Godswar.Server.Protocol.PartyProtocol.MaximumMembers),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            throw;
        }
        catch (Exception error)
        {
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] runtime creation failed " +
                $"destination={destination.DisplayName}: {error.Message}");
            await SendLegacyInstanceUnavailableAsync(
                cancellationToken);
            return new(LegacyEntryOutcome.RuntimeCreationFailed);
        }

        var target = creation.Runtime?.Descriptor;
        if (creation.Status != WorldInstanceRuntimeDirectoryStatus.Created ||
            target is null)
        {
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] runtime creation rejected " +
                $"destination={destination.DisplayName} " +
                $"status={creation.Status}/{creation.PlacementStatus}");
            await SendLegacyInstanceUnavailableAsync(
                cancellationToken);
            return new(LegacyEntryOutcome.RuntimeCreationRejected);
        }

        if (!_registry.IsLegacyInstancePartySnapshotCurrent(
                party,
                destination,
                npc.X,
                npc.Z,
                InstanceCallerProtocol.MaximumInteractionDistance))
        {
            await CleanupFailedLegacyInstanceLaunchAsync(
                target,
                reservationId);
            await SendLegacyInstanceUnavailableAsync(
                CancellationToken.None);
            return new(LegacyEntryOutcome.PartyChangedAfterCreation);
        }

        var opalsCharged = false;
        if (paymentRequired)
        {
            LegacyInstanceOpalChargeResult? charge;
            try
            {
                charge = await TryChargeLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    claimResult,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    Array.Empty<int>());
                await RetireFailedLegacyInstanceRuntimeAsync(target);
                throw;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "[instance-caller] Atlantis Opal charge failed " +
                    $"reservation={reservationId}: {error.Message}");
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    Array.Empty<int>());
                await RetireFailedLegacyInstanceRuntimeAsync(target);
                await SendLegacyInstanceUnavailableAsync(
                    CancellationToken.None);
                return new(LegacyEntryOutcome.OpalChargeFailed);
            }

            if (charge is null ||
                charge.Status != LegacyInstanceOpalChargeStatus.Charged)
            {
                await CleanupFailedLegacyInstanceLaunchAsync(
                    target,
                    reservationId);
                if (charge?.Status ==
                    LegacyInstanceOpalChargeStatus.InsufficientOpal)
                {
                    await SendAtlantisInsufficientOpalAsync(
                        npcId,
                        CancellationToken.None);
                }
                else
                {
                    await SendLegacyInstanceUnavailableAsync(
                        CancellationToken.None);
                }
                return new(LegacyEntryOutcome.OpalChargeRejected);
            }

            opalsCharged = true;
            var chargeProjected = false;
            try
            {
                chargeProjected =
                    await ProjectLegacyInstanceOpalMutationsAsync(
                        party,
                        charge.Mutations);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "[instance-caller] Atlantis Opal charge projection " +
                    $"failed reservation={reservationId}: {error.Message}");
            }
            if (!chargeProjected)
            {
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    Array.Empty<int>());
                await RetireFailedLegacyInstanceRuntimeAsync(target);
                await SendLegacyInstanceUnavailableAsync(
                    CancellationToken.None);
                return new(LegacyEntryOutcome.OpalProjectionFailed);
            }
        }

        if (!_registry.IsLegacyInstancePartySnapshotCurrent(
                party,
                destination,
                npc.X,
                npc.Z,
                InstanceCallerProtocol.MaximumInteractionDistance))
        {
            if (opalsCharged)
            {
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    Array.Empty<int>());
                await RetireFailedLegacyInstanceRuntimeAsync(target);
            }
            else
            {
                await CleanupFailedLegacyInstanceLaunchAsync(
                    target,
                    reservationId);
            }
            await SendLegacyInstanceUnavailableAsync(
                CancellationToken.None);
            return new(LegacyEntryOutcome.PartyChangedAfterPayment);
        }

        var leader = party.Members[0];
        var leaderCommand = BuildLegacyInstanceTransitionCommand(
            leader,
            target.InstanceId,
            destination);
        bool leaderMoved;
        try
        {
            leaderMoved = TryStartLegacyInstanceEncounter(destination, target.InstanceId,
                    // The daily limit the client's window is told about. A null
                    // limit means "no limit" (Wonderland's current setting), and
                    // the window has only a ushort to show, so the maximum stands
                    // in for unlimited.
                    claimResult.DailyEntryLimit ?? ushort.MaxValue, party, reservationId) &&
                await TryBeginAuthoritativeInstanceTransitionAsync(
                    leaderCommand,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            var leaderEntered = _registry.IsSessionInWorldInstance(
                _session,
                target.InstanceId);
            if (leaderEntered)
            {
                _ = await RecordLegacyInstanceAdmissionsAsync(
                    reservationId,
                    new[] { leader.CharacterId });
            }
            if (opalsCharged)
            {
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    leaderEntered
                        ? new[] { leader.CharacterId }
                        : Array.Empty<int>());
                if (!leaderEntered)
                {
                    await RetireFailedLegacyInstanceRuntimeAsync(target);
                }
            }
            else if (!leaderEntered)
            {
                await CleanupFailedLegacyInstanceLaunchAsync(
                    target,
                    reservationId);
            }
            else
            {
                await ReleaseLegacyInstanceDailyEntryMembersAsync(
                    reservationId,
                    party.Members
                        .Skip(1)
                        .Select(static member => member.CharacterId)
                        .ToArray());
            }
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] leader transfer fault " +
                $"destination={destination.DisplayName}: {error.Message}");
            leaderMoved = false;
        }

        if (!leaderMoved)
        {
            var leaderEntered = _registry.IsSessionInWorldInstance(
                _session,
                target.InstanceId);
            if (leaderEntered)
            {
                _ = await RecordLegacyInstanceAdmissionsAsync(
                    reservationId,
                    new[] { leader.CharacterId });
            }
            if (opalsCharged)
            {
                _ = await SettleLegacyInstanceOpalsAsync(
                    reservationId,
                    party,
                    leaderEntered
                        ? new[] { leader.CharacterId }
                        : Array.Empty<int>());
                if (!leaderEntered)
                {
                    await RetireFailedLegacyInstanceRuntimeAsync(target);
                }
            }
            else if (!leaderEntered)
            {
                await CleanupFailedLegacyInstanceLaunchAsync(
                    target,
                    reservationId);
            }
            else
            {
                await ReleaseLegacyInstanceDailyEntryMembersAsync(
                    reservationId,
                    party.Members
                        .Skip(1)
                        .Select(static member => member.CharacterId)
                        .ToArray());
                Console.Error.WriteLine(
                    "[instance-caller] leader entered before transfer " +
                    $"publication failed instance={target.InstanceId}; " +
                    "leader claim retained and member claims released");
            }
            await SendLegacyInstanceUnavailableAsync(
                CancellationToken.None);
            return new(LegacyEntryOutcome.EncounterOrTransferFailed);
        }

        _ = await RecordLegacyInstanceAdmissionsAsync(
            reservationId,
            new[] { leader.CharacterId });

        // A per-member instance admits only the members who confirmed the same
        // window the leader confirmed; a member who never confirmed keeps the
        // daily attempt they never spent.
        IReadOnlySet<int>? confirmedFollowers = null;
        if (InstanceCallerProtocol.UsesPerMemberEntryWindow(destination.Kind))
        {
            confirmedFollowers =
                _registry.ConfirmedMemberEntryMembers(_session);
        }
        await TransferLegacyInstanceFollowersAsync(party, destination, npc, target, reservationId,
            opalsCharged, confirmedFollowers);
        return new(LegacyEntryOutcome.Admitted);
    }

}
