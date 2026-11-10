using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private enum LegacyEntryOutcome
    {
        Admitted, PreparationRejected, PreparationChanged, DailyClaimFailed, DailyLimitReached,
        PaymentModeChanged, PaymentStoreUnavailable, ConsentRejected,
        RuntimeCreationFailed, RuntimeCreationRejected, PartyChangedAfterCreation,
        OpalChargeFailed, OpalChargeRejected, OpalProjectionFailed,
        PartyChangedAfterPayment, EncounterOrTransferFailed
    }

    private readonly record struct LegacyEntryResult(LegacyEntryOutcome Outcome, ushort? DailyLimit = null);

    private string? PendingInstanceEntryAuthorityFailure(PendingInstanceEntry pending)
    {
        // INSTANCES: a switch closed after the Enter window opened still stops it.
        if (pending.Destination?.Kind == InstanceCallerEntryKind.HarborAttack &&
            !InstanceEntrySwitches.IsHarborAttackOpen) return "InstanceClosed";
        if (pending.Destination?.Kind == InstanceCallerEntryKind.TrainingGround &&
            !InstanceEntrySwitches.IsTrainingGroundOpen) return "TrainingGroundClosed"; // TRAINING
        if (pending.Destination?.Kind is { } warriorKind && WarriorTrainingGroundPolicy.IsWarriorKind(warriorKind) &&
            !InstanceEntrySwitches.IsWarriorTrainingOpen) return "WarriorTrainingClosed"; // WARRIOR (F-51)
        if (_character is null || _character.CurrentHp <= 0) return "CallerNotAlive";
        if (!TryResolveMapNpc(pending.NpcId, out var npc)) return "CallerNpcUnavailable";
        if (!IsWithinInstanceCallerInteractionDistance(npc)) return "CallerOutOfRange";
        var valid = pending.Legacy is { } legacy && pending.Destination is { } destination
            ? _registry.IsLegacyInstancePartySnapshotCurrent(legacy.Party, destination,
                legacy.Npc.X, legacy.Npc.Z, InstanceCallerProtocol.MaximumInteractionDistance)
            : pending.MedusaParty is { } party && _registry.IsMedusaPartySnapshotCurrent(party, _session);
        return valid ? null : "PartyOrSessionChanged";
    }

    private async Task RejectPendingInstanceEntryAsync(PendingInstanceEntry pending, string reason,
        string? message, CancellationToken cancellationToken)
    {
        var mayReset = IsPendingInstanceEntrySourceCurrent(pending);
        Console.WriteLine("[instance-caller] countdown rejected " +
            $"character={_character?.Name ?? "<none>"} scene={pending.ClientSceneId} " +
            $"npc={pending.NpcId} reason={reason} reset={mayReset}");
        if (!mayReset) return;
        // Origin 0x4BEE9F / 10231 clears the queue flags and Enter window
        // silently. 10222/result2 would add the misleading RepCheckErr popup.
        // This is only called after consuming this handler's pending entry.
        await _session.SendAsync(PacketBuilder.RepetitionReset(), cancellationToken, "InstanceEntryRejectedReset");
        if (message is not null)
            await _session.SendAsync(PacketBuilder.ServerNote(message), cancellationToken, "InstanceEntryRejectedReason");
    }

    private bool IsPendingInstanceEntrySourceCurrent(PendingInstanceEntry pending)
    {
        var legacy = pending.Legacy?.Party.Members.FirstOrDefault(member => ReferenceEquals(member.Session, _session));
        var medusa = pending.MedusaParty?.Members.FirstOrDefault(member => ReferenceEquals(member.Session, _session));
        var source = legacy?.SourceWorldInstanceId ?? medusa?.SourceWorldInstanceId;
        var account = legacy?.AccountId ?? medusa?.AccountId;
        var ownership = legacy?.Ownership ?? medusa?.Ownership;
        return source is { } expectedSource && account is { } expectedAccount && ownership is { } expectedOwnership &&
            !_session.IsDisconnected && _registry.IsCurrentAccountSession(expectedAccount, _session, expectedOwnership) &&
            _registry.TryGetSessionWorldInstanceId(_session, out var current) && current == expectedSource;
    }

    private static string EntryAuthorityMessage(string reason) => reason switch
    {
        "InstanceClosed" => InstanceEntrySwitches.HarborAttackClosedNote, // INSTANCES
        "TrainingGroundClosed" => InstanceEntrySwitches.TrainingGroundClosedNote, // TRAINING
        "WarriorTrainingClosed" => InstanceEntrySwitches.WarriorTrainingClosedNote, // WARRIOR (F-51)
        "CallerNotAlive" => "Revive before entering the instance, then speak to the Instance Caller again.",
        "CallerNpcUnavailable" or "CallerOutOfRange" =>
            "Stay within 12 units of the Instance Caller until you enter, then try again.",
        _ => "Your party or session changed. Gather the same party near the Instance Caller and try again."
    };

    private static string? LegacyEntryRejectionMessage(PendingInstanceEntry pending, LegacyEntryResult result)
    {
        if (result.Outcome == LegacyEntryOutcome.DailyLimitReached &&
            pending.Destination?.Kind == InstanceCallerEntryKind.Wonderland)
        {
            // FINALISE2 (F-22): one entry a day reads "your Wonderland entry", not "all 1 Wonderland entries".
            var limit = result.DailyLimit is 1 ? "your Wonderland entry"
                : result.DailyLimit is { } maximum ? $"all {maximum} Wonderland entries" : "all Wonderland entries";
            return pending.Legacy!.Party.Members.Count == 1
                ? $"You have used {limit} for today. Try again after the daily reset." // FINALISE2
                : $"A party member has used {(result.DailyLimit is 1 ? "their Wonderland entry" : limit)} for today. Everyone needs an available entry.";
        }
        return result.Outcome is LegacyEntryOutcome.PreparationChanged
            ? EntryAuthorityMessage("PartyOrSessionChanged") : null;
    }
}
