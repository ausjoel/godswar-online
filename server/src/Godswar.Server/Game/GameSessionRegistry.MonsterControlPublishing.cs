using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task PublishMonsterControlsAsync(ClientSession source, MonsterRuntimeSnapshot expected,
        CancellationToken cancellationToken)
    {
        WorldInstanceRuntime runtime;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(source, out var context) ||
                !TryGetWorldInstance(context, out runtime!) || runtime.MapId != expected.Definition.MapId) return;
        }
        await PublishMonsterControlsAsync(runtime, expected, cancellationToken);
    }

    private async Task PublishMonsterControlsAsync(WorldInstanceRuntime runtime, MonsterRuntimeSnapshot expected,
        CancellationToken cancellationToken, DateTimeOffset? authoritativeNow = null)
    {
        GameSessionContext[] viewers;
        lock (_gate)
            viewers = _sessions.Values.Where(viewer => viewer.WorldReady &&
                viewer.WorldInstanceId == runtime.InstanceId).ToArray();
        foreach (var viewer in viewers)
        {
            try
            {
                await using var lease = await runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(viewer.Session,
                    expected.ObjectId, expected.SpawnGeneration, cancellationToken);
                if (lease is null) continue;
                await SendMonsterControlAppearancesAsync(viewer.Session, [expected], cancellationToken,
                    includeEmpty: true, authoritativeNow);
                lease.Commit();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { Remove(viewer.Session); }
        }
    }

    // The caller holds the viewer transition/delivery lease. Resolve controls
    // AFTER that wait, and admit their latest complete list under the same
    // registry gate that commits controls. An older publication therefore
    // cannot overwrite a newer source or hydrate an obsolete generation.
    internal async Task SendMonsterControlAppearancesAsync(ClientSession session,
        IReadOnlyList<MonsterRuntimeSnapshot> appearances, CancellationToken cancellationToken, bool includeEmpty = false,
        DateTimeOffset? authoritativeNow = null)
    {
        if (appearances.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        var sends = new List<Task>();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
                !IsCurrentAccountSession(context.AccountId, session, context.Ownership) ||
                !TryGetWorldInstance(context, out var runtime)) return;
            foreach (var expected in appearances)
            {
                var now = ResolveMonsterControlTimeLocked(runtime, authoritativeNow ?? DateTimeOffset.UtcNow);
                var current = InvokeWorldOwner(runtime, map =>
                    map.TryGetMonsterSnapshot(expected.ObjectId, out var actor) ? actor : null);
                if (current is null || !current.IsSpawned ||
                    current.RuntimeInstanceId != expected.RuntimeInstanceId ||
                    current.SpawnGeneration != expected.SpawnGeneration) continue;
                ScheduleMonsterControlExpiryLocked(runtime, current, now);
                var effects = current.IsAlive
                    ? current.Controls.Active(now).Select(entry => new ClientStatusEffect(entry.Definition.StatusId,
                        checked((uint)Math.Max(1, Math.Ceiling((entry.ExpiresAt - now).TotalSeconds))))).ToList()
                    : [];
                if (current.IsAlive && current.StunnedUntil is { } stunned && stunned > now &&
                    effects.All(effect => effect.StatusId != MonsterStunSkillCatalog.StunnedStatusId))
                    effects.Add(new(MonsterStunSkillCatalog.StunnedStatusId,
                        checked((uint)Math.Max(1, Math.Ceiling((stunned - now).TotalSeconds)))));
                if (!includeEmpty && effects.Count == 0 && current.IsAlive) continue;
                ReadOnlyMemory<byte>[] packets = [PacketBuilder.WorldObjectStatusEffects(current.ObjectId, effects.ToArray())];
                if (!session.TryAdmitExactBatch(packets, out var sent)) session.Disconnect();
                sends.Add(sent);
            }
        }
        await Task.WhenAll(sends);
    }
}
