using System.Collections.Concurrent;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private void AddToMap(GameSessionContext context)
    {
        var runtime = GetRequiredWorldInstance(context);
        InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map => map.AddOrUpdate(context));
    }

    private WorldInstancePlayerTransfer StageMapTransfer(
        GameSessionContext context) =>
        StageMapTransferCore(
            context,
            transformOverride: null);

    private WorldInstancePlayerTransfer StageMapTransfer(
        GameSessionContext context,
        byte targetMapId,
        float targetX,
        float targetZ) =>
        StageMapTransferCore(
            context,
            new PlayerTransformOverride(
                targetMapId,
                targetX,
                targetZ));

    private WorldInstancePlayerTransfer StageMapTransferCore(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride)
    {
        var runtime = GetRequiredWorldInstance(context);
        var transfer = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map => map.StagePlayerTransfer(
                context,
                transformOverride));
        return new WorldInstancePlayerTransfer(
            runtime,
            transfer);
    }

    private void EnsureMapObjectIdAvailable(GameSessionContext context)
    {
        if (!TryGetWorldInstance(context, out var runtime))
        {
            return;
        }

        var collision = InvokeWorldOwner(
            runtime,
            map => map.Snapshot()
                .FirstOrDefault(candidate =>
                    !ReferenceEquals(
                        candidate.Session,
                        context.Session) &&
                    candidate.ObjectId == context.ObjectId));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"World object ID {context.ObjectId} is already assigned " +
                $"to character {collision.CharacterName} in world instance " +
                $"{context.WorldInstanceId}.");
        }
    }

    private void RemoveFromMap(GameSessionContext context,
        MapInstance.PlayerRemovalLease? removal)
    {
        if (TryGetWorldInstance(context, out var runtime))
        {
            var removedAt = DateTimeOffset.UtcNow;
            var lifeRevision = _playerLifeRevisions.TryGetValue(
                context.Session,
                out var currentLifeRevision)
                ? currentLifeRevision
                : -1;
            if (removal is null)
            {
                throw new InvalidOperationException(
                    "World removal requires a prepared delivery lease.");
            }
            InvokeWorldOwnerAuthoritativeMutation(
                runtime,
                map =>
                {
                    map.ClearMedusaCharacterEffectsForLifeGuarded(
                        context,
                        lifeRevision,
                        removedAt);
                    removal.Remove(map, context.Session, out _);
                    map.ClearMonsterAggroForCharacter(
                        context.CharacterId,
                        removedAt);
                });
        }
    }

    private sealed class WorldInstancePlayerTransfer :
        IDisposable
    {
        private readonly WorldInstanceRuntime _runtime;
        private MapInstance.PlayerTransfer? _transfer;

        public WorldInstancePlayerTransfer(
            WorldInstanceRuntime runtime,
            MapInstance.PlayerTransfer transfer)
        {
            _runtime = runtime;
            _transfer = transfer;
        }

        public void Commit(Action publishRegistryContext)
        {
            ArgumentNullException.ThrowIfNull(
                publishRegistryContext);
            var transfer = _transfer ??
                throw new ObjectDisposedException(
                    nameof(WorldInstancePlayerTransfer));
            InvokeWorldOwnerAuthoritativeMutation(
                _runtime,
                _ => transfer.Commit(
                    publishRegistryContext));
            _transfer = null;
        }

        public void Dispose()
        {
            var transfer = Interlocked.Exchange(
                ref _transfer,
                null);
            if (transfer is not null)
            {
                InvokeWorldOwnerAuthoritativeMutation(
                    _runtime,
                    _ => transfer.Dispose());
            }
        }
    }

    private sealed class PlayerStatusState
    {
        // Optimistic editor identity, not a credential; fresh for each status-state lifetime.
        public Guid EditorEpoch { get; } = Guid.NewGuid();
        private string _lastPublishedElementalFingerprint =
            ElementalClientStatusProjection.EmptyFingerprint;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool PeriodicRecoveryActive; // ROUND3: fast-path hint only; deadlines belong to each runtime status.
        public bool PeriodicDamageActive; // ROUND3: exact source/life/deadline checks remain under authority.

        public object CharacterUiStatsGate { get; } = new();

        public Dictionary<int, ActiveRuntimeStatus> RuntimeStatuses { get; } = [];

        public ActiveRuntimeStatus[] SkillCastControlStatuses = [];

        public ExperienceBoostState ExperienceBoosts { get; set; } = ExperienceBoostState.Empty;

        public string? LastFingerprint { get; set; }

        public string LastPublishedElementalFingerprint
        {
            get => Volatile.Read(
                ref _lastPublishedElementalFingerprint);
            set => Volatile.Write(
                ref _lastPublishedElementalFingerprint,
                value);
        }

        public ClientStatusAggregate LastPublishedAggregate { get; set; } =
            ClientStatusAggregate.Empty;

        public bool LocalCalculatedStatsSynchronizationPending { get; set; }

        public long Revision { get; set; }

        public bool CharacterUiStatsV1Enabled { get; set; }

        public DateTimeOffset? LastCharacterUiStatsV1ProbeAt { get; set; }

        public CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed class ZodiacOnlineSessionState(
        int accountId,
        int characterId,
        GameCharacter character,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public GameCharacter Character { get; set; } = character;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class ProgressionBoostOnlineSessionState(
        int accountId,
        int characterId,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

internal readonly record struct MonsterAreaDamageBroadcastHit(
    MonsterHealthMutation HealthMutation,
    uint ReportedDamage,
    CombatHitOutcome Outcome = CombatHitOutcome.Normal);
