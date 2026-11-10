using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates; // RATES
using Godswar.Server.Application.Rewards; // RATES
using Godswar.Server.Application.World;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PendingMonsterKillReward?>
        PrepareMonsterKillRewardAsync(
            MonsterDamageResult damageResult,
            int partyDivisor = 1)
    {
        if (_account is null || _character is null || !damageResult.Killed)
        {
            return null;
        }

        // Quest objectives are credited before the reward settlement: whether the
        // reward lands is the reward path's business, but the kill happened.
        await RecordQuestKillAsync(damageResult, CancellationToken.None);

        // The Lelantine Farm scores its own kills. Its credit is independent of
        // the ordinary reward gate, since the activity's published rule counts a
        // kill rather than the experience it paid.
        await RecordFarmKillAsync(damageResult, CancellationToken.None);

        // BATTLEFIELD: the Pindus Dragon's 1,000 points (its own gate).
        await RecordBattlefieldBossKillAsync(damageResult, CancellationToken.None);

        // W2 F-27 TROY: Trojan Expedition points (map 44, while the event is open, troy.awards).
        await RecordTroyKillAsync(damageResult, CancellationToken.None);

        var rewardPolicy = _gameplayCatalogs.MonsterRewards;
        // ROUND3 summons, the admin F-150: a Totem Pole soldier or treasure guardian pays no kill EXP, Talent EXP or pet
        // EXP. The kill still settles (zero award, as for a monster outside the level gap), so the death event,
        // quest credit and corpse handling are unchanged.
        var noExperienceSummon = _registry.ResolveNoExperienceRuntimeSpawnSource(_session, damageResult);
        var rewardEligible = noExperienceSummon is null &&
            MonsterRewardCatalog.IsEligible(
                damageResult.Monster,
                _character.Level,
                rewardPolicy);
        var reward = noExperienceSummon is null
            ? MonsterRewardCatalog.Resolve(
                damageResult.Monster,
                _character.Level,
                rewardPolicy)
            : new MonsterKillReward(0, 0);
        if (noExperienceSummon is not null)
        {
            Console.WriteLine(
                $"[reward] summon no-exp character={_character.Name} monster={damageResult.ObjectId} " +
                $"source={noExperienceSummon}");
        }
        if (partyDivisor > 1)
        {
            // PARTY (r02): the killer keeps one equal share; boosts and the
            // server rates below then apply once, to that share.
            reward = new MonsterKillReward(
                MonsterRewardCatalog.SplitEqually(
                    reward.Experience,
                    partyDivisor),
                MonsterRewardCatalog.SplitEqually(
                    reward.TalentExperience,
                    partyDivisor));
        }
        var basePetExperience = noExperienceSummon is not null
            ? 0
            : MonsterRewardCatalog.ResolvePetExperience(
                damageResult.Monster,
                _character.Level,
                rewardPolicy);
        if (rewardEligible &&
            _registry.TryResolveMedusaMonsterRule(
                _session,
                damageResult,
                out var medusaRule))
        {
            basePetExperience = medusaRule.PetExperience;
        }
        var rewardTime = DateTimeOffset.UtcNow;
        var experienceBoosts = ExperienceBoostState.Empty;
        if (reward.Experience > 0 ||
            reward.TalentExperience > 0 ||
            basePetExperience > 0)
        {
            try
            {
                // ROUND3: combine operator session EXP with durable sources exactly once.
                experienceBoosts =
                    await _registry.GetRewardExperienceBoostStateAsync(
                        _session,
                        _account.Id,
                        _character.Id,
                        _character.Camp,
                        _character.CurrentMap,
                        rewardTime,
                        CancellationToken.None);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[reward] boost resolution failed character={_character.Name}: {ex.Message}");
            }
        }

        var awardedExperience = rewardPolicy.ApplyExperienceMultipliers(
            reward.Experience,
            experienceBoosts.TotalBonusBasisPoints);
        var awardedTalentExperience = rewardPolicy.ApplyExperienceMultipliers(
            reward.TalentExperience,
            experienceBoosts.TotalTalentBonusBasisPoints);
        var awardedPetExperience = rewardPolicy.ApplyExperienceMultipliers(
            basePetExperience,
            experienceBoosts.TotalPetBonusBasisPoints);

        // RATES: server-wide multipliers (public.server_rates, hot-reloaded),
        // applied to the final award and kept inside the settlement caps.
        var rates = ServerRates.Current;
        var stockExperience = awardedExperience;
        var stockTalentExperience = awardedTalentExperience;
        var stockPetExperience = awardedPetExperience;
        awardedExperience = rates.Multiply(
            ServerRateKeys.MonsterExperience,
            awardedExperience,
            MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);
        awardedTalentExperience = rates.Multiply(
            ServerRateKeys.MonsterTalentExperience,
            awardedTalentExperience,
            MonsterDeathRewardCommandEnvelope.MaximumAwardedTalentExperience);
        awardedPetExperience = rates.Multiply(
            ServerRateKeys.PetMonsterExperience,
            awardedPetExperience,
            MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);
        if (awardedExperience != stockExperience ||
            awardedTalentExperience != stockTalentExperience ||
            awardedPetExperience != stockPetExperience)
        {
            Console.WriteLine(
                $"[rates] kill character={_character.Name} " +
                $"monster={damageResult.ObjectId} " +
                $"exp={stockExperience}->{awardedExperience} " +
                $"talent-exp={stockTalentExperience}->{awardedTalentExperience} " +
                $"pet-exp={stockPetExperience}->{awardedPetExperience} " +
                $"rates-revision={rates.Revision}");
        }

        // TITLES: Fortune Teller Lucky Day, +5% EXP and Talent EXP (off unless switched on).
        (awardedExperience, awardedTalentExperience) = ApplyZodiacLuckyDayBonus(awardedExperience, awardedTalentExperience, rewardTime);

        MonsterRewardSettlement? settlement;
        try
        {
            settlement = await SettleMonsterRewardWithImmediateRetryAsync(
                damageResult,
                awardedExperience,
                awardedTalentExperience,
                awardedPetExperience,
                rewardTime);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] persistence failed character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            return null;
        }

        if (settlement is null)
        {
            Console.WriteLine(
                $"[reward] settlement unavailable account={_account.Id} character={_character.Id} monster={damageResult.ObjectId}");
            return null;
        }

        ApplyMonsterRewardProjection(settlement);
        MonsterLootPresentation? monsterLoot = null;
        try
        {
            monsterLoot = _registry.PrepareMedusaMonsterLoot(
                _session,
                damageResult,
                settlement.DeathEventId,
                rewardTime);
            // A field or dungeon monster resolves its drops from the captured
            // template-key table instead of an instance rule.
            monsterLoot ??= _registry.PrepareMonsterLoot(
                _session,
                damageResult,
                settlement.DeathEventId,
                rewardTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[loot] preparation failed monster={damageResult.ObjectId}: {ex.Message}");
        }
        var worldBossControl =
            await ActivateWorldBossAreaControlIfApplicableAsync(
                damageResult,
                rewardTime,
                settlement.DeathEventId);
        return new PendingMonsterKillReward(
            damageResult,
            reward,
            experienceBoosts,
            awardedExperience,
            awardedTalentExperience,
            settlement,
            worldBossControl,
            monsterLoot);
    }

    private async Task PublishMonsterKillRewardAsync(
        PendingMonsterKillReward pending,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var damageResult = pending.DamageResult;
        var reward = pending.Reward;
        var experienceBoosts = pending.ExperienceBoosts;
        var awardedExperience = pending.AwardedExperience;
        var awardedTalentExperience = pending.AwardedTalentExperience;
        var settlement = pending.Settlement;
        var progression = settlement.Progression;

        Console.WriteLine(
            $"[reward] character={_character.Name} base-exp={reward.Experience} awarded-exp={awardedExperience} exp-bonus-bps={experienceBoosts.TotalBonusBasisPoints} base-talent-exp={reward.TalentExperience} awarded-talent-exp={awardedTalentExperience} talent-bonus-bps={experienceBoosts.TotalTalentBonusBasisPoints} global-exp-multiplier-bps={_gameplayCatalogs.MonsterRewards.GlobalExperienceMultiplierBasisPoints} boosts={string.Join(',', experienceBoosts.ActiveBoosts.Select(boost => boost.StatusId))}");

        if (settlement.IsFirstCommit &&
            !pending.PartyShare &&
            _registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            try
            {
                var projection =
                    _registry.ProjectCommittedMonsterKillProgressionEcs(
                        _session,
                        damageResult,
                        progression);
                if (!projection.Applied)
                {
                    Console.WriteLine(
                        $"[reward] ECS progression projection skipped character={_character.Name} monster={damageResult.ObjectId} reason={projection.RejectionReason}");
                }
            }
            catch (Exception ex)
            {
                // Persistence is authoritative. Projection diagnostics must not
                // suppress packets for an already-committed reward.
                Console.WriteLine(
                    $"[reward] ECS progression projection deferred character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            }
        }

        ApplyMonsterRewardProjection(settlement);

        if (settlement.IsFirstCommit &&
            progression.LevelUps.Count > 0)
        {
            try
            {
                var refreshedProjection =
                    await _characterRuntimeProjections
                        .ReadCalculatedStatsAsync(
                            _account.Id,
                            _character.Id,
                            cancellationToken);
                if (refreshedProjection is not null)
                {
                    var refreshedStats =
                        CharacterLoadSnapshotHydrator.MapCalculatedStats(
                            refreshedProjection);
                    // The killing skill's MP cost is persisted after this reward
                    // sequence. Refresh derived maxima without restoring the
                    // older database vitals and accidentally refunding that cost.
                    lock (_character.VitalsSync)
                    {
                        var currentHp = _character.CurrentHp;
                        var currentMp = _character.CurrentMp;
                        refreshedStats.ApplyTo(_character);
                        ApplyElementalPassiveStats(
                            _character,
                            refreshedStats);
                        _character.CurrentHp = Math.Clamp(currentHp, 0, _character.MaxHp);
                        _character.CurrentMp = Math.Clamp(currentMp, 0, _character.MaxMp);
                        _character.MarkVitalsChanged();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[reward] level-up stat refresh deferred character={_character.Name}: {ex.Message}");
            }
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        foreach (var levelUp in settlement.IsFirstCommit
                     ? progression.LevelUps
                     : [])
        {
            var clientExperienceMaximum =
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    levelUp.Level,
                    _character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                "MonsterKillLevelUp");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "MonsterKillLevelUpWorld");
        }

        // A level-gated quest opens on its accept band alone, so this kill may have
        // unlocked one. The mark lists follow a hand-in for the same reason; without
        // this the new quest would wait for the next hand-in or relog to show up.
        if (settlement.IsFirstCommit && progression.LevelUps.Count > 0)
        {
            await SendQuestLevelUpRefreshAsync(cancellationToken);
        }

        if (settlement.IsFirstCommit &&
            progression.ExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.ExperienceGain(
                    progression.ExperienceGained,
                    progression.CurrentExperience),
                cancellationToken,
                "MonsterKillExperience");
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MonsterKillProgressionStatus");
        }

        if (settlement.IsFirstCommit &&
            progression.TalentExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.TalentExperienceGain(progression.TalentExperienceGained),
                cancellationToken,
                "MonsterKillTalentExperience");
        }

        // PARTY (r02): the 10027 death frame belongs to the killer (it also
        // clears that monster's loot window); a sharing member gets the 10031 (0x272F)
        // EXP frame and status refresh above instead.
        if (!pending.PartyShare)
        {
            await SendMonsterDeathProgressionAsync(
                damageResult.ObjectId,
                damageResult.Monster.SpawnGeneration,
                _character.Experience,
                _character.TalentExperience,
                _character.TalentPoints,
                cancellationToken);
        }

        if (settlement.PetExperience is
                { HasPetProjection: true } petExperience)
        {
            await _session.SendAsync(
                PacketBuilder.PetExperience(
                    petExperience.PetId!.Value,
                    petExperience.TotalExperience!.Value),
                cancellationToken,
                "MonsterKillPetExperience");
        }

        if (pending.MonsterLoot is { Entries.Count: > 0 } loot)
        {
            await _session.SendAsync(
                PacketBuilder.MonsterLoot(
                    loot.MonsterObjectId,
                    loot.DeathEventId,
                    loot.Entries),
                cancellationToken,
                "MonsterLootAvailable");
        }

        if (settlement.IsFirstCommit &&
            progression.TalentPointsGained > 0)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MonsterKillTalentPointCarry");
        }

        if (settlement.IsFirstCommit) // FINALISE2 (F-63): kill and pet EXP lines in the info window
        {
            await LogKillGainAsync(damageResult.Monster.Definition.DisplayName, (int)Math.Min(int.MaxValue, progression.ExperienceGained),
                (int)Math.Min(int.MaxValue, progression.TalentExperienceGained), pending.PartyShare,
                settlement.PetExperience?.AwardedExperience ?? 0, cancellationToken);
        }

        await PublishWorldBossAreaControlAsync(
            pending.WorldBossControl,
            cancellationToken);
        Console.WriteLine(
            $"[reward] {(pending.PartyShare ? "party-share" : "kill")} character={_character.Name} monster={damageResult.ObjectId} death={settlement.DeathEventId:N} durable={settlement.IsDurable} first={settlement.IsFirstCommit} level={progression.PreviousLevel}->{_character.Level} exp=+{(settlement.IsFirstCommit ? progression.ExperienceGained : 0)}->{_character.Experience} talent-exp=+{(settlement.IsFirstCommit ? progression.TalentExperienceGained : 0)}->{_character.TalentExperience} talent-points=+{(settlement.IsFirstCommit ? progression.TalentPointsGained : 0)}->{_character.TalentPoints}");
    }

    private sealed record PendingMonsterKillReward(
        MonsterDamageResult DamageResult,
        MonsterKillReward Reward,
        ExperienceBoostState ExperienceBoosts,
        int AwardedExperience,
        int AwardedTalentExperience,
        MonsterRewardSettlement Settlement,
        FactionAreaExperienceControl? WorldBossControl,
        MonsterLootPresentation? MonsterLoot,
        bool PartyShare = false);

    private async Task<MonsterRewardSettlement?>
        SettleLegacyMonsterRewardAsync(
            Guid deathEventId,
            int awardedExperience,
            int awardedTalentExperience,
            CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return null;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.ApplyMonsterKillReward);
        var progression = await _store.ApplyMonsterKillRewardAsync(
            _account.Id,
            _character.Id,
            awardedExperience,
            awardedTalentExperience,
            cancellationToken);
        return progression is null
            ? null
            : new MonsterRewardSettlement(
                deathEventId,
                progression,
                Projection: null,
                IsFirstCommit: true,
                IsDurable: false);
    }

    private async Task<FactionAreaExperienceControl?>
        ActivateWorldBossAreaControlIfApplicableAsync(
        MonsterDamageResult damageResult,
        DateTimeOffset killedAt,
        Guid deathEventId)
    {
        if (_character is null ||
            // ROUND3 runtime spawns: a summoned monster is never the map's world boss.
            WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(damageResult.ObjectId) ||
            !_gameplayCatalogs.WorldBosses.IsWorldBoss(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey))
        {
            return null;
        }

        var deathToken = $"monster-death:{deathEventId:N}";
        try
        {
            var result = await _worldBossAreaControl.ActivateAsync(
                new WorldBossAreaActivation(
                    _character.CurrentMap,
                    damageResult.Monster.Definition.TemplateKey,
                    _character.Camp,
                    killedAt,
                    deathToken),
                CancellationToken.None);
            return !result.IsSuccess || result.Control is null
                ? null
                : FocusedGameplayProjectionCompatibility.ToLegacy(
                    result.Control);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[world-boss] area-control activation failed map={_character.CurrentMap} boss={damageResult.Monster.Definition.TemplateKey}: {ex.Message}");
            return null;
        }
    }

    private async Task PublishWorldBossAreaControlAsync(
        FactionAreaExperienceControl? control,
        CancellationToken cancellationToken)
    {
        if (control is null)
        {
            return;
        }

        Console.WriteLine(
            $"[world-boss] area-control map={control.MapId} camp={control.ControllingCamp} boss={control.BossTemplateKey} expires={control.ExpiresAt:O}");
        try
        {
            await _registry.SendExperienceBoostStatusesAsync(
                mapId: control.MapId,
                camp: null,
                reason: "world-boss-control",
                cancellationToken: cancellationToken,
                routingSession: _session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] status refresh deferred map={control.MapId} boss={control.BossTemplateKey}: {ex.Message}");
        }
    }

    private async Task<bool> IsSkillLearnedAsync(uint skillId, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || skillId > int.MaxValue)
        {
            return false;
        }

        return await _characterRuntimeProjections.IsSkillLearnedAsync(
            _account.Id,
            _character.Id,
            checked((int)skillId),
            cancellationToken);
    }

}
