using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Legacy byte-map bridge. If the excluded session is already in-world,
    /// its instance identity is the route; otherwise this resolves only the
    /// Tempest default open-world instance.
    /// </summary>
    /// <summary>
    /// Announces a packet to every session this process is serving, whatever map
    /// the recipient is on.
    /// </summary>
    /// <remarks>
    /// This is the realm-wide channel an announcement needs: a map or world-instance
    /// broadcast only reaches the sender's own map. A failure on one session is
    /// swallowed so a closing client cannot stop the announcement for the rest.
    /// </remarks>
    public async Task<int> BroadcastToAllSessionsAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        var delivered = 0;
        foreach (var session in _sessions.Keys.ToArray())
        {
            try
            {
                await session.SendAsync(
                    packet,
                    cancellationToken,
                    label,
                    framed);
                delivered++;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or ObjectDisposedException or
                    IOException or InvalidOperationException)
            {
                // A session that is closing must not abort the announcement.
            }
        }

        return delivered;
    }

    public Task<int> BroadcastToMapAsync(
        byte mapId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true,
        Func<GameSessionContext, byte[]>? perRecipient = null) // ITEMS-HERMES: per-viewer frames when set
    {
        if (perRecipient is not null)
        {
            return BroadcastToMapPerRecipientAsync(
                mapId,
                perRecipient,
                cancellationToken,
                excludeSession,
                label,
                framed);
        }

        return TryResolveWorldInstance(
            mapId,
            excludeSession,
            out var runtime)
            ? BroadcastToWorldInstanceCoreAsync(
                runtime,
                packet,
                cancellationToken,
                excludeSession,
                label,
                framed)
            : Task.FromResult(0);
    }

    /// <summary>
    /// ITEMS-HERMES (F-56): like <see cref="BroadcastToMapAsync"/>, but each recipient gets its own frame from
    /// <paramref name="build"/> (the Hermes Hood shows each viewer their own camp). Every recipient gets a fresh
    /// buffer, so the parallel fan-out never shares a mutable frame.
    /// </summary>
    public async Task<int> BroadcastToMapPerRecipientAsync(
        byte mapId,
        Func<GameSessionContext, byte[]> build,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true)
    {
        if (!TryResolveWorldInstance(mapId, excludeSession, out var runtime))
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in SnapshotReadySessions(runtime, excludeSession))
        {
            try
            {
                if (await TrySendWorldInstancePacketAsync(
                        runtime,
                        context,
                        build(context),
                        cancellationToken,
                        label,
                        framed))
                {
                    sent++;
                }
            }
            catch (Exception ex)
                when (ex is IOException or
                    ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

    internal Task<int> BroadcastToWorldInstanceAsync(
        WorldInstanceId instanceId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true)
    {
        return WorldInstances.TryFind(
            instanceId,
            out var runtime)
            ? BroadcastToWorldInstanceCoreAsync(
                runtime,
                packet,
                cancellationToken,
                excludeSession,
                label,
                framed)
            : Task.FromResult(0);
    }

    internal Task<int> BroadcastToCurrentWorldInstanceAsync(
        ClientSession routingSession,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        bool includeRoutingSession,
        string? label = null,
        bool framed = true)
    {
        ArgumentNullException.ThrowIfNull(routingSession);
        if (!_sessions.TryGetValue(
                routingSession,
                out var context) ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return Task.FromResult(0);
        }

        return BroadcastToWorldInstanceCoreAsync(
            runtime,
            packet,
            cancellationToken,
            includeRoutingSession ? null : routingSession,
            label,
            framed);
    }

    public int GetMapPopulation(byte mapId)
    {
        return TryGetDefaultWorldInstance(
            mapId,
            out var runtime)
            ? InvokeWorldOwner(
                runtime,
                static map => map.Population)
            : 0;
    }

    internal int GetWorldInstancePopulation(
        WorldInstanceId instanceId)
    {
        return WorldInstances.TryFind(
            instanceId,
            out var runtime)
            ? InvokeWorldOwner(
                runtime,
                static map => map.Population)
            : 0;
    }

    public IReadOnlyList<GameSessionContext> GetMapSessions(
        byte mapId,
        ClientSession? excludeSession = null)
    {
        return TryResolveWorldInstance(
            mapId,
            excludeSession,
            out var runtime)
            ? SnapshotReadySessions(
                runtime,
                excludeSession)
            : [];
    }

    internal IReadOnlyList<GameSessionContext>
        GetWorldInstanceSessions(
            WorldInstanceId instanceId,
            ClientSession? excludeSession = null)
    {
        return WorldInstances.TryFind(
            instanceId,
            out var runtime)
            ? SnapshotReadySessions(
                runtime,
                excludeSession)
            : [];
    }

    public bool TryGetMapSessionByObjectId(
        byte mapId,
        uint objectId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = GetMapSessions(
                mapId,
                excludeSession)
            .FirstOrDefault(candidate =>
                candidate.ObjectId == objectId)!;
        return context is not null;
    }

    internal bool TryGetCurrentWorldSessionByObjectId(
        ClientSession routingSession,
        byte mapId,
        uint objectId,
        out GameSessionContext context)
    {
        if (!_sessions.TryGetValue(
                routingSession,
                out var route) ||
            route.MapId != mapId ||
            !TryGetWorldInstance(route, out var runtime))
        {
            context = default!;
            return false;
        }

        context = SnapshotReadySessions(
                runtime,
                excludeSession: null)
            .FirstOrDefault(candidate =>
                candidate.ObjectId == objectId)!;
        return context is not null;
    }

    internal bool IsCurrentWorldSessionSnapshot(
        ClientSession routingSession,
        GameSessionContext expected)
    {
        ArgumentNullException.ThrowIfNull(routingSession);
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            return _sessions.TryGetValue(
                       routingSession,
                       out var route) &&
                   route.WorldReady &&
                   route.WorldInstanceId == expected.WorldInstanceId &&
                   _sessions.TryGetValue(
                       expected.Session,
                       out var current) &&
                   current.WorldReady &&
                   current.WorldInstanceId == expected.WorldInstanceId &&
                   current.WorldRevision == expected.WorldRevision &&
                   current.CharacterId == expected.CharacterId &&
                   current.ObjectId == expected.ObjectId &&
                   current.Ownership == expected.Ownership &&
                   ReferenceEquals(
                       current.Character,
                       expected.Character);
        }
    }

    public bool TryGetMapSessionByCharacterId(
        byte mapId,
        int characterId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = GetMapSessions(
                mapId,
                excludeSession)
            .FirstOrDefault(candidate =>
                candidate.CharacterId == characterId)!;
        return context is not null;
    }

    internal bool TryGetCurrentWorldSessionByCharacterId(
        ClientSession routingSession,
        byte mapId,
        int characterId,
        out GameSessionContext context)
    {
        if (!_sessions.TryGetValue(
                routingSession,
                out var route) ||
            route.MapId != mapId ||
            !TryGetWorldInstance(route, out var runtime))
        {
            context = default!;
            return false;
        }

        context = SnapshotReadySessions(
                runtime,
                excludeSession: null)
            .FirstOrDefault(candidate =>
                candidate.CharacterId == characterId)!;
        return context is not null;
    }

    public int InitializeMapMonsters(
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset? initializedAt = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        var runtime = GetOrCreateDefaultWorldInstance(mapId);
        return InitializeWorldInstanceMonsters(
            runtime,
            definitions,
            initializedAt,
            activeWorldBossRespawn);
    }

    internal int InitializeMapMonsters(
        ClientSession routingSession,
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset? initializedAt = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        ArgumentNullException.ThrowIfNull(routingSession);
        if (!_sessions.TryGetValue(
                routingSession,
                out var context))
        {
            var gatewayAdmission =
                routingSession.GatewayWorldAdmission;
            if (gatewayAdmission is not null)
            {
                if (!gatewayAdmission.MapId.TryGetLegacyValue(
                        out var admittedMapId) ||
                    admittedMapId != mapId)
                {
                    throw new InvalidOperationException(
                        "The monster bootstrap map does not match the " +
                        "gateway admission.");
                }

                return InitializeWorldInstanceMonsters(
                    GetOrCreateGatewayWorldInstance(
                        gatewayAdmission),
                    definitions,
                    initializedAt,
                    activeWorldBossRespawn);
            }

            return InitializeMapMonsters(
                mapId,
                definitions,
                initializedAt,
                activeWorldBossRespawn);
        }

        if (context.MapId != mapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            throw new InvalidOperationException(
                "The monster bootstrap route does not match the " +
                "session's current world instance.");
        }

        return InitializeWorldInstanceMonsters(
            runtime,
            definitions,
            initializedAt,
            activeWorldBossRespawn);
    }

    internal int InitializeWorldInstanceMonsters(
        WorldInstanceId instanceId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset? initializedAt = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        return InitializeWorldInstanceMonsters(
            GetRequiredWorldInstance(instanceId),
            definitions,
            initializedAt,
            activeWorldBossRespawn);
    }

    private int InitializeWorldInstanceMonsters(
        WorldInstanceRuntime runtime,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset? initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return InvokeWorldOwner(
            runtime,
            map => map.TryGetMedusaMonsterAttachmentSnapshot(
                    out var medusa)
                ? medusa.MonsterCount
                : map.InitializeMonsters(
                    definitions,
                    initializedAt ?? DateTimeOffset.UtcNow,
                    activeWorldBossRespawn,
                    MonsterRespawnPolicyForMap(runtime.MapId)).Count);
    }

    /// <summary>
    /// INSTANCES: Bay Under Attack monsters never respawn inside a run (each run
    /// is a fresh instance). Every other map keeps the timed respawn it had.
    /// </summary>
    internal static MonsterRespawnPolicy MonsterRespawnPolicyForMap(int mapId) =>
        Godswar.Server.Domain.World.Instances.DynamicDungeonContentMapPolicy
            .IsHarborAttackMap(mapId)
            ? MonsterRespawnPolicy.Never
            : MonsterRespawnPolicy.Timed;

    private IReadOnlyList<GameSessionContext>
        SnapshotReadySessions(
            WorldInstanceRuntime runtime,
            ClientSession? excludeSession)
    {
        return InvokeWorldOwner(
            runtime,
            map => map.Snapshot()
                .Where(context =>
                    context.WorldReady &&
                    (excludeSession is null ||
                     !ReferenceEquals(
                         context.Session,
                         excludeSession)))
                .ToArray());
    }

    private async Task<int>
        BroadcastToWorldInstanceCoreAsync(
            WorldInstanceRuntime runtime,
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken,
            ClientSession? excludeSession,
            string? label,
            bool framed)
    {
        var recipients = SnapshotReadySessions(
            runtime,
            excludeSession);
        var sent = 0;
        await Parallel.ForEachAsync(
            recipients,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism =
                    _worldInstanceOptions
                        .MaximumFanoutConcurrency
            },
            async (context, token) =>
            {
                try
                {
                    if (!await TrySendWorldInstancePacketAsync(
                            runtime,
                            context,
                            packet,
                            token,
                            label,
                            framed))
                    {
                        return;
                    }

                    Interlocked.Increment(ref sent);
                }
                catch (Exception ex)
                    when (ex is IOException or
                        ObjectDisposedException)
                {
                    Remove(context.Session);
                }
            });
        return sent;
    }

    private async Task<bool>
        TrySendWorldInstancePacketAsync(
            WorldInstanceRuntime runtime,
            GameSessionContext recipientSnapshot,
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken,
            string? label,
            bool framed = true)
    {
        if (!IsCurrentWorldInstanceRecipient(
                runtime.InstanceId,
                recipientSnapshot))
        {
            return false;
        }

        await recipientSnapshot.Session.SendAsync(
            packet,
            cancellationToken,
            label,
            framed);
        return true;
    }

    private bool IsCurrentWorldInstanceRecipient(
        WorldInstanceId sourceInstanceId,
        GameSessionContext recipientSnapshot)
    {
        return recipientSnapshot.WorldInstanceId ==
                   sourceInstanceId &&
               _sessions.TryGetValue(
                   recipientSnapshot.Session,
                   out var current) &&
               current.WorldReady &&
               current.WorldInstanceId ==
                   recipientSnapshot.WorldInstanceId &&
               current.WorldRevision ==
                   recipientSnapshot.WorldRevision &&
               current.CharacterId ==
                   recipientSnapshot.CharacterId;
    }
}
