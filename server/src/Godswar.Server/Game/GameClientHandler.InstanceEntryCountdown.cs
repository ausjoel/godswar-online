using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal static readonly TimeSpan InstanceEntryCountdownDuration = TimeSpan.FromSeconds(60);
    internal TimeProvider InstanceEntryClock { private get; set; } = TimeProvider.System;
    private PendingInstanceEntry? _pendingInstanceEntry;
    private Task _instanceEntryCountdownTask = Task.CompletedTask;
    private readonly object _instanceEntryStopGate = new();
    private Task? _instanceEntryStopTask;
    private bool _instanceEntryCountdownStopped;

    private sealed record PendingInstanceEntry(
        uint NpcId, int DialogIndex, int ClientSceneId,
        LegacyInstanceEntryPreparation? Legacy,
        InstanceCallerEntryDestination? Destination,
        MedusaInstancePartySnapshot? MedusaParty,
        MedusaEncounterDifficulty Difficulty, int TargetMapId)
    {
        public CancellationTokenSource TimerCancellation { get; } = new();
    }

    private async Task BeginLegacyInstanceEntryCountdownAsync(uint npcId, int dialogIndex,
        InstanceCallerEntryDestination destination, CancellationToken cancellationToken)
    {
        if (_pendingInstanceEntry is not null || _instanceEntryCountdownStopped)
            return;
        // The client keys its queue state and Enter window by the destination's
        // own repetition scene, which the destination carries: 224 Atlantis,
        // 227 Wonderland, and 229/230 for the two 港湾遇袭 maps.
        if (destination.ClientSceneId <= 0)
        {
            Console.Error.WriteLine(
                "[instance-caller] destination has no client scene " +
                $"destination={destination.Kind}");
            return;
        }
        var sceneId = destination.ClientSceneId;
        var prepared = await TryPrepareLegacyInstanceEntryAsync(
            npcId, dialogIndex, destination, cancellationToken);
        if (prepared is null)
            return;
        if (!await PublishInstanceEntryCountdownAsync(new(npcId, dialogIndex, sceneId,
                prepared, destination, null, default, destination.TargetMapId), cancellationToken))
        {
            return;
        }

        // A per-member instance gives every admitted member the same native Enter
        // window the leader just received. A member who does not confirm inside
        // their own sixty seconds is left behind, and never spends a daily
        // attempt.
        if (InstanceCallerProtocol.UsesPerMemberEntryWindow(destination.Kind))
        {
            await _registry.PublishMemberEntryWindowsAsync(
                _session,
                prepared.Party,
                sceneId,
                destination.Kind,
                cancellationToken);
        }
    }

    private async Task BeginMedusaEntryCountdownAsync(uint npcId, int dialogIndex,
        MedusaInstancePartySnapshot party, MedusaEncounterDifficulty difficulty,
        int targetMapId, CancellationToken cancellationToken)
    {
        if (_pendingInstanceEntry is not null || _instanceEntryCountdownStopped)
            return;
        if (!MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                checked((short)targetMapId), out var sceneId))
            return;
        await PublishInstanceEntryCountdownAsync(new(npcId, dialogIndex, sceneId,
            null, null, party, difficulty, targetMapId), cancellationToken);
    }

    private async Task<bool> PublishInstanceEntryCountdownAsync(PendingInstanceEntry pending,
        CancellationToken cancellationToken)
    {
        // At most one outstanding zero-token notice. A repeated NPC choice
        // cannot replace its authority or extend its deadline.
        if (_pendingInstanceEntry is not null || _instanceEntryCountdownStopped || _session.IsDisconnected)
        {
            pending.TimerCancellation.Dispose();
            return false;
        }
        _pendingInstanceEntry = pending;
        try
        {
            await _session.SendAsync(PacketBuilder.InstanceEntryQueueState(pending.ClientSceneId),
                cancellationToken, "InstanceEntryQueued");
            await _session.SendAsync(PacketBuilder.InstanceEntryNotice(pending.ClientSceneId),
                cancellationToken, "InstanceEntryCountdown");
            _instanceEntryCountdownTask = RunInstanceEntryCountdownAsync(pending, cancellationToken);
            return true;
        }
        catch
        {
            _pendingInstanceEntry = null;
            ClearPendingInstanceEntryConsent(pending);
            pending.TimerCancellation.Dispose();
            throw;
        }
    }

    private async Task RunInstanceEntryCountdownAsync(PendingInstanceEntry pending,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, pending.TimerCancellation.Token);
        try
        {
            await Task.Delay(InstanceEntryCountdownDuration, InstanceEntryClock, lifetime.Token);
            await _characterStateGate.WaitAsync(lifetime.Token);
            try
            {
                if (!_instanceEntryCountdownStopped && ReferenceEquals(_pendingInstanceEntry, pending))
                    await CompleteInstanceEntryCountdownAsync(pending, accepted: true, cancellationToken);
            }
            finally
            {
                _characterStateGate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[instance-caller] countdown admission failed: {error.Message}");
        }
        finally
        {
            await _characterStateGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_pendingInstanceEntry, pending))
                {
                    _pendingInstanceEntry = null;
                    ClearPendingInstanceEntryConsent(pending);
                }
                pending.TimerCancellation.Dispose();
            }
            finally { _characterStateGate.Release(); }
        }
    }

    private async Task<bool> TryHandleInstanceEntryResponseAsync(GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode != Opcodes.RepetitionResponse || packet.Length != 16 ||
            packet.Buffer.Length != 16 || BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.Slice(4)) != 0)
            return false;
        var response = BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.Slice(8));
        if (response is 0 or 1 && _pendingInstanceEntry is { } pending &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.Payload) == pending.ClientSceneId)
            await CompleteInstanceEntryCountdownAsync(pending, response == 1, cancellationToken);
        // Zero-token panel traffic is never a positive-ID Medusa invitation.
        return true;
    }

    private async Task CompleteInstanceEntryCountdownAsync(PendingInstanceEntry pending,
        bool accepted, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_pendingInstanceEntry, pending))
            return;
        _pendingInstanceEntry = null;
        pending.TimerCancellation.Cancel();
        if (_instanceEntryCountdownStopped || _session.IsDisconnected)
        {
            ClearPendingInstanceEntryConsent(pending);
            return;
        }
        if (!accepted)
        {
            ClearPendingInstanceEntryConsent(pending);
            _registry.CloseMemberEntryWindow(_session);
            await _session.SendAsync(PacketBuilder.InstanceEntryQueueState(pending.ClientSceneId, 1),
                cancellationToken, "InstanceEntryCanceled");
            return;
        }
        try
        {
            await ActivatePendingInstanceEntryAsync(pending, cancellationToken);
        }
        catch (Exception error)
        {
            ClearPendingInstanceEntryConsent(pending);
            _registry.CloseMemberEntryWindow(_session);
            Console.Error.WriteLine("[instance-caller] entry activation fault " +
                $"character={_character?.Name ?? "<none>"} scene={pending.ClientSceneId} " +
                $"reason=ActivationFault exception={error.GetType().Name}");
            if (!_session.IsDisconnected && !cancellationToken.IsCancellationRequested)
                await RejectPendingInstanceEntryAsync(pending, "ActivationFault",
                    "The instance is temporarily unavailable. Please try again shortly.", CancellationToken.None);
            throw;
        }
    }

    private Task StopInstanceEntryCountdownAsync()
    {
        // RunAsync owns semaphore disposal. A second cleanup caller must
        // await the original stop, never touch that semaphore again.
        lock (_instanceEntryStopGate)
            return _instanceEntryStopTask ??= StopInstanceEntryCountdownCoreAsync();
    }

    private async Task StopInstanceEntryCountdownCoreAsync()
    {
        await _characterStateGate.WaitAsync();
        try
        {
            _instanceEntryCountdownStopped = true;
            var pending = _pendingInstanceEntry;
            _pendingInstanceEntry = null;
            pending?.TimerCancellation.Cancel();
            if (pending is not null)
                ClearPendingInstanceEntryConsent(pending);
        }
        finally { _characterStateGate.Release(); }
        await _instanceEntryCountdownTask;
        // The initiator is gone, so the party's open windows have no run to join.
        _registry.CloseMemberEntryWindow(_session);
    }

    private void ClearPendingInstanceEntryConsent(PendingInstanceEntry pending)
    {
        if (pending.Destination?.PaymentMode == InstanceCallerEntryPaymentMode.OpalRetry &&
            pending.Legacy is { } legacy)
            _registry.ClearLegacyInstanceOpalRetryConsents(legacy.Party);
    }
}
