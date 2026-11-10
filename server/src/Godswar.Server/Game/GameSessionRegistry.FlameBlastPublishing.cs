using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal PveLifeAbsorptionCommit CommitFlameBlastLifeAbsorption(
        FlameBlastSource source, PveLifeAbsorptionCommitter committer,
        IReadOnlyList<PveCommittedMonsterDamage> hits, int healingReceivedBasisPoints)
    {
        lock (_gate)
        {
            if (!TryResolveFlameBlastSourceLocked(source, out _)) return default;
            // Reentrant registry locking keeps frozen source validation and
            // the ordinary HP mutation in one life/membership transaction.
            return CommitPveLifeAbsorption(source.Context.Session, source.Context.Character,
                committer, hits, healingReceivedBasisPoints);
        }
    }

    internal async Task PublishFlameBlastPulseDamageAsync(
        FlameBlastSource source, MonsterDamageResult result, CombatResolution resolution,
        CancellationToken cancellationToken)
    {
        WorldInstanceRuntime runtime;
        lock (_gate)
        {
            if (result.HealthMutation is not { } ||
                !TryResolveFlameBlastSourceLocked(source, out _) ||
                !WorldInstances.TryFind(source.Context.WorldInstanceId, out runtime!) ||
                result.Monster.Definition.MapId != runtime.MapId) return;
        }
        var members = SnapshotReadySessions(runtime, excludeSession: null);
        var recipients = CaptureMonsterAttackPublicationRecipients(runtime, members);
        foreach (var recipient in recipients)
        {
            try
            {
                await PublishFlameBlastPulseToViewerAsync(runtime, source, recipient,
                    result, resolution, cancellationToken);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                Remove(recipient.Context.Session);
            }
        }
    }

    private async Task PublishFlameBlastPulseToViewerAsync(
        WorldInstanceRuntime runtime, FlameBlastSource source,
        MonsterAttackPublicationRecipient recipient, MonsterDamageResult result,
        CombatResolution resolution, CancellationToken cancellationToken)
    {
        await using var lease = await runtime.Map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
            recipient.Context.Session, [result.HealthMutation!.Value], cancellationToken);
        if (lease is null) return;
        var packets = new List<ReadOnlyMemory<byte>>();
        // The damage number has to be admitted while the viewer still has the
        // monster. A lethal pass retires the object, and a removal (or respawn)
        // that arrives first makes the client drop the number for the very hit
        // that killed it, which is what the single-target path already avoids.
        var caster = ReferenceEquals(recipient.Context.Session, source.Context.Session)
            ? LocalPlayerObjectId : source.Context.ObjectId;
        // Recurring Flame Blast retains generic skill250 without recasting.
        // Its low result byte selects the actual normal/critical number font.
        packets.Add(PacketBuilder.SkillDamage(caster, result.ObjectId, (byte)resolution.Outcome,
            resolution.CapturedDamageValue, skillId: 250, result.Monster.X, result.Monster.Z));
        if (lease.ReconciliationObjectIds.Count > 0)
        {
            packets.Add(PacketBuilder.RemoveWorldObjects(lease.ReconciliationObjectIds.ToArray()));
            if (lease.ReconciliationMonsters.Count > 0)
                packets.Add(PacketBuilder.CapturedMonsterSpawns(
                    lease.ReconciliationMonsters.Select(monster => monster.Appearance).ToArray()));
            foreach (var monster in lease.ReconciliationMonsters.Where(monster => monster.IsMoving))
                packets.Add(PacketBuilder.MonsterMovementStart(monster.ObjectId,
                    monster.X, monster.Y, monster.Z, monster.VelocityX, monster.VelocityY, monster.VelocityZ));
        }
        var bytes = new byte[packets.Sum(packet => packet.Length)];
        var offset = 0;
        foreach (var packet in packets)
        {
            packet.Span.CopyTo(bytes.AsSpan(offset));
            offset += packet.Length;
        }
        Task completion = Task.CompletedTask;
        ClientSession? claimedDisconnect = null;
        var admitted = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryResolveFlameBlastSourceLocked(source, out _) ||
                source.Context.WorldInstanceId != runtime.InstanceId ||
                !TryResolveExactMedusaPublicationContextLocked(runtime, recipient.Context,
                    recipient.LifeRevision, out var viewer)) return;
            var outcome = viewer.Session.TryAdmitExactOutcome(bytes, out completion);
            admitted = outcome is ExactEgressAdmissionOutcome.Admitted or ExactEgressAdmissionOutcome.AdmittedTerminal;
            if (admitted) lease.Commit();
            if (outcome != ExactEgressAdmissionOutcome.Admitted && viewer.Session.TryClaimDisconnect())
                claimedDisconnect = viewer.Session;
        }
        try
        {
            if (admitted && claimedDisconnect is null && lease.ReconciliationMonsters.Count > 0)
            {
                // These existing helpers expect the viewer appearance lease
                // and refresh current corpse loot/control metadata after a
                // health reconciliation, rather than replaying old damage.
                await SendWonderlandBossCorpseAppearancesAsync(recipient.Context.Session,
                    lease.ReconciliationMonsters, cancellationToken);
                await SendMonsterControlAppearancesAsync(recipient.Context.Session,
                    lease.ReconciliationMonsters, cancellationToken);
            }
        }
        finally
        {
            lease.Release();
            if (claimedDisconnect is not null) CompleteClaimedExactStatusDisconnect(claimedDisconnect);
            if (admitted) ObserveExactAdmissionCompletion(recipient.Context.Session, completion, "FlameBlastPulse");
        }
    }
}
