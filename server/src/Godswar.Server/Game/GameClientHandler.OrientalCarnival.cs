using Godswar.Server.Application.Seasons;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using S = Godswar.Server.Application.Seasons.OrientalCarnivalSeason;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasons oriental / carnival: the Oriental Cuisine Master (functions 102 NpcFunMooncake.lua and 101
/// NpcFunFood.lua) and the Carnival Envoy (74, NpcPermit.lua), only while their season is open and their switch is on
/// (<see cref="OrientalCarnivalSeason"/>). Replies are the scripts' own result numbers; no pop-up follows an exchange
/// (the admin's rule). Nothing here is sent at world entry: every frame answers a click (after EnterUiReady).
/// <para>
/// Click path: the request's +12.. slots, one per clicked entry (page 1 entry, then page 2 entry). The page-2 numbers
/// of NpcPermit (210-235) never repeat a page-1 number, so the last click decides. Every request's words are logged as
/// <c>[oriental-carnival]</c>, because no real-client capture of these windows exists: in particular the "Repeated
/// Wish-making" button (FirstWin_ButtonA3) has never been seen on the wire; a request with no known button repeats the
/// last wish of this session.
/// </para>
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>Oriental Cuisine Master, recipes -> scroll (NpcFunMooncake.lua, NPC_FLAG_SYS_MOONCAKE = 102).</summary>
    private static readonly ScriptedNpcDialogue CuisineScrollDialogue = new(
        FunctionNumber: S.ScrollExchangeFunction,
        OpeningMenu: [100],
        Steps: new Dictionary<int, int[]>());

    /// <summary>Oriental Cuisine Master, free recipe wish (NpcFunFood.lua, NPC_FLAG_SYS_FOOD = 101).</summary>
    private static readonly ScriptedNpcDialogue CuisineWishDialogue = new(
        FunctionNumber: S.FoodWishFunction,
        OpeningMenu: [S.WishOpening(0)],
        Steps: new Dictionary<int, int[]>());

    /// <summary>Carnival Envoy, wish-making credits (NpcPermit.lua, NPC_FLAG_SYS_PERMIT = 74).</summary>
    private static readonly ScriptedNpcDialogue CarnivalEnvoyDialogue = new(
        FunctionNumber: S.CarnivalFunction,
        OpeningMenu: S.PageOneButtons,
        Steps: new Dictionary<int, int[]>
        {
            [124] = S.PageTwoButtons(124),
            [125] = S.PageTwoButtons(125),
            [126] = S.PageTwoButtons(126)
        });

    /// <summary>The last wish button of this session (for "Repeated Wish-making").</summary>
    private int _carnivalLastWish;

    private async Task<bool> TryHandleOrientalCarnivalAsync(NpcSpawnDefinition npc, int dialogIndex, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null) return false;
        var now = DateTimeOffset.UtcNow;
        var kind = dialogIndex switch
        {
            S.ScrollExchangeFunction when S.IsCuisineMaster(npc.NpcKey) && S.OrientalOpen(now) => 1,
            S.FoodWishFunction when S.IsCuisineMaster(npc.NpcKey) && S.OrientalOpen(now) => 2,
            S.CarnivalFunction when S.IsCarnivalEnvoy(npc.NpcKey) && S.CarnivalSwitchOn => 3,
            _ => 0
        };
        if (kind == 0) return false;

        var path = DialogPath(packet);
        Console.WriteLine($"[oriental-carnival] npc={npc.NpcKey} function={dialogIndex} character={_character.Name} " +
            $"path=[{string.Join(',', path)}]{EventsNpcWords(packet)}");
        try
        {
            return kind switch
            {
                1 => await CuisineScrollAsync(npc, path, cancellationToken),
                2 => await CuisineWishAsync(npc, path, cancellationToken),
                _ => await CarnivalEnvoyAsync(npc, path, S.CarnivalOpen(now), cancellationToken)
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An event fault must never end the session; the transaction did not commit.
            Console.Error.WriteLine($"[oriental-carnival] fault npc={npc.NpcKey}: {error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private Task OrientalCarnivalReplyAsync(NpcSpawnDefinition npc, int function, int[] reply, string reason,
        CancellationToken token) => SendFinalise2ReplyAsync(npc, function, reply, reason, token);

    // ---- Oriental Cuisine Master, function 102: 4 recipes -> 1 scroll ------------------------------------------------

    private async Task<bool> CuisineScrollAsync(NpcSpawnDefinition npc, int[] path, CancellationToken token)
    {
        const int f = S.ScrollExchangeFunction;
        if (path.Length == 0)
        {
            await OrientalCarnivalReplyAsync(npc, f, [100], "CuisineScrollOpen", token); // MN100 + the exchange button
            return true;
        }

        if (!path.Contains(100))
        {
            await OrientalCarnivalReplyAsync(npc, f, [900], "CuisineScrollChoose", token); // NF_L0_S009
            return true;
        }

        var character = _character!;
        var athens = character.Camp != GameDefaults.SpartaCamp;
        var allPercent = Math.Max(0, (int)Application.Rates.ServerRates.Current.Get(S.AllScrollPercentKey));
        var scroll = S.PickScroll(athens, character.Level, Random.Shared.Next(0, int.MaxValue), allPercent);
        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.CountAsync(S.GreekCuisineRecipe) < S.RecipesPerScroll) return 902; // NF_L0_M117
            if (!await tx.ConsumeAsync(S.GreekCuisineRecipe, S.RecipesPerScroll)) return 902;
            if (await tx.GrantAsync(scroll, 1, bound: true, stackCap: 99)) return 910; // NF_L0_M110
            tx.Rollback();
            return 901; // NF_L0_M118: bag full
        }, 901, token);
        Console.WriteLine($"[oriental-carnival] scroll exchange character={character.Name} result={result} scroll={scroll}");
        if (result == 910) await RefreshAfterEventsNpcAsync([(scroll, 1)], token);
        await OrientalCarnivalReplyAsync(npc, f, [result], "CuisineScrollResult", token);
        return true;
    }

    // ---- Oriental Cuisine Master, function 101: the free recipe wish -------------------------------------------------

    private async Task<bool> CuisineWishAsync(NpcSpawnDefinition npc, int[] path, CancellationToken token)
    {
        const int f = S.FoodWishFunction;
        if (path.Length == 0)
        {
            var used = await RunEventsNpcAsync(tx => tx.DailyUsedAsync("oriental_wish"), 0, token);
            await OrientalCarnivalReplyAsync(npc, f, [S.WishOpening(used)], "CuisineWishOpen", token);
            return true;
        }

        if (path[0] % 1000 != 1)
        {
            await OrientalCarnivalReplyAsync(npc, f, [S.WishIncorrect], "CuisineWishIncorrect", token);
            return true;
        }

        var count = Random.Shared.Next(S.WishRecipesMin, S.WishRecipesMax + 1);
        var result = await RunEventsNpcAsync(async tx =>
        {
            var used = await tx.DailyUsedAsync("oriental_wish");
            if (used >= S.WishesPerDay) return S.WishNoneLeft;
            if (used > 0 && await tx.ScalarAsync(
                    "SELECT EXTRACT(EPOCH FROM now() - updated_at)::int FROM public.finalise2_npc_daily " +
                    "WHERE character_id = @c AND day = @d AND counter = 'oriental_wish';",
                    ("d", EventsNpcDay(EventsNpcNow))) is int elapsed && elapsed < S.WishGapSeconds)
                return S.WishWait(S.WishGapSeconds - elapsed);
            if (!await tx.GrantAsync(S.GreekCuisineRecipe, count, bound: true, stackCap: 99))
            {
                tx.Rollback();
                return S.WishBagFull;
            }

            await tx.DailyAddAsync("oriental_wish", 1);
            return S.WishSuccess;
        }, S.WishIncorrect, token);
        Console.WriteLine($"[oriental-carnival] recipe wish character={_character!.Name} result={result} count={count}");
        if (result == S.WishSuccess) await RefreshAfterEventsNpcAsync([(S.GreekCuisineRecipe, count)], token);
        await OrientalCarnivalReplyAsync(npc, f, [result], "CuisineWishResult", token);
        return true;
    }

    // ---- Carnival Envoy, function 74: wish-making credits ------------------------------------------------------------

    private async Task<bool> CarnivalEnvoyAsync(NpcSpawnDefinition npc, int[] path, bool open, CancellationToken token)
    {
        const int f = S.CarnivalFunction;
        if (!open)
        {
            await OrientalCarnivalReplyAsync(npc, f, [S.Closed], "CarnivalClosed", token); // NF_L0_TZN919
            return true;
        }

        if (path.Length == 0)
        {
            var credits = await RunEventsNpcAsync(async tx => CarnivalInt(await tx.ScalarAsync(CarnivalCreditsReadSql,
                ("d", S.CreditsDay), ("k", S.CreditsCounter)), 0), 0, token);
            await OrientalCarnivalReplyAsync(npc, f, [S.CreditsLine(credits, 5), .. S.PageOneButtons], "CarnivalOpen",
                token);
            return true;
        }

        var click = path[^1];
        if (click is 124 or 125 or 126)
        {
            var credits = await RunEventsNpcAsync(async tx => CarnivalInt(await tx.ScalarAsync(CarnivalCreditsReadSql,
                ("d", S.CreditsDay), ("k", S.CreditsCounter)), 0), 0, token);
            await OrientalCarnivalReplyAsync(npc, f, [S.CreditsLine(credits, 6), .. S.PageTwoButtons(click)],
                "CarnivalPageTwo", token);
            return true;
        }

        // A wish button, or (A3 "Repeated Wish-making", never captured) the last wish of this session.
        var wish = S.FindWish(click) ?? Enumerable.Reverse(path).Select(S.FindWish).FirstOrDefault(static w => w is not null) ??
            (_carnivalLastWish > 0 ? S.FindWish(_carnivalLastWish) : null);
        if (wish is not { } chosen)
        {
            await OrientalCarnivalReplyAsync(npc, f, [S.Replies(path.Length >= 2).Error], "CarnivalUnknown", token);
            return true;
        }

        _carnivalLastWish = chosen.Button;
        var replies = S.Replies(chosen.SecondPage);
        var prize = chosen.Items[Random.Shared.Next(chosen.Items.Length)];
        var lucky = Random.Shared.Next(10_000) < S.Odds(chosen.Tier);
        var (result, left) = await RunEventsNpcAsync(async tx =>
        {
            var credits = CarnivalInt(await tx.ScalarAsync(CarnivalCreditsReadSql, ("d", S.CreditsDay),
                ("k", S.CreditsCounter)), 0);
            if (credits < chosen.Cost) return (replies.NoCredits, credits);
            var remaining = CarnivalInt(await tx.ScalarAsync(CarnivalCreditsSpendSql, ("d", S.CreditsDay),
                ("k", S.CreditsCounter), ("n", chosen.Cost)), -1);
            if (remaining < 0) return (replies.NoCredits, credits);
            if (!lucky) return (replies.Fail, remaining);
            if (await tx.GrantAsync(prize, chosen.Count, bound: false, stackCap: 99)) return (replies.Success, remaining);
            tx.Rollback(); // the credits are kept when the bag is full
            return (replies.BagFull, credits);
        }, (replies.Error, 0), token);
        Console.WriteLine($"[oriental-carnival] wish character={_character!.Name} button={chosen.Button} " +
            $"cost={chosen.Cost} prize={prize}x{chosen.Count} result={result} credits={left}");
        if (result == replies.Success) await RefreshAfterEventsNpcAsync([(prize, chosen.Count)], token);
        await OrientalCarnivalReplyAsync(npc, f, [result, S.CreditsLine(left, replies.RemainingSuffix)], "CarnivalWish",
            token);
        return true;
    }

    private static int CarnivalInt(object? value, int fallback) => value is int number ? number : fallback;

    private const string CarnivalCreditsReadSql =
        "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @d AND counter = @k;";

    /// <summary>Takes @n credits; returns the balance left, or no row when there are not enough.</summary>
    private const string CarnivalCreditsSpendSql = """
        UPDATE public.finalise2_npc_daily SET used = used - @n, updated_at = now()
        WHERE character_id = @c AND day = @d AND counter = @k AND used >= @n
        RETURNING used;
        """;

    private const string CarnivalCreditsAddSql = """
        INSERT INTO public.finalise2_npc_daily (character_id, day, counter, used) VALUES (@c, @d, @k, @n)
        ON CONFLICT (character_id, day, counter) DO UPDATE
            SET used = LEAST(@max, finalise2_npc_daily.used + EXCLUDED.used), updated_at = now()
        RETURNING used;
        """;

    /// <summary>
    /// After a committed Item Mall purchase (hook in CompleteMallPurchaseAsync): Gold purchases earn wish-making
    /// credits while the Carnival season is open. No frame, no notice: the envoy shows the balance.
    /// </summary>
    private async Task OnCarnivalMallPurchaseAsync(CapitalShopOffer offer, int quantity, CancellationToken token)
    {
        if (offer.Currency != CapitalNpcShopCurrency.Gold || !S.CarnivalOpen(DateTimeOffset.UtcNow)) return;
        var earned = S.CreditsFor((long)offer.UnitPrice * Math.Max(1, quantity));
        if (earned <= 0) return;
        try
        {
            var total = await RunEventsNpcAsync(async tx => CarnivalInt(await tx.ScalarAsync(CarnivalCreditsAddSql,
                ("d", S.CreditsDay), ("k", S.CreditsCounter), ("n", earned), ("max", S.MaxCredits)), 0), -1, token);
            Console.WriteLine($"[oriental-carnival] mall credits character={_character?.Name} +{earned} total={total}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[oriental-carnival] mall credits failed: {error.Message}");
        }
    }

    /// <summary>
    /// After a quest hand-in (hook at the end of the hand-in burst): a Mysterious Oriental Scroll quest pays its
    /// Delicate Mooncakes (1, or 2 for scroll VII) unless its reward menu already offers them, and is taken off the
    /// completed list so a second scroll of the same quest can be accepted. Switch-gated only, so a scroll taken in
    /// the season still pays after it.
    /// </summary>
    private async Task OnOrientalScrollHandedInAsync(uint questId, CancellationToken token)
    {
        var cakes = S.MooncakesFor(questId);
        if (cakes <= 0 || !S.OrientalSwitchOn || _character is not { } character) return;
        try
        {
            character.QuestCompletedIds = [.. character.QuestCompletedIds.Where(id => id != questId)];
            await SaveQuestStateAsync(token);
            if (QuestRewardItemCatalog.Resolve(questId).Any(static item => item.ItemId == S.DelicateMooncake)) return;
            var paid = await RunEventsNpcAsync(
                tx => tx.GrantAsync(S.DelicateMooncake, cakes, bound: true, stackCap: 99), false, token);
            Console.WriteLine($"[oriental-carnival] scroll quest {questId} character={character.Name} " +
                $"mooncakes={cakes} paid={paid}");
            if (paid) await RefreshAfterEventsNpcAsync([(S.DelicateMooncake, cakes)], token);
            else
                await SendEventsNpcNoticeAsync("Oriental Cuisine: your bag is full, so the Delicate Mooncake could not " +
                    "be given.", "OrientalScrollBagFull", token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[oriental-carnival] scroll quest {questId} payout failed: {error.Message}");
        }
    }
}
