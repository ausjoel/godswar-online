using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<int> SendExperienceBoostStatusesAsync(
        byte mapId,
        byte? camp,
        string reason,
        CancellationToken cancellationToken,
        ClientSession? routingSession = null)
    {
        if (_store is null ||
            !TryResolveWorldInstance(
                mapId,
                routingSession,
                out var runtime))
        {
            return 0;
        }

        var recipients = InvokeWorldOwner(
            runtime,
            map => map.Snapshot()
                .Where(context =>
                    context.WorldReady &&
                    (camp is null ||
                     context.Character.Camp == camp.Value))
                .ToArray(),
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var sent = 0;
        foreach (var context in recipients)
        {
            try
            {
                if (!IsCurrentWorldInstanceRecipient(
                        runtime.InstanceId,
                        context))
                {
                    continue;
                }

                var boosts = await GetExperienceBoostStateAsync(
                    context.Session,
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                if (!IsCurrentWorldInstanceRecipient(
                        runtime.InstanceId,
                        context))
                {
                    continue;
                }

                if (await RefreshExperienceStatusesAndPublishAsync(
                        context.Session,
                        boosts,
                        now,
                        reason,
                        force: true,
                        broadcast: true,
                        cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[status] EXP boost map sync failed character={context.DisplayName} reason={reason}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[status] EXP boost map sync map={mapId} camp={(camp?.ToString() ?? "all")} reason={reason} sent={sent}");
        return sent;
    }

    public Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        string reason,
        CancellationToken cancellationToken)
    {
        return RefreshExperienceStatusesAndPublishAsync(
            session,
            boosts,
            DateTimeOffset.UtcNow,
            reason,
            force: true,
            broadcast: true,
            cancellationToken);
    }

    internal async Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        DateTimeOffset now,
        string reason,
        bool force,
        bool broadcast,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(boosts);

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var donatorStatsChanged =
                HasDonatorStatProjectionChanged(
                    state.ExperienceBoosts,
                    boosts);
            if (donatorStatsChanged &&
                !await RefreshDonatorCalculatedStatsAsync(
                    session,
                    cancellationToken))
            {
                return false;
            }
            if (donatorStatsChanged)
            {
                state.LocalCalculatedStatsSynchronizationPending = true;
            }

            state.ExperienceBoosts = boosts;
            var published = await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force,
                broadcast,
                cancellationToken,
                forceLocalGameDataSynchronization:
                    state.LocalCalculatedStatsSynchronizationPending,
                claimedDisconnects: admissionClaims);
            if (published)
            {
                state.LocalCalculatedStatsSynchronizationPending = false;
            }

            return published;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

    public async Task<bool> ApplyRuntimeStatusAndPublishAsync(
        ClientSession session,
        SkillStatusEffectDefinition definition,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // ROUND3: mounts must pass native equipment/life/MP authority instead of becoming icon-only generic statuses.
        if (definition.RequiresEquippedMount || definition.Kind == MountCatalog.RuntimeStatusKind)
        {
            return false;
        }
        // ROUND3: this is administrative HP loss, never an implicit player skill/attacker source.
        if (definition.HpDamagePerPulse < 0 || (definition.HpDamagePerPulse > 0 &&
            (!definition.OperatorApplied || definition.DamageIntervalSeconds is < 1 or > 3))) return false;

        // ROUND2: /bufflock on - the GM's own buffs last 24 hours.
        if (definition.Beneficial &&
            _sessions.TryGetValue(session, out var operatorContext) &&
            Application.Operators.OperatorControls.HasBuffLock(
                operatorContext.CharacterId) &&
            definition.Duration < TimeSpan.FromHours(24))
        {
            definition = definition with { Duration = TimeSpan.FromHours(24) };
        }

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        ActiveRuntimeStatus? appliedStatus = null;
        var applied = false;
        var admissionClaims = new ExactStatusDisconnectClaims();
        var interruptionTask = Task.CompletedTask;
        TaskCompletionSource? interruptionNotificationBarrier = null;
        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                if (state.RuntimeStatuses.TryGetValue(
                        definition.Kind,
                        out var existing) &&
                    existing.ExpiresAt > now &&
                    IsRuntimeStatusSourceCurrent(session, existing) && // ROUND3: old-life harm cannot block a new application.
                    existing.Priority > definition.Priority)
                {
                    return false;
                }

                var interruptionReason =
                    PlayerSkillCastControlCatalog
                        .ResolveAppliedInterruption(
                            definition.StatusId);
                // ROUND3: generic controls use their audited flags, including one-shot HaltIntonate.
                interruptionReason ??= (definition.Control & HostileStatusControlFlags.HaltIntonate) != 0
                    ? SkillCastInterruptionReason.Stunned
                    : (definition.Control & (HostileStatusControlFlags.NonMagicUsing |
                        HostileStatusControlFlags.NonTechniqueUsing)) != 0 ? SkillCastInterruptionReason.Silenced : null;
                if (interruptionReason is not null)
                {
                    interruptionNotificationBarrier =
                        new TaskCompletionSource(
                            TaskCreationOptions
                                .RunContinuationsAsynchronously);
                    // The handler claims the pending generation
                    // synchronously before its first await. Claim before
                    // mutating the status so completion and interruption
                    // have one authoritative order even at the deadline.
                    interruptionTask =
                        RequestSkillCastInterruptionAsync(
                            session,
                            interruptionReason.Value,
                            cancellationToken,
                            interruptionNotificationBarrier.Task);
                }

                // ROUND3: bind a harmful application to the exact living character life while installing its source.
                lock (_gate)
                {
                    long damageLifeRevision = 0;
                    if (definition.HpDamagePerPulse > 0 &&
                        (session.IsDisconnected || !_playerStatusStates.TryGetValue(session, out var currentState) || !ReferenceEquals(state, currentState) || // ROUND3
                         !_sessions.TryGetValue(session, out var target) || !target.WorldReady ||
                         (target.Ownership.IsValid && !IsCurrentAccountSession(target.AccountId, session, target.Ownership)) ||
                         !_playerLifeRevisions.TryGetValue(session, out damageLifeRevision) || !IsLivingStatusDamageTarget(target.Character))) // ROUND3
                        return false;
                    // ROUND3 (rig f2): an administrative application of any kind needs a living target, checked
                    // under the same lock as the harmful-source install above.
                    if (definition.OperatorApplied &&
                        (!_sessions.TryGetValue(session, out var living) || !IsLivingStatusDamageTarget(living.Character)))
                        return false;
                    appliedStatus = new ActiveRuntimeStatus(
                        definition.StatusId,
                        definition.Kind,
                        definition.Priority,
                        definition.Beneficial,
                        now + definition.Duration,
                        new ClientStatusAggregate(
                            definition.HitBonus,
                            definition.CriticalAppendBonus,
                            0f,
                            PhysicalDefense:
                                definition.PhysicalDefenseBonus,
                            MagicDefense:
                                definition.MagicDefenseBonus,
                            Dodge: definition.DodgeBonus,
                            CriticalResistance:
                                definition.CriticalResistanceBonus,
                            // ROUND3: authoritative action gates consume the same control flags as the snapshot.
                            Control: definition.Control,
                            PhysicalAttack: definition.PhysicalAttackBonus,
                            MagicAttack: definition.MagicAttackBonus,
                            PhysicalDamageBonusBasisPoints: definition.PhysicalDamageBonusBasisPoints,
                            MagicDamageBonusBasisPoints: definition.MagicDamageBonusBasisPoints),
                        checked(++state.Revision),
                        definition.PhysicalDamageReduction,
                        definition.MagicDamageReduction,
                        HpPerSecond: definition.HpPerSecond,
                        MpPerSecond: definition.MpPerSecond)
                    {
                        // ROUND3: never infer removal authority from a reused status ID.
                        OperatorApplied = definition.OperatorApplied,
                        // ROUND3: recasting this kind restarts only this application's first recovery deadline.
                        NextRecoveryAt = now + StatusPeriodicInterval,
                        // ROUND3: a recast gets a new application deadline/life; pulse progress keeps its revision.
                        HpDamagePerPulse = definition.HpDamagePerPulse,
                        DamageIntervalSeconds = definition.DamageIntervalSeconds,
                        NextDamageAt = now.AddSeconds(definition.DamageIntervalSeconds),
                        DamageLifeRevision = damageLifeRevision,
                        // ROUND3: do not lose the authored incoming channel when installing runtime/ECS state.
                        PhysicalDamageTakenIncreaseBasisPoints = definition.PhysicalDamageTakenIncreaseBasisPoints,
                        MagicDamageTakenIncreaseBasisPoints = definition.MagicDamageTakenIncreaseBasisPoints,
                        ExperienceBonuses = definition.ExperienceBonuses
                    };
                    state.RuntimeStatuses[definition.Kind] = appliedStatus;
                    if (definition.HpPerSecond > 0 || definition.MpPerSecond > 0)
                    {
                        // ROUND3: adding another kind must not postpone or advance this source's cadence.
                        Volatile.Write(ref state.PeriodicRecoveryActive, true);
                    }
                    if (definition.HpDamagePerPulse > 0) Volatile.Write(ref state.PeriodicDamageActive, true); // ROUND3
                }
                RefreshSkillCastControlSnapshot(state);
                await PublishStatusSnapshotLockedAsync(
                    session,
                    state,
                    now,
                    reason,
                    force: true,
                    broadcast: true,
                    cancellationToken,
                    claimedDisconnects: admissionClaims);
                applied = true;
            }
            finally
            {
                state.Gate.Release();
                admissionClaims.CompleteAll(this);
                if (appliedStatus is not null)
                {
                    ScheduleRuntimeStatusExpiry(
                        session,
                        state,
                        appliedStatus);
                }
            }
        }
        finally
        {
            // Preserve status-before-interruption packet ordering while
            // claiming the pending generation before status mutation.
            // Always release the notice if status publication fails.
            interruptionNotificationBarrier?.TrySetResult();
            await interruptionTask;
        }

        return applied;
    }

    public bool IsRuntimeStatusActive(
        ClientSession session,
        int kind,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return false;
        }

        state.Gate.Wait();
        try
        {
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return EvaluatePlayerStatusEcsLocked(
                                session,
                                state,
                                context,
                                now)
                            .ActiveRuntimeStatuses
                            .Any(status => status.Kind == kind);
                    }
                }
            }

            return state.RuntimeStatuses.TryGetValue(kind, out var status) &&
                status.ExpiresAt > now;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> SetPersistentRuntimeStatusAndPublishAsync(
        ClientSession session,
        int kind,
        uint statusId,
        int priority,
        bool beneficial,
        float movementSpeedBonus,
        bool active,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!float.IsFinite(movementSpeedBonus) || movementSpeedBonus < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementSpeedBonus),
                movementSpeedBonus,
                "A persistent runtime speed bonus must be finite and non-negative.");
        }

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            if (active)
            {
                var replacement = new ActiveRuntimeStatus(
                    statusId,
                    kind,
                    priority,
                    beneficial,
                    DateTimeOffset.MaxValue,
                    ClientStatusAggregate.Empty,
                    checked(++state.Revision),
                    MovementSpeedBonus: movementSpeedBonus);
                changed = !state.RuntimeStatuses.TryGetValue(kind, out var existing) ||
                    existing.StatusId != replacement.StatusId ||
                    existing.MovementSpeedBonus != replacement.MovementSpeedBonus ||
                    existing.ExpiresAt <= now;
                state.RuntimeStatuses[kind] = replacement;
            }
            else
            {
                changed = state.RuntimeStatuses.Remove(kind);
            }

            if (!changed)
            {
                return false;
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken,
                forceLocalGameDataSynchronization:
                    kind == MountCatalog.RuntimeStatusKind,
                claimedDisconnects: admissionClaims);
            return true;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

}
