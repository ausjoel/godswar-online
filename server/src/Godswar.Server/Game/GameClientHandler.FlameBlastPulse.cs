using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // The field scheduler owns the character command gate. This is a committed
    // field effect, so it reserves no new cast, mana, cooldown or intonation.
    private async Task<bool> ApplyFlameBlastPulseAsync(
        FlameBlastSource source,
        SkillCombatDefinition combat,
        float centerX,
        float centerZ,
        ulong fieldId,
        int pulseOrdinal,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_character is not { } character || source.Context is null ||
            !ReferenceEquals(character, source.Context.Character) ||
            !RevalidateCurrentWorldEffectOwnership("flame_blast_pulse") ||
            !_registry.IsCurrentFlameBlastSource(source)) return false;
        if (!_registry.TryCaptureFlameBlastTargets(source, out var captured, out var authority))
            return _registry.IsCurrentFlameBlastSource(source);

        using var elementalAuthority = CapturePveElementalCommitAuthority(character);
        if (elementalAuthority is null || !_registry.IsCurrentFlameBlastSource(source))
            return false;
        var candidates = captured.Where(monster => monster.IsSpawned && monster.IsAlive &&
                _registry.IsMonsterVisibleTo(_session, monster.ObjectId, monster.SpawnGeneration) &&
                SkillCombatResolver.IsWithinArea(centerX, centerZ, monster.X, monster.Z, combat))
            .OrderBy(monster => monster.ObjectId).ToArray();
        var modifiers = _registry.GetRuntimeStatusAggregate(_session, observedAt);
        var pulseScope = CombatEventIdentity.ForFlameBlastPulseScope(fieldId, pulseOrdinal);
        var hits = new List<(MonsterDamageResult Result, CombatResolution Resolution)>();
        for (var index = 0; index < candidates.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested || !_registry.IsCurrentFlameBlastSource(source)) break;
            var target = candidates[index];
            var eventId = CombatEventIdentity.ForFlameBlastPulse(character.Id, target.ObjectId,
                target.SpawnGeneration, target.HealthRevision, fieldId,
                checked((uint)combat.SkillId), pulseOrdinal, index);
            var targetStats = _gameplayCatalogs.MonsterCombatProfiles.Resolve(target.Definition).ToTargetStats();
            targetStats = _registry.AdjustPveMonsterTargetStats(_session, target, observedAt, targetStats);
            var resolution = SkillCombatResolver.ResolveDamage(
                character, combat, targetStats, eventId, index, modifiers);
            resolution = _registry.AdjustPveOutgoingResolution(_session, character, target,
                CombatEventProvenance.DirectSkill, observedAt, resolution, pulseScope);
            if (!resolution.Hit || resolution.Damage == 0) continue;
            if (_registry.TryCommitPlayerMonsterDamageGuarded(_session, source.Context.MapId,
                    target.ObjectId, target.RuntimeInstanceId, character.Id, target.SpawnGeneration,
                    target.HealthRevision, authority, observedAt, resolution, out var committed) &&
                committed.DamageResult is { } damage && damage.BeforeHealth != damage.AfterHealth)
            {
                hits.Add((damage, committed.Resolution));
            }
        }
        if (hits.Count == 0) return _registry.IsCurrentFlameBlastSource(source);

        // Stop new effects when the source expires, but keep settlement of any
        // already committed primary defeats instead of abandoning a partial pulse.
        var sourceCurrent = _registry.IsCurrentFlameBlastSource(source);
        var healingReceived = checked((int)Math.Clamp(_registry.AdjustElementalHealingReceived(
            _session, character, observedAt, ElementalBasisPointMath.Denominator),
            0, ElementalBasisPointMath.Denominator));
        var healing = sourceCurrent
            ? _registry.CommitFlameBlastLifeAbsorption(source, _pveLifeAbsorptionCommitter,
                hits.Select(hit => new PveCommittedMonsterDamage(
                hit.Resolution.EventId, hit.Result.ObjectId, hit.Result.Monster.SpawnGeneration,
                hit.Result.BeforeHealth - hit.Result.AfterHealth)).ToArray(), healingReceived)
            : default;
        var elemental = sourceCurrent
            ? _registry.CommitPveElementalHits(elementalAuthority, CombatEventProvenance.DirectSkill,
                hits.Select(hit => new PveElementalCommittedHit(hit.Resolution.EventId,
                    hit.Resolution.TargetOrder, hit.Result)).ToArray(), observedAt)
            : PveElementalCommitResult.Empty;
        var rewards = new List<PreparedPveMonsterKillReward>();
        foreach (var hit in hits.Where(hit => hit.Result.Killed))
        {
            var reward = await PrepareClaimedMonsterKillRewardAsync(hit.Result);
            if (reward is not null) rewards.Add(reward);
        }
        var elementalRewards = await PreparePveElementalKillRewardsAsync(elementalAuthority, elemental);
        if (_registry.IsCurrentFlameBlastSource(source))
            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        foreach (var hit in hits)
        {
            if (!_registry.IsCurrentFlameBlastSource(source)) break;
            await _registry.PublishMonsterClaimStateAsync(_session, source.Context.MapId,
                hit.Result, cancellationToken);
        }
        foreach (var hit in hits)
            await _registry.PublishFlameBlastPulseDamageAsync(source, hit.Result,
                hit.Resolution, cancellationToken);
        await PublishPveLifeAbsorptionAsync(character, healing, cancellationToken, persistVitals: false);
        await PublishPveElementalCommitAsync(elementalAuthority, elemental, elementalRewards, cancellationToken);
        if (_registry.IsCurrentFlameBlastSource(source))
            await PersistSkillVitalsAsync(character, areaSkill: true, cancellationToken);
        foreach (var reward in rewards) await reward.PublishAsync(cancellationToken);

        Console.WriteLine($"[skill] ground pulse skill={combat.SkillId} field={fieldId} pulse={pulseOrdinal} " +
            $"candidates={candidates.Length} hits={hits.Count} applied=" +
            hits.Aggregate(0UL, (total, hit) => total + hit.Result.BeforeHealth - hit.Result.AfterHealth));
        return _registry.IsCurrentFlameBlastSource(source);
    }
}
