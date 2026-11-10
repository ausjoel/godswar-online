using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;
internal sealed partial class GameClientHandler
{
    private async Task ActivatePendingInstanceEntryAsync(PendingInstanceEntry pending,
        CancellationToken cancellationToken)
    {
        if (PendingInstanceEntryAuthorityFailure(pending) is { } authorityFailure)
        {
            ClearPendingInstanceEntryConsent(pending);
            await RejectPendingInstanceEntryAsync(pending, authorityFailure,
                EntryAuthorityMessage(authorityFailure), cancellationToken);
            return;
        }

        // These are the original durable admission paths: they re-read the
        // schedule/day, claim limits, settle explicit payment consent and
        // revalidate ownership before transferring any admitted participant.
        if (pending.Destination is { } legacyDestination)
        {
            var result = await HandleLegacyInstanceEntryAsync(pending.NpcId, pending.DialogIndex,
                legacyDestination, pending.Legacy!, cancellationToken);
            // A per-member instance published the party's own windows before this
            // point. They stay open for their own sixty seconds so every member
            // can still confirm and join the committed run; only a leader who
            // never entered leaves them nothing to join.
            var hasCommittedInstance = _registry.TryGetSessionWorldInstanceId(
                _session,
                out var committedInstance);
            var sourceInstanceId =
                pending.Legacy!.Party.Members[0].SourceWorldInstanceId;
            if (InstanceCallerProtocol.UsesPerMemberEntryWindow(
                    legacyDestination.Kind))
            {
                if (!_session.IsDisconnected &&
                    hasCommittedInstance &&
                    committedInstance != sourceInstanceId)
                {
                    _registry.BindMemberEntryRun(
                        _session,
                        committedInstance,
                        legacyDestination.TargetMapId,
                        legacyDestination.TargetX,
                        legacyDestination.TargetZ);
                }
                else
                {
                    await _registry.CloseMemberEntryWindowsAsync(_session);
                }
            }
            if (!_session.IsDisconnected &&
                hasCommittedInstance &&
                committedInstance == sourceInstanceId)
                await RejectPendingInstanceEntryAsync(pending, result.Outcome.ToString(),
                    LegacyEntryRejectionMessage(pending, result), cancellationToken);
            return;
        }
        var status = await BeginMedusaLeaderEntryAsync(pending.MedusaParty!, pending.Difficulty,
            pending.TargetMapId, cancellationToken);
        if (status != MedusaPartyEntryStatus.Ready)
        {
            await RejectPendingInstanceEntryAsync(pending, $"Medusa:{status}", null, cancellationToken);
            await SendInstanceCallerFailureAsync(pending.NpcId, pending.DialogIndex, status, cancellationToken);
        }
    }
}
