using Godswar.Server.Application.Seasons;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasons mid-autumn: the Lunar Priest (Athens_032 / Sparta_026, function 103, client NpcFunMoon0.lua), only while
/// the "mid_autumn" season is open and season.mid_autumn is 1 (<see cref="MidAutumnSeason"/>).
/// Page 1 (Index 1) is the stock menu 1-4 (EXP, Talent Points, Pet EXP, Zodiac energy). A choice opens page 2
/// (Index 2): its text with "Exchange 1 Delicate Mooncake" (100/110/120/130) and "Exchange 3 / 5 / 10" (101-103,
/// shared by all four pages, so the reward comes from the click path, or from the page this handler last opened).
/// The result (Index 3) is MOON910 success, MOON901 not enough cakes, MOON902 no pet out, NF_L0_S009 otherwise.
/// Amounts are GENERATED (season.mid_autumn_cake_* rates). No pop-up follows (the admin's rule): the result line only.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>The reward page this handler last opened for this player (fallback when the path is short).</summary>
    private MidAutumnSeason.Reward _lunarPriestPage;

    private async Task<bool> TryHandleLunarPriestAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null || !MidAutumnSeason.IsLunarPriest(npc.NpcKey) ||
            !MidAutumnSeason.IsOpen(DateTimeOffset.UtcNow))
            return false;
        var path = DialogPath(packet);
        // Same fallback as the generic catalog: with no path slots the sub id is the click (the Thanksgiving
        // capture carried the page-1 choice there); a fresh-page request has none.
        if (path.Length == 0 && subId > 0) path = [subId];
        Console.WriteLine($"[mid-autumn] priest character={_character.Name} sub={subId} " +
            $"path=[{string.Join(',', path)}]{EventsNpcWords(packet)}");
        if (path.Length == 0)
        {
            _lunarPriestPage = MidAutumnSeason.Reward.None;
            return false; // the stock page 1 [1,2,3,4]
        }

        var click = path[^1];
        if (path.Length == 1 && MidAutumnSeason.RewardOf(click) is var chosen and not MidAutumnSeason.Reward.None)
        {
            _lunarPriestPage = chosen;
            await SendFinalise2ReplyAsync(npc, MidAutumnSeason.LunarPriestFunction, MidAutumnSeason.PageTwo(chosen),
                "LunarPriestPage", cancellationToken);
            return true;
        }

        var (cakes, fromEntry) = MidAutumnSeason.CakesOf(click);
        var reward = fromEntry is not MidAutumnSeason.Reward.None ? fromEntry
            : path.Length >= 2 ? MidAutumnSeason.RewardOf(path[^2])
            : _lunarPriestPage;
        int result;
        if (cakes == 0 || reward is MidAutumnSeason.Reward.None) result = MidAutumnSeason.ReplyChoose;
        else result = await LunarPriestExchangeAsync(reward, cakes, cancellationToken);
        Console.WriteLine($"[mid-autumn] priest character={_character.Name} reward={reward} cakes={cakes} result={result}");
        await SendFinalise2ReplyAsync(npc, MidAutumnSeason.LunarPriestFunction, [result], "LunarPriestResult",
            cancellationToken);
        return true;
    }

    private async Task<int> LunarPriestExchangeAsync(MidAutumnSeason.Reward reward, int cakes,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (reward is MidAutumnSeason.Reward.PetExperience && !HasSummonedPet())
            return MidAutumnSeason.ReplyNoPet; // NF_L0_MOON902: keep your pet out; nothing is taken

        var amount = MidAutumnSeason.Amount(reward, cakes, character.Level);
        var zodiacGainX100 = 0;
        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.CountAsync((int)MidAutumnSeason.DelicateMooncake) < cakes)
                return MidAutumnSeason.ReplyNotEnough;
            if (reward is MidAutumnSeason.Reward.ZodiacEnergy)
            {
                zodiacGainX100 = await tx.AddZodiacEnergyAsync(checked((int)amount));
                if (zodiacGainX100 <= 0)
                {
                    tx.Rollback();
                    return MidAutumnSeason.ReplyChoose; // the store is full: keep the cakes
                }
            }

            if (!await tx.ConsumeAsync((int)MidAutumnSeason.DelicateMooncake, cakes))
            {
                tx.Rollback();
                return MidAutumnSeason.ReplyNotEnough;
            }

            if (reward is MidAutumnSeason.Reward.TalentPoints)
                await tx.AddTalentPointsAsync(checked((int)amount));
            return MidAutumnSeason.ReplySuccess;
        }, MidAutumnSeason.ReplyChoose, cancellationToken);
        if (result != MidAutumnSeason.ReplySuccess) return result;

        // Committed: repaint the bag and wallet, then pay what lives outside the transaction (like the War Supplier
        // and the farm donation).
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        switch (reward)
        {
            case MidAutumnSeason.Reward.Experience:
                await PayEventsNpcExperienceAsync(amount, "LunarPriest", cancellationToken);
                break;
            case MidAutumnSeason.Reward.TalentPoints:
                await _session.SendAsync(PacketBuilder.TalentPointGain(checked((int)amount)), cancellationToken,
                    "LunarPriestTalent");
                break;
            case MidAutumnSeason.Reward.PetExperience:
                await PayLunarPriestPetExperienceAsync(checked((int)amount), cancellationToken);
                break;
            case MidAutumnSeason.Reward.ZodiacEnergy:
                await _session.SendAsync(PacketBuilder.ZodiacEnergyIncrease(_character!.ZodiacEnergy, zodiacGainX100),
                    cancellationToken, "LunarPriestZodiac");
                await SendZodiacFullSyncAsync(cancellationToken);
                break;
        }

        Console.WriteLine($"[mid-autumn] priest paid character={character.Name} reward={reward} cakes={cakes} " +
            $"amount={amount} zodiacX100={zodiacGainX100}");
        return result;
    }

    private async Task PayLunarPriestPetExperienceAsync(int experience, CancellationToken cancellationToken)
    {
        try
        {
            var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(_account!.Id, _character!.Id,
                Guid.NewGuid(), experience, cancellationToken);
            if (pet is { HasPetProjection: true })
                await _session.SendAsync(PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                    cancellationToken, "LunarPriestPetExperience");
            else
                Console.WriteLine($"[mid-autumn] priest pet experience not applied character={_character.Name} " +
                    $"status={pet?.Status}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The cakes are already taken; a pet-experience fault must not end the session.
            Console.Error.WriteLine($"[mid-autumn] priest pet experience failed character={_character?.Name}: {ex.Message}");
        }
    }
}
