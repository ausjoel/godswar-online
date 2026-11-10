using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>
/// BAY2: the Bay Under Attack completion EXP. When a run is first seen ended
/// (Christina's kill, the leader's end vote or the timer) the evidence is
/// frozen - clearance (team score cleared / team score the map loaded) and the
/// members still inside - and settled once through the durable store. The
/// frozen request outlives the run: a failed write is retried on every world
/// tick even after the members were carried home, and the store's exactly-once
/// receipt makes every retry safe.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    private IHarborAttackCompletionRewardStore? _harborAttackRewards;
    private readonly ConcurrentDictionary<WorldInstanceId, byte> _harborAttackRewardFrozen = [];
    private readonly ConcurrentDictionary<Guid, HarborAttackCompletionRewardRequest> _harborAttackRewardPending = [];
    private readonly ConcurrentDictionary<Guid, byte> _harborAttackRewardInFlight = [];
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _harborAttackRewardLastLog = [];
    private readonly ConcurrentDictionary<WorldInstanceId, ConcurrentDictionary<int, Guid>>
        _harborAttackMemberReservations = [];
    private readonly ConditionalWeakTable<ClientSession,
        Func<HarborAttackRewardProjection, CancellationToken, Task>> _harborAttackRewardProjectors = new();

    internal void ConfigureHarborAttackCompletionRewards(IHarborAttackCompletionRewardStore? store)
    {
        if (store is not null)
        {
            Interlocked.CompareExchange(ref _harborAttackRewards, store, null);
        }
    }

    /// <summary>The handler's own projection (EXP, level-ups, the log line).</summary>
    internal void RegisterHarborAttackRewardProjector(
        ClientSession session,
        Func<HarborAttackRewardProjection, CancellationToken, Task> projector)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(projector);
        _harborAttackRewardProjectors.AddOrUpdate(session, projector);
    }

    /// <summary>A late joiner's own reservation, part of the run's admission fence.</summary>
    private void RememberHarborAttackMemberReservation(
        WorldInstanceId instanceId,
        Guid memberReservationId,
        int characterId) =>
        _harborAttackMemberReservations.GetOrAdd(instanceId, static _ => new())[characterId] =
            memberReservationId;

    private void ForgetHarborAttackRewardRun(WorldInstanceId instanceId)
    {
        _harborAttackRewardFrozen.TryRemove(instanceId, out _);
        _harborAttackMemberReservations.TryRemove(instanceId, out _);
    }

    /// <summary>
    /// Freezes the run's reward evidence the first time it is seen ended. Kills
    /// during the end countdown still move the panel, never the reward.
    /// </summary>
    private Guid? FreezeHarborAttackReward(
        WorldInstanceRuntime runtime,
        Guid runReservationId,
        DateTimeOffset startedAt,
        IReadOnlyList<GameSessionContext> members,
        string endReason,
        DateTimeOffset endedAt)
    {
        if (_harborAttackRewards is null ||
            !_harborAttackRewardFrozen.TryAdd(runtime.InstanceId, 0))
        {
            return null;
        }
        if (members.Count == 0)
        {
            Console.WriteLine(
                $"[harbor] reward instance={runtime.InstanceId} reason={endReason}: nobody inside, nothing paid");
            return null;
        }

        var total = InvokeWorldOwner(runtime, static map => map.SnapshotMonsters()
            .Sum(static monster => HarborAttackContentPolicy.ScoreFor(monster.Definition.TemplateKey)));
        var cleared = Math.Min(HarborAttackScore(runtime.InstanceId), Math.Max(total, 0));
        var reservations = _harborAttackMemberReservations.TryGetValue(runtime.InstanceId, out var late)
            ? late.Values.ToArray()
            : [];
        var request = new HarborAttackCompletionRewardRequest(
            runReservationId,
            runtime.InstanceId.Value,
            checked((short)runtime.RealmId.Value),
            checked((byte)runtime.MapId),
            endReason,
            startedAt.ToUniversalTime(),
            endedAt.ToUniversalTime(),
            cleared,
            total,
            HarborAttackCompletionRewardPolicy.CurrentPercentOfLevel,
            [.. members.Select(static member =>
                new HarborAttackCompletionMember(member.AccountId, member.CharacterId))],
            reservations);
        _harborAttackRewardPending[runReservationId] = request;
        Console.WriteLine(
            $"[harbor] reward frozen instance={runtime.InstanceId} reason={endReason} " +
            $"score={cleared}/{total} clearance={request.ClearanceBasisPoints / 100.0:0.##}% " +
            $"percent={request.PercentOfLevel} members={string.Join(',', request.CharacterIds)}");
        return runReservationId;
    }

    /// <summary>Settles every frozen reward still owed (one at a time per run).</summary>
    private async Task SettlePendingHarborAttackRewardsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var reservationId in _harborAttackRewardPending.Keys)
        {
            await SettleHarborAttackRewardAsync(reservationId, now, cancellationToken);
        }
    }

    private async Task SettleHarborAttackRewardAsync(
        Guid reservationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var store = _harborAttackRewards;
        if (store is null ||
            !_harborAttackRewardPending.TryGetValue(reservationId, out var request) ||
            !_harborAttackRewardInFlight.TryAdd(reservationId, 0))
        {
            return;
        }
        try
        {
            var receipt = await store.SettleAsync(request, cancellationToken);
            if (receipt.Status == HarborAttackCompletionRewardStatus.RequestConflict)
            {
                // Not retryable: a different request already owns this run.
                _harborAttackRewardPending.TryRemove(reservationId, out _);
                Console.Error.WriteLine(
                    $"[harbor] reward conflict run={reservationId}: another settlement owns it; nothing paid");
                return;
            }
            _harborAttackRewardPending.TryRemove(reservationId, out _);
            _harborAttackRewardLastLog.TryRemove(reservationId, out _);
            Console.WriteLine(
                $"[harbor] reward settled run={reservationId} status={receipt.Status} " +
                $"clearance={receipt.ClearanceBasisPoints / 100.0:0.##}% " +
                string.Join(' ', receipt.Members.Select(static member =>
                    $"char={member.CharacterId}:{(member.Admitted ? "+" + member.ExperienceGranted : "not-admitted")}" +
                    $":lv{member.LevelBefore}->{member.LevelAfter}")));
            if (receipt.Status == HarborAttackCompletionRewardStatus.Applied)
            {
                await ProjectHarborAttackRewardsAsync(store, receipt, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            if (!_harborAttackRewardLastLog.TryGetValue(reservationId, out var last) ||
                now - last >= TimeSpan.FromSeconds(30))
            {
                _harborAttackRewardLastLog[reservationId] = now;
                Console.Error.WriteLine(
                    $"[harbor] reward will retry run={reservationId}: {error.Message}");
            }
        }
        finally
        {
            _harborAttackRewardInFlight.TryRemove(reservationId, out _);
        }
    }

    private async Task ProjectHarborAttackRewardsAsync(
        IHarborAttackCompletionRewardStore store,
        HarborAttackCompletionRewardReceipt receipt,
        CancellationToken cancellationToken)
    {
        foreach (var member in receipt.Members.Where(static member => member.Admitted))
        {
            ClientSession? session;
            lock (_gate)
            {
                session = _sessions.Values.FirstOrDefault(context =>
                    context.AccountId == member.AccountId &&
                    context.CharacterId == member.CharacterId &&
                    !context.Session.IsDisconnected &&
                    IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership))?.Session;
            }
            if (session is null ||
                !_harborAttackRewardProjectors.TryGetValue(session, out var projector))
            {
                // Offline: the EXP is stored; the next login reads it.
                continue;
            }
            try
            {
                // Always project the stored values, never the receipt's, so a kill
                // that committed after the payout is not rolled back on screen.
                var stored = await store.ReadProgressionAsync(member.CharacterId, cancellationToken);
                if (stored is not { } current)
                {
                    continue;
                }
                await projector(new HarborAttackRewardProjection(member, receipt.ClearanceBasisPoints,
                    current.Level, current.Experience), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[harbor] reward projection deferred character={member.CharacterId}: {error.Message}");
            }
        }
    }
}

/// <summary>BAY2: what a member's handler shows after a paid run.</summary>
internal readonly record struct HarborAttackRewardProjection(
    HarborAttackCompletionRewardMember Reward,
    int ClearanceBasisPoints,
    int StoredLevel,
    long StoredExperience);
