using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private sealed record WonderlandAlliedHit(MonsterRuntimeSnapshot Source, uint SkillId,
        MonsterDamageResult Damage, IReadOnlyList<MonsterAttackPublicationRecipient> Recipients);

    private async Task PublishWonderlandAlliedHitAsync(WorldInstanceRuntime runtime,
        WonderlandAlliedHit hit, CancellationToken cancellationToken)
    {
        if (hit.Damage.HealthMutation is not { } mutation) return;
        foreach (var recipient in hit.Recipients)
        {
            try
            {
                await PublishWonderlandAlliedHitToViewerAsync(runtime, hit, mutation, recipient, cancellationToken);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                Remove(recipient.Context.Session);
            }
        }
    }

    private async Task PublishWonderlandAlliedHitToViewerAsync(WorldInstanceRuntime runtime,
        WonderlandAlliedHit hit, MonsterHealthMutation mutation, MonsterAttackPublicationRecipient recipient,
        CancellationToken cancellationToken)
    {
        // This is called only after the authoritative registry gate is released.
        // The target's health revision and any missing source appearance share
        // one viewer gate and one exact egress admission.
        await using var lease = await runtime.Map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
            recipient.Context.Session, [mutation], cancellationToken);
        if (lease is null) return;
        Task completion = Task.CompletedTask;
        ClientSession? claimedDisconnect = null;
        var admitted = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryResolveExactMedusaPublicationContextLocked(runtime, recipient.Context,
                    recipient.LifeRevision, out var viewer) ||
                !runtime.Map.TryGetWonderlandSnapshot(out var run) ||
                run.State is WonderlandRunState.Cancelled or WonderlandRunState.TimedOut ||
                !IsWonderlandPublishedStage(run, 5) ||
                !run.Participants.Any(member => member.CharacterId == viewer.CharacterId) ||
                !runtime.Map.TryGetMonsterSnapshot(hit.Source.ObjectId, out var source) ||
                source.RuntimeInstanceId != hit.Source.RuntimeInstanceId ||
                source.SpawnGeneration != hit.Source.SpawnGeneration || !source.IsSpawned ||
                !runtime.Map.TryGetMonsterSnapshot(hit.Damage.ObjectId, out var target) ||
                target.RuntimeInstanceId != hit.Damage.Monster.RuntimeInstanceId ||
                target.SpawnGeneration != mutation.SpawnGeneration ||
                target.HealthRevision < mutation.AfterHealthRevision) return;

            var packets = new List<ReadOnlyMemory<byte>>();
            if (lease.ReconciliationObjectIds.Count > 0)
            {
                // Preserve the ordinary repair for genuinely missed deltas.
                // Correctly sequenced NPC assistance now follows the direct path.
                Console.WriteLine("[monster] health delivery reconciliation " +
                    $"label=WonderlandAlliedDamage objects={string.Join(',', lease.ReconciliationObjectIds)}");
                packets.Add(PacketBuilder.RemoveWorldObjects(lease.ReconciliationObjectIds.ToArray()));
                foreach (var monster in lease.ReconciliationMonsters)
                    AddWonderlandAlliedAppearance(packets, monster);
            }
            else
            {
                lease.IncludeRequiredSourceAppearance(source);
                foreach (var appearance in lease.RequiredSourceAppearances)
                    AddWonderlandAlliedAppearance(packets, appearance);
                packets.Add(PacketBuilder.SkillCastImpact(source.ObjectId, target.ObjectId,
                    hit.SkillId, hit.Damage.Monster.X, hit.Damage.Monster.Z));
                packets.Add(PacketBuilder.SkillCastImpact(source.ObjectId, target.ObjectId,
                    2000, hit.Damage.Monster.X, hit.Damage.Monster.Z));
                packets.Add(PacketBuilder.PhysicalDamage(source.ObjectId, hit.Source.X, hit.Source.Y, hit.Source.Z,
                    target.ObjectId, hit.Damage.BeforeHealth - hit.Damage.AfterHealth,
                    1, 1, includeMonsterAnimationFlags: true));
            }
            var outcome = viewer.Session.TryAdmitExactBatchOutcome(packets, out completion);
            admitted = outcome is ExactEgressAdmissionOutcome.Admitted or ExactEgressAdmissionOutcome.AdmittedTerminal;
            if (admitted) lease.Commit();
            if (outcome != ExactEgressAdmissionOutcome.Admitted && viewer.Session.TryClaimDisconnect())
                claimedDisconnect = viewer.Session;
        }
        try
        {
            if (admitted && claimedDisconnect is null && lease.ReconciliationMonsters.Count > 0)
            {
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
            if (admitted) ObserveExactAdmissionCompletion(recipient.Context.Session, completion, "WonderlandAlliedDamage");
        }
    }

    private static void AddWonderlandAlliedAppearance(List<ReadOnlyMemory<byte>> packets,
        MonsterRuntimeSnapshot monster)
    {
        packets.Add(PacketBuilder.CapturedMonsterAppearance(monster.Appearance));
        if (monster.IsMoving)
            packets.Add(PacketBuilder.MonsterMovementStart(monster.ObjectId,
                monster.X, monster.Y, monster.Z, monster.VelocityX, monster.VelocityY, monster.VelocityZ));
    }
}
