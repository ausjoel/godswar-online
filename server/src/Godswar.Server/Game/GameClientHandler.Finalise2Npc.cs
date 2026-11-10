using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

// FINALISE2 (P4, 2026-11-05): NPC exchanges from the admin's answers F-28 (B-Gold Trader), F-72 (Mystery Merchant),
// F-48 (Cadian Generals) and F-53 (War Supplier buy-back). Every move runs in one EventsNpc transaction with the
// character row locked, then the character is reloaded (RefreshAfterEventsNpcAsync). Each switch is a hot rate in
// Application/Finalise2/Finalise2Rates.cs; at 0 the old menu-only answer runs. Development/finalise2/p4-handlers.
internal sealed partial class GameClientHandler
{
    // FINALISE2: client function numbers (NpcFun.lua) and the client's own result numbers.
    private const int BGoldTraderFunction = 100;      // NpcFunMoney.lua
    private const int MysteryMerchantFunction = 110;  // NpcFunSecretShop.lua
    private const int CadianFunction = 94;            // NpcFunJunpai.lua
    private const int WarSupplierFunction = 114;      // NpcFunMaterialBack.lua
    private const int HolyStoneRedeemerFunction = 38; // NpcFunHolyPackage.lua (HolyPackage1-10 in the 1.4.1 client)
    private const int SummerGiftEnvoyFunction = 66;   // Mid-autumn Gift Pack Giver, captured on Sparta_115

    private const int BGoldTraderPrice = 180;          // Gold (client MONEYCHANGE6)
    private const int RebirthSpringItem = 11010;       // Spring Water (Restricted) = 重生泉水(限定版)
    private const int RebirthSpringPrice = 21000;
    private const int MorningDewItem = 10141;          // Morning Dew 2 (Restricted) = 中级仙宠灵露(限定版)
    private const int MorningDewDailyMaximum = 30;
    private const int CadianPlateItem = 14033;
    private const int WarMaterialDailyMaximum = 6;     // item text and godswar.online node 110

    // FINALISE2: B-Gold Trader (no buttons in its script: it acts on the page request) and the Cadian Generals.
    private static readonly ScriptedNpcDialogue BGoldTraderDialogue = new(
        FunctionNumber: BGoldTraderFunction, OpeningMenu: [], Steps: new Dictionary<int, int[]>());

    // FINALISE2 (F-90): the Holy Stone Redeemer opens on HolyPackage1 (101, the voucher description, ENDTEXT).
    private static readonly ScriptedNpcDialogue HolyStoneRedeemerDialogue = new(
        FunctionNumber: HolyStoneRedeemerFunction, OpeningMenu: [101], Steps: new Dictionary<int, int[]>());

    private static readonly ScriptedNpcDialogue CadianGeneralDialogue = new(
        FunctionNumber: CadianFunction, OpeningMenu: [100], Steps: new Dictionary<int, int[]>());

    // FINALISE2: war materials and their resource points (godswar.online node 110: Food 1, Weapons 2,
    // Medicine 3, Wine 4). Rewards per resource point are GENERATED: 20,000 EXP, 5,000 Silver and, in a guild,
    // 10 personal contribution; plus 1 Talent Point per delivery.
    private static readonly (int ItemId, int Points)[] WarMaterials = [(4262, 1), (4263, 2), (4264, 3), (4265, 4)];

    private async Task<bool> TryHandleFinalise2NpcAsync(NpcSpawnDefinition npc, int dialogIndex, int[] path,
        int amount, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return false;
        }

        try
        {
            return dialogIndex switch
            {
                BGoldTraderFunction when Finalise2Rates.On(Finalise2RateKeys.BGoldTrader) &&
                    npc.NpcKey is "Athens_133" or "Sparta_133" =>
                    await HandleBGoldTraderAsync(npc, cancellationToken),
                MysteryMerchantFunction when Finalise2Rates.On(Finalise2RateKeys.MysteryMerchant) &&
                    path is [101] or [102] =>
                    await HandleMysteryMerchantAsync(npc, path[0], cancellationToken),
                // ROUND3 summons (F-27): the Sacred Sealer's three "Buy 1 ... Scroll" buttons.
                SacredSealerFunction when Finalise2Rates.On(Finalise2RateKeys.SacredSealer) &&
                    path is [102] or [103] or [104] =>
                    await HandleSacredSealerAsync(npc, path[0], cancellationToken),
                CadianFunction when Finalise2Rates.On(Finalise2RateKeys.CadianPlates) =>
                    await HandleCadianGeneralAsync(npc, path, amount, cancellationToken),
                WarSupplierFunction when Finalise2Rates.On(Finalise2RateKeys.WarSupplierBuyBack) && path.Length == 0 =>
                    await HandleWarSupplierAsync(cancellationToken),
                // F-90: the vouchers have no Use flag in the client, so the NPCs their text names take them when their
                // page is opened (one chat line says what was given); the page itself is then drawn as before.
                // F-140: with the redeemer's three pages on (npc.holy_stone_refund), page 38 is information only and
                // the exchange is page 39 (GameClientHandler.HolyStoneRedeemerPages.cs); this is the switch-off path.
                HolyStoneRedeemerFunction when Finalise2Rates.On(Finalise2RateKeys.Vouchers) && path.Length == 0 &&
                    !HolyStoneRedeemerPagesOn =>
                    await RedeemCarriedVouchersAsync([14053, HolyBoxTwoVoucher, HolyBoxThreeVoucher], cancellationToken),
                SummerGiftEnvoyFunction when Finalise2Rates.On(Finalise2RateKeys.Vouchers) && path.Length == 0 &&
                    npc.NpcKey is "Athens_115" or "Sparta_115" =>
                    await RedeemCarriedVouchersAsync([14013], cancellationToken),
                _ when dialogIndex == GuildAltarDialogue.FunctionNumber && path.Length == 0 &&
                    npc.NpcKey is "Athens_050" or "Sparta_050" && Finalise2Rates.On(Finalise2RateKeys.Vouchers) =>
                    await OfferCarriedSacrificialBagAsync(cancellationToken),
                _ => false
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[finalise2-npc] fault npc={npc.NpcKey} function={dialogIndex}: " +
                $"{error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private Task SendFinalise2ReplyAsync(NpcSpawnDefinition npc, int function, int[] reply, string reason,
        CancellationToken cancellationToken) =>
        SendEventsNpcReplyAsync(npc, function, reply, reason, cancellationToken);

    // ---- F-28 B-Gold Trader -----------------------------------------------------------------------------
    private async Task<bool> HandleBGoldTraderAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        var now = EventsNpcNow;
        var local = _realmCalendar.ToRealmTime(now);
        var amount = Finalise2Rates.Amount(Finalise2RateKeys.BGoldTraderAmount);
        int result;
        if (local.TimeOfDay >= new TimeSpan(23, 55, 0))
        {
            result = 999; // MONEYCHANGE5: only between 12:00AM and 11:55PM
        }
        else
        {
            result = await RunEventsNpcAsync(async tx =>
            {
                if (await tx.DailyUsedAsync("bgold_trader") > 0)
                {
                    return 996; // MONEYCHANGE4: already today
                }

                if (await tx.TrySpendGoldAsync(BGoldTraderPrice) is null)
                {
                    return 998; // MONEYCHANGE6: not enough Gold
                }

                await tx.AddBindingGoldAsync(amount);
                await tx.DailyAddAsync("bgold_trader", 1);
                return 997;
            }, 999, cancellationToken);
        }

        Console.WriteLine($"[finalise2-npc] bgold-trader character={_character!.Name} result={result} b-gold={amount}");
        if (result == 997)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        await SendFinalise2ReplyAsync(npc, BGoldTraderFunction, [result], "BGoldTrader", cancellationToken);
        if (result == 997)
        {
            // The client's MONEYCHANGE3 says 900; this line says what was really paid.
            await SendEventsNpcNoticeAsync($"You exchanged {BGoldTraderPrice} Gold for {amount} B-Gold.",
                "BGoldTraderNotice", cancellationToken);
        }

        return true;
    }

    // ---- F-72 Mystery Merchant --------------------------------------------------------------------------
    private async Task<bool> HandleMysteryMerchantAsync(NpcSpawnDefinition npc, int button,
        CancellationToken cancellationToken)
    {
        var spring = button == 101;
        var itemId = spring ? RebirthSpringItem : MorningDewItem;
        var price = 0;
        var result = await RunEventsNpcAsync(async tx =>
        {
            var used = await tx.DailyUsedAsync(spring ? "mystery_spring" : "mystery_dew");
            if (used >= (spring ? 1 : MorningDewDailyMaximum))
            {
                return 903; // SecretShop_08: that's all for today
            }

            price = spring ? RebirthSpringPrice : 200 + 100 * used; // SecretShop_01: 200, then +100 each
            if (await tx.TrySpendGoldAsync(price) is null)
            {
                return 902; // SecretShop_07: not enough Gold
            }

            if (!await tx.GrantAsync(itemId, 1, bound: null))
            {
                tx.Rollback();
                return 901; // SecretShop_06: bag full
            }

            await tx.DailyAddAsync(spring ? "mystery_spring" : "mystery_dew", 1);
            return spring ? 201 : 202;
        }, 903, cancellationToken);
        Console.WriteLine($"[finalise2-npc] mystery-merchant character={_character!.Name} button={button} " +
            $"result={result} price={price}");
        if (result is 201 or 202)
        {
            await RefreshAfterEventsNpcAsync([(itemId, 1)], cancellationToken);
        }

        await SendFinalise2ReplyAsync(npc, MysteryMerchantFunction, [result], "MysteryMerchant", cancellationToken);
        return true;
    }

    // ---- F-48 Cadian Generals ---------------------------------------------------------------------------
    private async Task<bool> HandleCadianGeneralAsync(NpcSpawnDefinition npc, int[] path, int amount,
        CancellationToken cancellationToken)
    {
        if (path.Length == 0 && amount <= 0)
        {
            await SendFinalise2ReplyAsync(npc, CadianFunction, [100], "CadianPage", cancellationToken);
            return true;
        }

        if (amount is < 1 or > 99)
        {
            await SendFinalise2ReplyAsync(npc, CadianFunction, [999], "CadianRange", cancellationToken);
            return true;
        }

        var accepted = await RunEventsNpcAsync(async tx => await tx.ConsumeAsync(CadianPlateItem, amount),
            false, cancellationToken);
        Console.WriteLine($"[finalise2-npc] cadian character={_character!.Name} plates={amount} accepted={accepted}");
        if (!accepted)
        {
            await SendFinalise2ReplyAsync(npc, CadianFunction, [998], "CadianShort", cancellationToken);
            return true;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        var each = Finalise2Rates.Amount(Finalise2RateKeys.CadianPlateExperience);
        // The client text's own number, paid as is (not multiplied by exp.events).
        await PayEventsNpcExperienceAsync((long)each * amount, "CadianPlates", cancellationToken, eventRate: false);
        // The client draws (SubID - 1) / 100 * 1000 EXP, so SubID = plates * 100 + 1.
        await SendFinalise2ReplyAsync(npc, CadianFunction, [amount * 100 + 1], "CadianDone", cancellationToken);
        return true;
    }

    // ---- F-53 War Supplier buy-back ---------------------------------------------------------------------
    /// <summary>
    /// The War Supplier's client page is the Zeus' Oracle deposit list (NpcFunMaterialBack.lua) and the client has
    /// no buy-back page, so carried war materials are handed in when the supplier's page is opened; one chat line
    /// says what was paid. The page itself is then drawn as before (returns false).
    /// </summary>
    private async Task<bool> HandleWarSupplierAsync(CancellationToken cancellationToken)
    {
        var handed = new List<(int ItemId, int Count)>();
        long experience = 0;
        var silver = 0;
        var points = 0;
        var talent = 0;
        var contribution = 0;
        var done = await RunEventsNpcAsync(async tx =>
        {
            var left = WarMaterialDailyMaximum - await tx.DailyUsedAsync("war_materials");
            foreach (var (itemId, value) in WarMaterials.OrderByDescending(static m => m.Points))
            {
                if (left <= 0)
                {
                    break;
                }

                var take = Math.Min(left, await tx.CountAsync(itemId));
                if (take <= 0 || !await tx.ConsumeAsync(itemId, take))
                {
                    continue;
                }

                handed.Add((itemId, take));
                points += value * take;
                talent += take;
                left -= take;
            }

            if (handed.Count == 0)
            {
                return false;
            }

            experience = 20_000L * points;
            silver = 5_000 * points;
            await tx.AddSilverAsync(silver);
            await tx.AddTalentPointsAsync(talent);
            if (await tx.ScalarAsync("SELECT EXISTS (SELECT 1 FROM public.guild_members WHERE character_id = @c);")
                is true)
            {
                contribution = 10 * points;
                await tx.ScalarAsync(
                    "UPDATE public.character_base SET consortia_contribute = consortia_contribute + @n WHERE id = @c;",
                    ("n", contribution));
            }

            await tx.DailyAddAsync("war_materials", handed.Sum(static h => h.Count));
            return true;
        }, false, cancellationToken);
        if (!done)
        {
            return false;
        }

        Console.WriteLine($"[finalise2-npc] war-supplier character={_character!.Name} handed=" +
            $"{string.Join(',', handed.Select(static h => $"{h.ItemId}x{h.Count}"))} points={points} exp={experience} " +
            $"silver={silver} tp={talent} contribution={contribution}");
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await PayEventsNpcExperienceAsync(experience, "WarSupplier", cancellationToken);
        await SendEventsNpcNoticeAsync(
            $"War Supplier: {handed.Sum(static h => h.Count)} war material(s) handed in for {silver} Silver, " +
            $"{talent} Talent Point(s)" + (contribution > 0 ? $", {contribution} contribution" : "") +
            " and EXP.", "WarSupplierNotice", cancellationToken);
        return false; // the page is still drawn
    }

    // ---- F-90 vouchers at their NPCs --------------------------------------------------------------------
    /// <summary>Redeems every carried voucher of the listed kinds (one at a time); returns false so the page opens.</summary>
    private async Task<bool> RedeemCarriedVouchersAsync(int[] vouchers, CancellationToken cancellationToken)
    {
        foreach (var voucher in vouchers)
        {
            for (var n = 0; n < 99 && await CountCarriedAsync(voucher, cancellationToken) > 0; n++)
            {
                var ok = voucher switch
                {
                    HolyBoxTwoVoucher or HolyBoxThreeVoucher => await UseHolyBoxVoucherAsync(voucher, cancellationToken),
                    _ => await ExchangeVoucherAsync(voucher, voucher == 14013 ? 14012 : HolyStonePackage, cancellationToken)
                };
                if (!ok)
                {
                    break;
                }
            }
        }

        return false;
    }

    private Task<int> CountCarriedAsync(int itemId, CancellationToken cancellationToken) =>
        RunEventsNpcAsync(tx => tx.CountAsync(itemId), 0, cancellationToken);

    /// <summary>F-90: one voucher for its stated item (14013 -> 14012, 14053 -> 14054). False when nothing moved.</summary>
    private async Task<bool> ExchangeVoucherAsync(int voucher, int reward, CancellationToken cancellationToken)
    {
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(voucher, 1) || !await tx.GrantAsync(reward, 1, bound: null))
            {
                tx.Rollback();
                return false;
            }

            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[finalise2-npc] voucher character={_character!.Name} voucher={voucher} reward={reward} done={done}");
        if (done)
        {
            await RefreshAfterEventsNpcAsync([(reward, 1)], cancellationToken);
            await SendGainLogAsync("Obtained: " + GainItemList([((uint)reward, 1)]), cancellationToken);
        }

        return done;
    }

    /// <summary>F-90: the Altar Supervisor takes a carried Sacrificial Bag; returns false so the altar page opens.</summary>
    private async Task<bool> OfferCarriedSacrificialBagAsync(CancellationToken cancellationToken)
    {
        if (await CountCarriedAsync(SacrificialBag, cancellationToken) > 0)
        {
            await UseSacrificialBagAsync(cancellationToken);
        }

        return false;
    }
}
