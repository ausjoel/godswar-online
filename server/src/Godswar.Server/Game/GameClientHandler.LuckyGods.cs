using Godswar.Server.Application.LuckyGods;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The divine wish's first page: the rules line with the Poseidon button, the
    /// Apollo button and the claim entry, which the script raises at
    /// <c>25,175</c>, <c>25,195</c> and <c>25,215</c>.
    /// </summary>
    private static readonly int[] LuckyGodsMenu = [100, 101, 1000];

    /// <summary>
    /// Runs one click of the divine wish: gate, roll, streak, pool, claim.
    /// </summary>
    /// <remarks>
    /// The numbers this sends are read off <c>NpcFunLuckyGods.lua</c>: the tail-9
    /// family prints the pool, the tail-2 family prints the seventh and final
    /// correct guess, the tail-8 family prints the wishes left today, and the
    /// tail-6 family prints the minutes of wait. Which of them a click deserves is
    /// the server's own rule, and the rule is the user's: eighty percent of guesses
    /// hit, the pool doubling per consecutive hit up to the seventh, a miss wiping
    /// the pool and restarting the ladder, five minutes between wishes, ten wishes a
    /// day, and the pool paid out only when the player presses the claim button.
    /// </remarks>
    private async Task HandleLuckyGodsActionAsync(
        uint npcId,
        int page,
        int selection,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (_luckyGodsWish is null)
        {
            Console.WriteLine(
                "[lucky-gods] wish unavailable: no durable wish state");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var state = await _luckyGodsWish.ReadAsync(
            _character.Id,
            _realmCalendar,
            now,
            cancellationToken);

        if (LuckyGodsWishPolicy.IsClaimClick(selection))
        {
            await SendLuckyGodsClaimAsync(
                npcId,
                page,
                state.Remaining,
                now,
                cancellationToken);
            return;
        }

        if (LuckyGodsWishPolicy.IsGuessClick(selection))
        {
            await SendLuckyGodsGuessAsync(
                npcId,
                page,
                selection,
                state,
                now,
                cancellationToken);
            return;
        }

        // Anything else is the request that opens the page: the client echoes the
        // page in the option words and puts -1 in the argument (captured
        // 2026-09-15, `10069 {5212, 16, 16, -1}` -> `10070 {5212, 16, 101, 201}`).
        Console.WriteLine(
            $"[lucky-gods] page request npc={npcId} page={page} " +
            $"selection={selection}");
        await SendLuckyGodsOpeningAsync(npcId, page, state, cancellationToken);
    }

    /// <summary>
    /// Answers the request that opens the page. The level and allowance gates have to
    /// be spent here: their client lines (<c>NF_L0_L012</c> and <c>NF_L0_L013</c>)
    /// are branches of <c>Index == 1</c>, so the same numbers fall into other
    /// branches once the player is one click deep.
    /// </summary>
    private async Task SendLuckyGodsOpeningAsync(
        uint npcId,
        int page,
        LuckyGodsWishState state,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character)
        {
            return;
        }

        if (character.Level < LuckyGodsWishPolicy.MinimumLevel)
        {
            await SendWishingPoolPageAsync(
                npcId, page, [1001], "LuckyGodsLevelGate", cancellationToken);
            return;
        }

        if (state.Remaining <= 0)
        {
            await SendWishingPoolPageAsync(
                npcId, page, [104], "LuckyGodsExhausted", cancellationToken);
            return;
        }

        await SendWishingPoolPageAsync(
            npcId, page, LuckyGodsMenu, "LuckyGodsMenu", cancellationToken);
    }

    /// <summary>
    /// One guess. The wish is booked before anything is sent, so a disconnect
    /// cannot be used to reroll a lost streak.
    /// </summary>
    private async Task SendLuckyGodsGuessAsync(
        uint npcId,
        int page,
        int selection,
        LuckyGodsWishState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || _luckyGodsWish is not { } store)
        {
            return;
        }

        if (state.Streak >= LuckyGodsWishPolicy.MaximumStreak)
        {
            // The seventh is already won and unclaimed, so the only honest answer is
            // the same seventh line; the pool waits for the claim button.
            await SendWishingPoolPageAsync(
                npcId,
                page,
                [LuckyGodsWishPolicy.CompletedSubId(state.PendingExperience)],
                "LuckyGodsStreakComplete",
                cancellationToken);
            return;
        }

        if (state.LastUsedAt is { } lastUsedAt &&
            lastUsedAt + LuckyGodsWishPolicy.Interval > now)
        {
            var minutes = (int)Math.Ceiling(
                (lastUsedAt + LuckyGodsWishPolicy.Interval - now).TotalMinutes);
            await SendWishingPoolPageAsync(
                npcId,
                page,
                [LuckyGodsWishPolicy.WaitingSubId(minutes)],
                "LuckyGodsWaiting",
                cancellationToken);
            return;
        }

        if (state.Remaining <= 0)
        {
            await SendWishingPoolPageAsync(
                npcId,
                page,
                [LuckyGodsWishPolicy.MissedSubId(0)],
                "LuckyGodsNoWishesLeft",
                cancellationToken);
            return;
        }

        var won = LuckyGodsWishPolicy.IsHit(Random.Shared.Next(100));
        var streak = won ? state.Streak + 1 : 0;
        // A wrong guess costs the whole pot and starts the ladder again; the only
        // other ways the pot leaves the row are the claim button and the realm's
        // midnight, after which the client's own line is "第二天可就不算数啦".
        var pool = LuckyGodsWishPolicy.PoolAfter(streak);
        await store.RecordGuessAsync(
            character.Id,
            _realmCalendar,
            now,
            streak,
            pool,
            cancellationToken);
        var reply = LuckyGodsWishPolicy.ReplyFor(
            streak,
            pool,
            state.Remaining - 1);
        Console.WriteLine(
            $"[lucky-gods] wish character={character.Name} " +
            $"choice={selection} streak={streak} pool={pool} " +
            $"wishes-left={state.Remaining - 1} " +
            $"reply=[{string.Join(',', reply)}]");
        await SendWishingPoolPageAsync(
            npcId, page, reply, "LuckyGodsGuess", cancellationToken);
    }

    /// <summary>
    /// Pays the accumulated pool out as experience. The pool is taken from the
    /// state row first, so a failure while the experience is being written costs
    /// the player a prize rather than minting a second one.
    /// </summary>
    private async Task SendLuckyGodsClaimAsync(
        uint npcId,
        int page,
        int wishesRemaining,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character ||
            _account is not { } account ||
            _luckyGodsWish is not { } store)
        {
            return;
        }

        var claimed = await store.ClaimAsync(
            character.Id, _realmCalendar, now, cancellationToken);
        if (claimed <= 0)
        {
            // NF_L0_L016, which is the script's own line for an empty pool.
            await SendWishingPoolPageAsync(
                npcId, page, [201], "LuckyGodsNothingToClaim", cancellationToken);
            return;
        }

        var progression = await _store.ApplyMonsterKillRewardAsync(
            account.Id,
            character.Id,
            // RATES: event NPC EXP multiplier (the wish pool itself is unchanged).
            Godswar.Server.Application.Rates.ServerRates.Current.Multiply(
                Godswar.Server.Application.Rates.ServerRateKeys.EventExperience,
                claimed,
                Godswar.Server.Application.Rewards.MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience), // ROUND2
            talentExperience: 0,
            cancellationToken);
        if (progression is null)
        {
            Console.Error.WriteLine(
                $"[lucky-gods] claim lost character={character.Name} " +
                $"character-id={character.Id} account-id={account.Id} " +
                $"exp={claimed}: the store wrote nothing");
            await SendWishingPoolPageAsync(
                npcId, page, [201], "LuckyGodsClaimFailed", cancellationToken);
            return;
        }

        character.Level = progression.CurrentLevel;
        character.Experience = progression.CurrentExperience;
        character.TalentExperience = progression.CurrentTalentExperience;
        character.TalentPoints = progression.CurrentTalentPoints;
        if (progression.LevelUps.Count > 0)
        {
            await RefreshLevelUpStatsAsync(character, cancellationToken);
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        foreach (var levelUp in progression.LevelUps)
        {
            var experienceMaximum =
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    levelUp.Level,
                    character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    character.MaxHp,
                    character.CurrentHp,
                    character.MaxMp,
                    character.CurrentMp),
                cancellationToken,
                "LuckyGodsLevelUp");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    character.MaxHp,
                    character.CurrentHp,
                    character.MaxMp,
                    character.CurrentMp),
                cancellationToken,
                _session,
                "LuckyGodsLevelUpWorld");
        }

        await _session.SendAsync(
            PacketBuilder.ExperienceGain(claimed, character.Experience),
            cancellationToken,
            "LuckyGodsExperienceGain");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "LuckyGodsClaimStatus");
        Console.WriteLine(
            $"[lucky-gods] claim character={character.Name} exp={claimed} " +
            $"level={character.Level} exp-now={character.Experience} " +
            $"wishes-left={wishesRemaining}");
        await SendWishingPoolPageAsync(
            npcId,
            page,
            [LuckyGodsWishPolicy.ClaimedSubId(wishesRemaining)],
            "LuckyGodsClaimed",
            cancellationToken);
    }
}
