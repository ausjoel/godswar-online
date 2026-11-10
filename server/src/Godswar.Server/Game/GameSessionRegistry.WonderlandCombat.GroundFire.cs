using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Dedicated native589 clones Fire Blast580's exact effects with Target=17
    // for ground coordinates. Ordinary580 targeting and damage are unchanged.
    internal const uint WonderlandGroundFireVisualSkillId = 589;

    private Task PublishWonderlandGroundFireVisualAsync(WorldInstanceRuntime runtime,
        GameSessionContext viewer, MonsterRuntimeSnapshot source, float x, float z, bool impact,
        CancellationToken cancellationToken)
    {
        // Capture life synchronously before the caller releases its registry
        // lock; a new life must never inherit a queued old-life publication.
        if (!_playerLifeRevisions.TryGetValue(viewer.Session, out var life)) return Task.CompletedTask;
        return PublishWonderlandGroundFireVisualCoreAsync(runtime, viewer, life, source, x, z, impact,
            cancellationToken);
    }

    private async Task PublishWonderlandGroundFireVisualCoreAsync(WorldInstanceRuntime runtime,
        GameSessionContext viewer, long life, MonsterRuntimeSnapshot source, float x, float z, bool impact,
        CancellationToken cancellationToken)
    {
        await using var lease = await runtime.Map.AcquireWonderlandFireSourceLeaseAsync(
            viewer.Session, source, cancellationToken);
        if (lease is null) return;
        Task write;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || runtime.MapId != 207 ||
                !IsCurrentWonderlandPlayerLocked(viewer, life) || viewer.WorldInstanceId != runtime.InstanceId ||
                !WonderlandTerrainPolicy.GetIsland(6).Bounds.Contains(
                    viewer.Character.PositionX, viewer.Character.PositionZ) ||
                !runtime.Map.TryGetWonderlandSnapshot(out var run) || run.State != WonderlandRunState.Active ||
                !IsWonderlandPublishedStage(run, 6) ||
                !run.Participants.Any(member => member.CharacterId == viewer.CharacterId) ||
                !runtime.Map.TryGetMonsterSnapshot(source.ObjectId, out var current) ||
                current.RuntimeInstanceId != source.RuntimeInstanceId ||
                current.SpawnGeneration != source.SpawnGeneration || !current.IsAlive || !current.IsSpawned ||
                !runtime.Map.TryGetWonderlandSpawnPolicy(current.ObjectId, out var policy) ||
                policy.Stage != 6 || policy.MechanicKey != "platinum") return;
            var packets = new List<ReadOnlyMemory<byte>>();
            foreach (var appearance in lease.ReconciliationMonsters)
            {
                packets.Add(PacketBuilder.CapturedMonsterAppearance(appearance.Appearance));
                if (appearance.IsMoving)
                    packets.Add(PacketBuilder.MonsterMovementStart(appearance.ObjectId,
                        appearance.X, appearance.Y, appearance.Z,
                        appearance.VelocityX, appearance.VelocityY, appearance.VelocityZ));
            }
            packets.Add(impact
                ? PacketBuilder.SkillCastImpact(source.ObjectId, 0, WonderlandGroundFireVisualSkillId, x, z)
                : PacketBuilder.MonsterSkillCastVisual(source.ObjectId, 0, WonderlandGroundFireVisualSkillId,
                    current.X, current.Z, x, z));
            // The visibility gate keeps an AOI removal from separating source
            // hydration and the effect. Membership and egress own one batch.
            var outcome = runtime.Map.TryAdmitMonsterCastStart(viewer, viewer, packets,
                cancellationToken, out write);
            if (!WasMonsterCastStartOwned(outcome))
            {
                if (outcome == MonsterCastStartAdmissionOutcome.AdmissionFailed) viewer.Session.Disconnect();
                return;
            }
            lease.Commit();
            if (outcome == MonsterCastStartAdmissionOutcome.AdmittedTerminal) viewer.Session.Disconnect();
        }
        // Egress now owns the ordered batch; do not retain a visibility lease
        // while waiting for a slow transport to finish writing it.
        lease.Release();
        try { await write; }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            viewer.Session.Disconnect();
        }
    }
}
