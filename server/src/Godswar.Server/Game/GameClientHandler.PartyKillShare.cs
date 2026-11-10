using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// PARTY (r02): party kill sharing - the paying half.
//
// Rule (the admin, DECISIONS-2026-11-03 r02; IGG guide acid=45 "The quests completed
// can be shared by them on the same map"): every party member on the killer's
// map, alive and near the kill (GameSessionRegistry.PartyKillShare.cs) gets
// kill-quest credit, and the kill EXP and Talent EXP are split equally between
// the members who can earn EXP from that monster (below level 200 and inside
// the 20-level gap rule). Each share then gets its owner's own EXP boosts, the
// global multiplier and the server_rates multiplier once. Drops, quest-item
// drops, pet EXP, farm points, Atlantis scoring and world-boss control stay
// with the killer, exactly as before.
//
// The killer plans the split and queues one immutable fact per member; each
// member applies its fact in its own session under its own character gate
// (the pattern RecordQuestPlayerKillAsync uses), so no session ever writes
// another session's character.
internal sealed partial class GameClientHandler
{
    private const int PartyKillShareQueueLimit = 256;
    private const int PartyKillShareMemory = 512;

    private readonly ConcurrentQueue<PartyKillShareFact> _partyKillShares = new();
    private readonly SemaphoreSlim _partyKillShareSignal = new(0, int.MaxValue);
    private readonly HashSet<Guid> _partyKillSharesApplied = [];
    private readonly Queue<Guid> _partyKillSharesAppliedOrder = new();
    private CancellationTokenSource? _partyKillShareCancellation;
    private Task? _partyKillShareTask;

    private sealed record PartyKillSharePlan(
        int Divisor,
        IReadOnlyList<(PartyKillShareCandidate Member, PartyKillShareFact Fact)> Deliveries);

    /// <summary>
    /// Decides the split for a kill this session has claimed. Null means a
    /// solo kill (no party, nobody near, sharing switched off): the killer's
    /// path then runs exactly as it did before party sharing existed.
    /// </summary>
    private PartyKillSharePlan? PlanPartyKillShare(MonsterDamageResult damageResult)
    {
        try
        {
            if (_character is null ||
                !damageResult.Killed ||
                damageResult.HealthMutation is not { } mutation)
            {
                return null;
            }

            var rates = ServerRates.Current;
            if (rates.Absolute(ServerRateKeys.PartyKillShare) != 1)
            {
                return null;
            }

            // ROUND3 summons, the admin F-150: a Totem Pole soldier or treasure guardian pays no share either.
            if (_registry.ResolveNoExperienceRuntimeSpawnSource(_session, damageResult) is not null)
            {
                return null;
            }

            var cellRadius = (int)Math.Clamp(
                rates.Absolute(ServerRateKeys.PartyKillShareCells),
                0,
                8);
            var candidates = _registry.GetPartyKillShareCandidates(
                _session,
                damageResult,
                cellRadius);
            if (candidates.Count == 0)
            {
                return null;
            }

            var policy = _gameplayCatalogs.MonsterRewards;
            var monster = damageResult.Monster;
            var earners = candidates
                .Where(member => MonsterRewardCatalog.EarnsExperience(
                    monster,
                    member.Level,
                    policy))
                .Select(member => member.CharacterId)
                .ToHashSet();
            var divisor = Math.Max(
                1,
                earners.Count +
                (MonsterRewardCatalog.EarnsExperience(
                    monster,
                    _character.Level,
                    policy) ? 1 : 0));
            var stock = MonsterRewardCatalog.ResolveStockReward(monster);
            var deathEventId = MonsterDeathRewardCommandEnvelope.DeriveDeathEventId(
                monster.RuntimeInstanceId,
                _character.CurrentMap,
                damageResult.ObjectId,
                mutation.SpawnGeneration,
                mutation.AfterHealthRevision);
            var deliveries = candidates
                .Select(member =>
                {
                    var earns = earners.Contains(member.CharacterId);
                    return (member, new PartyKillShareFact(
                        damageResult,
                        deathEventId,
                        earns
                            ? MonsterRewardCatalog.SplitEqually(stock.Experience, divisor)
                            : 0,
                        earns
                            ? MonsterRewardCatalog.SplitEqually(stock.TalentExperience, divisor)
                            : 0,
                        divisor,
                        _character.Name));
                })
                .ToArray();
            Console.WriteLine(
                $"[party] kill-share claimant={_character.Name} monster={damageResult.ObjectId} " +
                $"death={deathEventId:N} divisor={divisor} cells={cellRadius} " +
                $"members={string.Join(',', candidates.Select(member => $"{member.CharacterName}{(earners.Contains(member.CharacterId) ? "" : "(no-exp)")}"))}");
            return new PartyKillSharePlan(divisor, deliveries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sharing must never cost the killer the kill.
            Console.WriteLine(
                $"[party] kill-share planning failed monster={damageResult.ObjectId}: {ex.Message}");
            return null;
        }
    }

    private static void DeliverPartyKillShares(PartyKillSharePlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        foreach (var (member, fact) in plan.Deliveries)
        {
            bool accepted;
            try
            {
                accepted = member.Receive(fact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[party] kill-share delivery failed member={member.CharacterName}: {ex.Message}");
                continue;
            }

            if (!accepted)
            {
                Console.WriteLine(
                    $"[party] kill-share not queued member={member.CharacterName} death={fact.DeathEventId:N}");
            }
        }
    }

    /// <summary>Registered with the registry; called from the killer's flow.</summary>
    private bool ReceivePartyKillShare(PartyKillShareFact fact)
    {
        if (_session.IsDisconnected ||
            _character is null ||
            _partyKillShares.Count >= PartyKillShareQueueLimit)
        {
            return false;
        }

        _partyKillShares.Enqueue(fact);
        _partyKillShareSignal.Release();
        return true;
    }

    private void StartPartyKillShares()
    {
        if (_partyKillShareTask is { IsCompleted: false })
        {
            return;
        }

        _partyKillShareCancellation?.Dispose();
        _partyKillShareCancellation = new CancellationTokenSource();
        _partyKillShareTask = RunPartyKillSharesAsync(
            _partyKillShareCancellation.Token);
    }

    private async Task StopPartyKillSharesAsync()
    {
        var cancellation = _partyKillShareCancellation;
        var task = _partyKillShareTask;
        _partyKillShareCancellation = null;
        _partyKillShareTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }

        if (!_partyKillShares.IsEmpty)
        {
            Console.WriteLine(
                $"[party] kill-share dropped {_partyKillShares.Count} queued share(s) at session end character={_character?.Name ?? "(none)"}");
        }
    }

    private async Task RunPartyKillSharesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _partyKillShareSignal.WaitAsync(cancellationToken);
                if (_partyKillShares.IsEmpty)
                {
                    continue;
                }

                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    while (_partyKillShares.TryDequeue(out var fact))
                    {
                        try
                        {
                            await ApplyPartyKillShareAsync(fact, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.WriteLine(
                                $"[party] kill-share failed character={_character?.Name ?? "(none)"} death={fact.DeathEventId:N}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Applies one shared kill to this session's own character.</summary>
    /// <remarks>
    /// Quest credit and EXP are saved separately, as the killer's are: quest
    /// progress by SaveQuestStateAsync, EXP by the exactly-once monster reward
    /// command under this member's own share identity. A fact seen twice in
    /// this session is ignored, and a replayed EXP command pays nothing.
    /// </remarks>
    private async Task ApplyPartyKillShareAsync(
        PartyKillShareFact fact,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !RememberPartyKillShare(fact.DeathEventId))
        {
            return;
        }

        var damageResult = fact.DamageResult;
        if (damageResult.Monster.Definition.MapId != _character.CurrentMap ||
            _character.CurrentHp <= 0)
        {
            Console.WriteLine(
                $"[party] kill-share skipped character={_character.Name} death={fact.DeathEventId:N} " +
                $"reason={(_character.CurrentHp <= 0 ? "dead" : "left-map")}");
            return;
        }

        await RecordQuestKillCoreAsync(damageResult, cancellationToken, rollQuestDrops: false);

        if ((fact.ShareExperience <= 0 && fact.ShareTalentExperience <= 0) ||
            _character.Level >= PlayerExperienceCatalog.MaximumLevel)
        {
            Console.WriteLine(
                $"[party] kill-share credit-only character={_character.Name} level={_character.Level} " +
                $"monster={damageResult.ObjectId} from={fact.ClaimantName}");
            return;
        }

        var rewardPolicy = _gameplayCatalogs.MonsterRewards;
        var rewardTime = DateTimeOffset.UtcNow;
        var experienceBoosts = ExperienceBoostState.Empty;
        try
        {
            // ROUND3: party shares use the same session EXP channels as the killer.
            experienceBoosts = await _registry.GetRewardExperienceBoostStateAsync(
                _session,
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                rewardTime,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] boost resolution failed character={_character.Name}: {ex.Message}");
        }

        // The same order as the killer's own share: own boosts and the global
        // multiplier, then the server_rates multiplier exactly once.
        var rates = ServerRates.Current;
        var awardedExperience = rates.Multiply(
            ServerRateKeys.MonsterExperience,
            rewardPolicy.ApplyExperienceMultipliers(
                fact.ShareExperience,
                experienceBoosts.TotalBonusBasisPoints),
            MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);
        var awardedTalentExperience = rates.Multiply(
            ServerRateKeys.MonsterTalentExperience,
            rewardPolicy.ApplyExperienceMultipliers(
                fact.ShareTalentExperience,
                experienceBoosts.TotalTalentBonusBasisPoints),
            MonsterDeathRewardCommandEnvelope.MaximumAwardedTalentExperience);

        MonsterRewardSettlement? settlement;
        try
        {
            settlement = await SettleMonsterRewardWithImmediateRetryAsync(
                damageResult,
                awardedExperience,
                awardedTalentExperience,
                awardedPetExperience: 0,
                rewardTime,
                partyShare: true);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] party-share persistence failed character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            return;
        }

        if (settlement is null)
        {
            Console.WriteLine(
                $"[reward] party-share settlement unavailable character={_character.Id} monster={damageResult.ObjectId}");
            return;
        }

        ApplyMonsterRewardProjection(settlement);
        await PublishMonsterKillRewardAsync(
            new PendingMonsterKillReward(
                damageResult,
                new MonsterKillReward(
                    fact.ShareExperience,
                    fact.ShareTalentExperience),
                experienceBoosts,
                awardedExperience,
                awardedTalentExperience,
                settlement,
                WorldBossControl: null,
                MonsterLoot: null,
                PartyShare: true),
            cancellationToken);
    }

    private bool RememberPartyKillShare(Guid deathEventId)
    {
        if (!_partyKillSharesApplied.Add(deathEventId))
        {
            return false;
        }

        _partyKillSharesAppliedOrder.Enqueue(deathEventId);
        while (_partyKillSharesAppliedOrder.Count > PartyKillShareMemory)
        {
            _partyKillSharesApplied.Remove(_partyKillSharesAppliedOrder.Dequeue());
        }

        return true;
    }
}
