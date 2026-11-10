using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// The shared Battlefield Awarder claim flow (Athens_073 / Sparta_073, client NpcFunWar.lua, function 3). One flow
// serves the three battlefields on the Awarder's first page: 1 Pindus Mountains, 2 Ni Mini Valley, 3 Lelantine Farm.
// The menu pages themselves stay the captured ones (BattlefieldAwarderDialogue); only the clicks that claim or ask
// for numbers are answered here, keyed by the whole click path the client sends (+12 onwards), because 173 and 174
// are buttons on two different pages (Pindus [1,173], Ni Mini [2,1967,173]). With the battlefield.awarder rate at 0
// nothing here runs and every click gets the captured fixed reply, as before.
internal sealed partial class GameClientHandler
{
    private const short PindusKillTitleFlag = 1;
    private const short PindusTaskTitleFlag = 2;
    private const short NiMiniTitleFlag = 4;

    private IBattlefieldRoundStore? BattlefieldStore =>
        _store as IBattlefieldRoundStore ?? _farmPoints as IBattlefieldRoundStore;

    private static bool IsBattlefieldAwarder(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_073" or "Sparta_073";

    private async Task<bool> TryHandleBattlefieldAwarderActionAsync(
        NpcSpawnDefinition npc, int dialogIndex, int[] path, CancellationToken cancellationToken)
    {
        if (dialogIndex != BattlefieldAwarderReplies.FunctionNumber || !IsBattlefieldAwarder(npc) ||
            ServerRates.Current.Absolute(ServerRateKeys.BattlefieldAwarder) != 1 ||
            BattlefieldStore is not { } store || _character is not { } character || path.Length < 2)
        {
            return false;
        }

        int[]? reply = (path[0], path[1], path.Length > 2 ? path[2] : -1) switch
        {
            (BattlefieldAwarderReplies.PindusPage, BattlefieldAwarderReplies.ClaimFaction, -1) =>
                await ClaimPindusAsync(store, character, BattlefieldClaimPart.Faction, cancellationToken),
            (BattlefieldAwarderReplies.PindusPage, BattlefieldAwarderReplies.ClaimIndividual, -1) =>
                await ClaimPindusAsync(store, character, BattlefieldClaimPart.Points, cancellationToken),
            (BattlefieldAwarderReplies.PindusPage, BattlefieldAwarderReplies.ClaimTitle, var title and > 0) =>
                await ClaimPindusTitleAsync(store, character, title, cancellationToken),
            (BattlefieldAwarderReplies.PindusPage, BattlefieldAwarderReplies.Statistics, var board and > 0) =>
                await PindusStatisticsAsync(store, character, board, cancellationToken),
            (BattlefieldAwarderReplies.NiMiniPage, BattlefieldAwarderReplies.NiMiniBracket, -1) =>
                await NiMiniPageAsync(store, character, cancellationToken),
            (BattlefieldAwarderReplies.NiMiniPage, BattlefieldAwarderReplies.NiMiniBracket,
                BattlefieldAwarderReplies.ClaimFaction) =>
                await ClaimNiMiniReputationAsync(store, character, cancellationToken),
            (BattlefieldAwarderReplies.NiMiniPage, BattlefieldAwarderReplies.NiMiniBracket,
                BattlefieldAwarderReplies.ClaimTitle) =>
                await ClaimNiMiniTitleAsync(store, character, cancellationToken),
            (BattlefieldAwarderReplies.FarmPage, BattlefieldAwarderReplies.FarmFactionReward, -1) =>
                await ClaimFarmAtAwarderAsync(store, character, cancellationToken),
            (BattlefieldAwarderReplies.FarmPage, BattlefieldAwarderReplies.FarmFirstPlace, -1) =>
                await FarmFirstPlaceAsync(store, character, cancellationToken),
            // KELSIS (F-15): the contest's prize page; 207 (no data) while npc.kelsis_contest is 0, as before.
            (BattlefieldAwarderReplies.FarmPage, BattlefieldAwarderReplies.FarmKelsis, -1) =>
                await ClaimKelsisPrizeAtAwarderAsync(cancellationToken),
            _ => null
        };
        if (reply is null)
        {
            return false;
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc, BattlefieldAwarderDialogue, dialogIndex, path[^1], reply, cancellationToken);
        Console.WriteLine(
            $"[battlefield] awarder character={character.Name} path=[{string.Join(',', path)}] " +
            $"reply=[{string.Join(',', reply)}]");
        return true;
    }

    private bool BattlefieldClaimExpired(BattlefieldRewardRecord record) =>
        DateTimeOffset.UtcNow > BattlefieldRewardPolicy.ClaimDeadline(_realmCalendar, record.RoundStart, record.RoundEnd);

    /// <summary>How far back the Awarder looks for rewards: past the longest claim time the rates allow (720 h).</summary>
    private static readonly TimeSpan BattlefieldRewardLookBack = TimeSpan.FromDays(32);

    private static Task<IReadOnlyList<BattlefieldRewardRecord>> BattlefieldRewardsAsync(
        IBattlefieldRoundStore store, short battlefield, int characterId, CancellationToken cancellationToken) =>
        store.GetBattlefieldRewardsAsync(
            battlefield, characterId, DateTimeOffset.UtcNow - BattlefieldRewardLookBack, cancellationToken);

    /// <summary>
    /// The reward a click works on: the OLDEST unexpired one that still has this part to claim, so two rounds on one
    /// day (Pindus Saturday / Sunday) are both claimable, one click each. When none is claimable the newest one is
    /// returned so the usual "already claimed" / "claim on the battle day" / "only the top five" answer is given.
    /// </summary>
    private BattlefieldRewardRecord? PickBattlefieldReward(
        IReadOnlyList<BattlefieldRewardRecord> records, Func<BattlefieldRewardRecord, bool> claimable) =>
        records.Count == 0
            ? null
            : records.FirstOrDefault(record => claimable(record) && !BattlefieldClaimExpired(record)) ?? records[^1];

    /// <summary>The reply when there is no settled reward: still fighting (1757) or never entered (1755).</summary>
    private static async Task<int> NoBattlefieldRewardLineAsync(
        IBattlefieldRoundStore store, short battlefield, int characterId, CancellationToken cancellationToken) =>
        await store.IsInUnsettledBattlefieldRoundAsync(battlefield, characterId, cancellationToken)
            ? BattlefieldAwarderReplies.ClaimOnBattleDay
            : BattlefieldAwarderReplies.NotEntered;

    private async Task<int[]> ClaimPindusAsync(
        IBattlefieldRoundStore store, GameCharacter character, BattlefieldClaimPart part,
        CancellationToken cancellationToken)
    {
        var record = PickBattlefieldReward(
            await BattlefieldRewardsAsync(store, BattlefieldRewardPolicy.Pindus, character.Id, cancellationToken),
            r => part == BattlefieldClaimPart.Faction
                ? !r.FactionClaimed
                : !r.PointsClaimed && r.Reward.KillExperience + r.Reward.HolyExperience > 0);
        if (record is null)
        {
            return [await NoBattlefieldRewardLineAsync(store, BattlefieldRewardPolicy.Pindus, character.Id, cancellationToken)];
        }
        if (part == BattlefieldClaimPart.Faction ? record.FactionClaimed : record.PointsClaimed)
        {
            return [BattlefieldAwarderReplies.AlreadyClaimed];
        }
        if (BattlefieldClaimExpired(record))
        {
            return [BattlefieldAwarderReplies.ClaimOnBattleDay];
        }

        var reward = record.Reward;
        if (part == BattlefieldClaimPart.Points && reward.KillExperience + reward.HolyExperience <= 0)
        {
            // IGG: the faction (points) reward is EXP from Holy Water and kill points; none scored, nothing to claim.
            return [BattlefieldAwarderReplies.NoContribution];
        }

        var reputation = part == BattlefieldClaimPart.Faction
            ? ServerRates.Current.Multiply(ServerRateKeys.BattlefieldReputation, reward.Reputation)
            : 0;
        if (!await PayBattlefieldClaimAsync(store, record, part, reputation, cancellationToken))
        {
            return [BattlefieldAwarderReplies.AlreadyClaimed];
        }

        if (part == BattlefieldClaimPart.Faction)
        {
            if (!await PayBattlefieldProgressionAsync(store, record, part, reputation, reward.Experience,
                    reward.TalentPoints, "pindus-faction", cancellationToken))
            {
                return [BattlefieldAwarderReplies.ClaimOnBattleDay];
            }
            // BATTLEFIELD2: the lines print the paid amounts where the client can (battlefield.show_paid_amounts).
            return BattlefieldAwarderReplies.PindusFactionReward(
                record.Outcome,
                BattlefieldPaidAmounts.FactionLineLevel(record.Outcome, record.Level, reward.Experience, reward.TalentPoints),
                BattlefieldPaidAmounts.Enabled ? BattlefieldPaidAmounts.TalentPoints(reward.TalentPoints) : reward.TalentPoints,
                BattlefieldPaidAmounts.Enabled ? BattlefieldPaidAmounts.Experience(reward.Experience) : reward.Experience);
        }

        if (!await PayBattlefieldProgressionAsync(store, record, part, reputation,
                checked(reward.KillExperience + reward.HolyExperience), 0, "pindus-points", cancellationToken))
        {
            return [BattlefieldAwarderReplies.ClaimOnBattleDay];
        }
        var (killLine, holyLine) = BattlefieldPaidAmounts.PointsLines(reward.KillExperience, reward.HolyExperience); // BATTLEFIELD2
        return BattlefieldAwarderReplies.PindusPointsReward(killLine, holyLine);
    }

    private async Task<int[]> ClaimPindusTitleAsync(
        IBattlefieldRoundStore store, GameCharacter character, int board, CancellationToken cancellationToken)
    {
        if (board == BattlefieldAwarderReplies.TopHealer)
        {
            // Healing is not tracked by this server, so nobody is ever in the healers' top five.
            return [BattlefieldAwarderReplies.OnlyTopHealers];
        }
        if (board is not (BattlefieldAwarderReplies.MostKills or BattlefieldAwarderReplies.BestScore))
        {
            return [BattlefieldAwarderReplies.NotJoinedOrError];
        }

        var kills = board == BattlefieldAwarderReplies.MostKills;
        var record = PickBattlefieldReward(
            await BattlefieldRewardsAsync(store, BattlefieldRewardPolicy.Pindus, character.Id, cancellationToken),
            r => (kills ? BattlefieldRewardPolicy.PindusKillTitle(r.KillRank) : BattlefieldRewardPolicy.PindusTaskTitle(r.TaskRank)) != 0 &&
                 (r.TitlesClaimed & (kills ? PindusKillTitleFlag : PindusTaskTitleFlag)) == 0);
        if (record is null)
        {
            return [BattlefieldAwarderReplies.NotJoinedOrError];
        }

        var rank = kills ? record.KillRank : record.TaskRank;
        var flag = kills ? PindusKillTitleFlag : PindusTaskTitleFlag;
        var titleId = kills ? BattlefieldRewardPolicy.PindusKillTitle(rank) : BattlefieldRewardPolicy.PindusTaskTitle(rank);
        if (titleId == 0)
        {
            return [BattlefieldAwarderReplies.OnlyTopFive];
        }
        if ((record.TitlesClaimed & flag) != 0)
        {
            return [BattlefieldAwarderReplies.TitleClaimed];
        }
        if (BattlefieldClaimExpired(record))
        {
            return [BattlefieldAwarderReplies.ClaimOnBattleDay];
        }
        if (!await GrantBattlefieldTitleAsync(store, record, flag, titleId, PostgresGameStore.PindusTitleGrantor,
                cancellationToken))
        {
            return [BattlefieldAwarderReplies.TitleClaimed];
        }

        // 1st place: "Successfully obtained"; 2nd-5th: the client's own place lines 4002-4005 / 5002-5005.
        return [rank == 1 ? BattlefieldAwarderReplies.TitleObtained : (kills ? 4000 : 5000) + rank];
    }

    private async Task<int[]> PindusStatisticsAsync(
        IBattlefieldRoundStore store, GameCharacter character, int board, CancellationToken cancellationToken)
    {
        var stats = await store.GetBattlefieldStatisticsAsync(
            BattlefieldRewardPolicy.Pindus, character.Camp, character.Id, cancellationToken);
        if (stats is null || board == BattlefieldAwarderReplies.TopFiveHeals)
        {
            return [BattlefieldAwarderReplies.NoStatistics];
        }

        switch (board)
        {
            case BattlefieldAwarderReplies.TopFiveScores:
            case BattlefieldAwarderReplies.TopFiveKills:
            {
                var scores = board == BattlefieldAwarderReplies.TopFiveScores;
                var values = scores ? stats.TopPoints : stats.TopKills;
                var rows = values.Select((value, index) =>
                    BattlefieldAwarderReplies.RankingRow(value, (scores ? 1 : 11) + index)).ToList();
                rows.Add(BattlefieldAwarderReplies.OwnScore(stats.OwnPoints));
                return [.. rows];
            }
            case BattlefieldAwarderReplies.FactionPoints:
                return BattlefieldAwarderReplies.FactionPointRows(stats.AthensPoints, stats.SpartaPoints);
            default:
                return [BattlefieldAwarderReplies.NoStatistics];
        }
    }

    private async Task<int[]> NiMiniPageAsync(
        IBattlefieldRoundStore store, GameCharacter character, CancellationToken cancellationToken)
    {
        var records = await BattlefieldRewardsAsync(store, BattlefieldRewardPolicy.NiMini, character.Id, cancellationToken);
        return records.Count == 0
            ? [await NoBattlefieldRewardLineAsync(store, BattlefieldRewardPolicy.NiMini, character.Id, cancellationToken)]
            : [BattlefieldAwarderReplies.ClaimFaction, BattlefieldAwarderReplies.ClaimTitle];
    }

    private async Task<int[]> ClaimNiMiniReputationAsync(
        IBattlefieldRoundStore store, GameCharacter character, CancellationToken cancellationToken)
    {
        var record = PickBattlefieldReward(
            await BattlefieldRewardsAsync(store, BattlefieldRewardPolicy.NiMini, character.Id, cancellationToken),
            r => !r.FactionClaimed);
        if (record is null)
        {
            return [BattlefieldAwarderReplies.NotJoinedOrError];
        }
        if (record.FactionClaimed)
        {
            return [BattlefieldAwarderReplies.ReputationClaimed];
        }
        if (BattlefieldClaimExpired(record))
        {
            return [BattlefieldAwarderReplies.ClaimOnBattleDay];
        }

        var reputation = ServerRates.Current.Multiply(ServerRateKeys.BattlefieldReputation, record.Reward.Reputation);
        if (!await PayBattlefieldClaimAsync(store, record, BattlefieldClaimPart.Faction, reputation, cancellationToken))
        {
            return [BattlefieldAwarderReplies.ReputationClaimed];
        }
        return [BattlefieldAwarderReplies.NiMiniReputationLine(record.Outcome, record.Faction)];
    }

    private async Task<int[]> ClaimNiMiniTitleAsync(
        IBattlefieldRoundStore store, GameCharacter character, CancellationToken cancellationToken)
    {
        var record = PickBattlefieldReward(
            await BattlefieldRewardsAsync(store, BattlefieldRewardPolicy.NiMini, character.Id, cancellationToken),
            r => r.FactionRank == 1 && (r.TitlesClaimed & NiMiniTitleFlag) == 0);
        if (record is null)
        {
            return [BattlefieldAwarderReplies.NotJoinedOrError];
        }
        // IGG: the top scorer of each faction.
        if (record.FactionRank != 1)
        {
            return [BattlefieldAwarderReplies.OnlyBravest];
        }
        if ((record.TitlesClaimed & NiMiniTitleFlag) != 0)
        {
            return [BattlefieldAwarderReplies.TitleClaimed];
        }
        if (BattlefieldClaimExpired(record))
        {
            return [BattlefieldAwarderReplies.ClaimOnBattleDay];
        }
        await GrantBattlefieldTitleAsync(store, record, NiMiniTitleFlag,
            BattlefieldRewardPolicy.NiMiniTitle(record.Faction), PostgresGameStore.NiMiniTitleGrantor, cancellationToken);
        return [BattlefieldAwarderReplies.TitleClaimed];
    }

    /// <summary>
    /// The farm's faction reward. Live pays it automatically within 20 s (GameClientHandler.LelantineFarmRounds.cs),
    /// and that stays: the Awarder claims through the farm's own atomic claim (ClaimFarmRoundRewardsAsync), so
    /// whichever comes first pays and the other sees it claimed. The reward line is built from the claim THIS click
    /// actually took and paid; when it took none (auto-pay was first) or the payment failed (the claim is put back,
    /// exactly as auto-pay does, and the next auto-pay tick retries it) the answer is 204.
    /// </summary>
    private async Task<int[]> ClaimFarmAtAwarderAsync(
        IBattlefieldRoundStore store, GameCharacter character, CancellationToken cancellationToken)
    {
        var status = await store.GetLatestFarmRewardAsync(character.Id, cancellationToken);
        if (status is null)
        {
            return [BattlefieldAwarderReplies.FarmNotJoined];
        }
        if (status.Claimed || FarmRoundStore is not { } farm || _account is not { } account)
        {
            return [BattlefieldAwarderReplies.FarmAlreadyClaimed];
        }

        FarmRoundRewardClaim? paid = null;
        foreach (var claim in await farm.ClaimFarmRoundRewardsAsync(character.Id, cancellationToken))
        {
            try
            {
                await PayFarmRoundRewardAsync(account.Id, character, claim, cancellationToken);
                paid = claim;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[battlefield] awarder farm payment failed character={character.Name} round={claim.RoundStart:u}: " +
                    error.Message);
                await farm.UnclaimFarmRoundRewardAsync(character.Id, claim.RoundStart, CancellationToken.None);
                break;
            }
        }
        if (paid is null)
        {
            return [BattlefieldAwarderReplies.FarmAlreadyClaimed];
        }

        var lost = paid.Outcome == FarmRoundOutcome.Lost;
        return [BattlefieldAwarderReplies.FarmReward(
            BattlefieldAwarderReplies.FarmLevelFromExperience(paid.CharacterExperience, lost), lost)];
    }

    private static async Task<int[]> FarmFirstPlaceAsync(
        IBattlefieldRoundStore store, GameCharacter character, CancellationToken cancellationToken)
    {
        var status = await store.GetLatestFarmRewardAsync(character.Id, cancellationToken);
        // The Hero of Lelantine title itself is granted when the round settles (live behaviour); this only reports it.
        return status is null
            ? [BattlefieldAwarderReplies.FarmDidNotJoin]
            : [status.RankOverall == 1 ? BattlefieldAwarderReplies.FarmFirst : BattlefieldAwarderReplies.FarmNotFirst];
    }

    /// <summary>Marks the part claimed, adds the reputation and refreshes the client's honour balance.</summary>
    private async Task<bool> PayBattlefieldClaimAsync(
        IBattlefieldRoundStore store, BattlefieldRewardRecord record, BattlefieldClaimPart part, int reputation,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character)
        {
            return false;
        }
        var receipt = await store.ClaimBattlefieldRewardAsync(
            record.Battlefield, record.RoundStart, character.Id, part, reputation, cancellationToken);
        if (receipt is null)
        {
            return false;
        }

        if (reputation > 0 && character.MedusaRewardRevision <= receipt.RewardRevisionAfter)
        {
            character.MedusaHonorPoints = receipt.HonorAfter;
            character.MedusaRewardRevision = receipt.RewardRevisionAfter;
            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            var detail = PacketBuilder.PlayerDetail(character);
            if (detail.Length > 0)
            {
                await _session.SendAsync(detail, cancellationToken, "BattlefieldReputation", framed: false);
            }
        }
        Console.WriteLine(
            $"[battlefield] claim paid character={character.Name} battlefield={record.Battlefield} " +
            $"round={record.RoundStart:u} part={part} outcome={record.Outcome} reputation={reputation} " +
            $"honor={receipt.HonorAfter}");
        return true;
    }

    /// <summary>
    /// EXP and talent through the shared progression path the farm and the Zeus Gift claims use. The claim (and its
    /// reputation) is already committed; when the progression write does not happen (an exception, or the session's
    /// cancellation before the store wrote), the claim is undone with <see cref="CancellationToken.None"/>: the mark is
    /// cleared and the reputation taken back, so a later click pays the whole part again. Returns false then.
    /// When the progression store already wrote (the character's level/EXP/talent moved) and a later step (a packet
    /// to a closing session) failed, the claim stands: undoing it would pay the EXP twice.
    /// </summary>
    private async Task<bool> PayBattlefieldProgressionAsync(
        IBattlefieldRoundStore store, BattlefieldRewardRecord record, BattlefieldClaimPart part, int reputation,
        int experience, int talentPoints, string reason, CancellationToken cancellationToken)
    {
        if (_character is not { } character)
        {
            return false;
        }

        var before = (character.Level, character.Experience, character.TalentExperience, character.TalentPoints);
        try
        {
            await PayZeusGiftProgressionAsync(
                experience,
                checked(talentPoints * Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint),
                reason, cancellationToken);
            Console.WriteLine(
                $"[battlefield] progression paid character={character.Name} reason={reason} exp={experience} " +
                $"talent-points={talentPoints}");
            return true;
        }
        catch (Exception error)
        {
            if (before != (character.Level, character.Experience, character.TalentExperience, character.TalentPoints))
            {
                Console.Error.WriteLine(
                    $"[battlefield] progression written, a later step failed character={character.Name} " +
                    $"reason={reason}: {error.Message}");
                if (error is OperationCanceledException) throw;
                return true;
            }

            Console.Error.WriteLine(
                $"[battlefield] progression payment failed, claim undone character={character.Name} reason={reason} " +
                $"exp={experience} talent-points={talentPoints}: {error.Message}");
            var receipt = await store.UnclaimBattlefieldRewardAsync(
                record.Battlefield, record.RoundStart, character.Id, part, reputation, CancellationToken.None);
            if (receipt is not null && reputation > 0)
            {
                character.MedusaHonorPoints = receipt.HonorAfter;
                character.MedusaRewardRevision = receipt.RewardRevisionAfter;
                _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            }
            if (error is OperationCanceledException) throw;
            return false;
        }
    }

    private async Task<bool> GrantBattlefieldTitleAsync(
        IBattlefieldRoundStore store, BattlefieldRewardRecord record, short flag, int titleId, string grantedBy,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character ||
            !await store.ClaimBattlefieldTitleAsync(record.Battlefield, record.RoundStart, character.Id, flag, titleId,
                grantedBy, $"Battlefield {record.Battlefield} round {record.RoundStart:u}", cancellationToken))
        {
            return false;
        }

        character.AddOwnedTitle((uint)titleId);
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        await _session.SendAsync(
            PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds),
            cancellationToken,
            "BattlefieldTitleOwnership");
        Console.WriteLine(
            $"[battlefield] title granted character={character.Name} battlefield={record.Battlefield} title={titleId}");
        return true;
    }
}
