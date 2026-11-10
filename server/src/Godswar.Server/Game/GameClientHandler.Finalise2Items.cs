using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// FINALISE2 (P4, 2026-11-05): right-click handlers for items whose reward is not a plain gift pack:
// Holy Box II/III Vouchers and the Sacrificial Bag (F-90), Zeus' Baptism (F-54). Hook: InventoryActivation.cs.
// No pop-up follows a used item (the admin's rule); only the server's one chat/info line says what happened.
internal sealed partial class GameClientHandler
{
    private const int HolyBoxTwoVoucher = 14046;
    private const int HolyBoxThreeVoucher = 14047;
    private const int SacrificialBag = 3982;
    private const int SacrificialBagSilver = 500_000; // item text: "equal to a donation of 500,000 silver coins"
    private const int ZeusBaptism = 4269;
    private const int PrettyWig = 4380;       // Magic.ini skill 4700 ScriptID ChangeHairstyle
    private const int SilverHairdye = 4390;   // Magic.ini skill 4710 ScriptID ChangeHaircolor

    private async Task<bool> TryUseFinalise2ItemAsync(int itemId, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return false;
        }

        try
        {
            return itemId switch
            {
                HolyBoxTwoVoucher or HolyBoxThreeVoucher when Finalise2Rates.On(Finalise2RateKeys.Vouchers) =>
                    await UseHolyBoxVoucherAsync(itemId, cancellationToken),
                SacrificialBag when Finalise2Rates.On(Finalise2RateKeys.Vouchers) =>
                    await UseSacrificialBagAsync(cancellationToken),
                ZeusBaptism when Finalise2Rates.On(Finalise2RateKeys.TalentReset) =>
                    await UseZeusBaptismAsync(cancellationToken),
                PrettyWig or SilverHairdye when Finalise2Rates.On(Finalise2RateKeys.HairItems) =>
                    await UseHairItemAsync(itemId, cancellationToken),
                CypressLeave when Finalise2Rates.On(Finalise2RateKeys.CypressLeave) =>
                    await UseCypressLeaveAsync(cancellationToken), // W1 dead items
                HermesHoods.ItemId when Finalise2Rates.On(Finalise2RateKeys.HermesHood) =>
                    await UseHermesHoodAsync(cancellationToken), // ITEMS-HERMES F-56
                ChatBubbleOne or ChatBubbleTwo when Finalise2Rates.On(Finalise2RateKeys.ChatBubbles) =>
                    await UseChatBubbleAsync(itemId, cancellationToken), // W1 dead items
                PlumpWheat or GoldenLeaf when Finalise2Rates.On(Finalise2RateKeys.Autoloot) =>
                    await UseAutolootItemAsync(itemId, cancellationToken), // W3 autoloot
                ParcelStone => await UseParcelStoneAsync(cancellationToken), // W3: bags are always fully open
                _ when SummonItemPolicy.TryGet(itemId, out _) =>
                    await TryUseSummonItemAsync(itemId, cancellationToken), // ROUND3 runtime spawns (F-31)
                _ when ItemRefreshMonsters.TryGetTotem(itemId, out _, out _) =>
                    await TryUseTotemPoleAsync(itemId, cancellationToken), // ROUND3 summons: Totem Poles 4473-4476
                _ => false
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[finalise2-item] fault item={itemId} character={_character.Name}: " +
                $"{error.GetType().Name}: {error.Message}");
            await SendKitBagRefreshAsync(cancellationToken);
            return true;
        }
    }

    /// <summary>F-90: the voucher becomes one FULL Holy Box II / III (item EXP = the box capacity, rate-scaled).</summary>
    private async Task<bool> UseHolyBoxVoucherAsync(int voucher, CancellationToken cancellationToken)
    {
        var box = voucher == HolyBoxTwoVoucher ? 9021 : 9022;
        var capacity = ServerRates.Current.Multiply(ServerRateKeys.HolyBoxCapacity,
            voucher == HolyBoxTwoVoucher ? 1_000_000 : 10_000_000, int.MaxValue);
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(voucher, 1) || !await tx.GrantAsync(box, 1, bound: null))
            {
                tx.Rollback();
                return false;
            }

            await tx.ScalarAsync("""
                UPDATE public.character_items SET item_exp = @e WHERE id = (SELECT max(id) FROM public.character_items
                    WHERE user_id = @c AND item_location = 1 AND prop_id = @p AND item_exp = 0);
                """, ("e", capacity), ("p", box));
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[finalise2-item] holy-box-voucher character={_character!.Name} voucher={voucher} " +
            $"box={box} exp={capacity} done={done}");
        if (done)
        {
            await RefreshAfterEventsNpcAsync([(box, 1)], cancellationToken);
            await SendGainLogAsync($"Obtained: full {(box == 9021 ? "Holy Box II" : "Holy Box III")}",
                cancellationToken);
        }
        else
        {
            await SendKitBagRefreshAsync(cancellationToken);
        }

        return done;
    }

    /// <summary>F-90: the Sacrificial Bag is a 500,000 Silver donation to the player's guild (contribution too).</summary>
    private async Task<bool> UseSacrificialBagAsync(CancellationToken cancellationToken)
    {
        if (_guilds is null)
        {
            return false;
        }

        // Step 1 (one transaction): the bag goes and its 500,000 Silver worth is credited, only for a guild member.
        var opened = await RunEventsNpcAsync(async tx =>
        {
            // Membership lives in guild_members (the altar's own donation path reads it there).
            if (await tx.ScalarAsync("SELECT EXISTS (SELECT 1 FROM public.guild_members WHERE character_id = @c);")
                is not true)
            {
                return "no-guild";
            }

            if (!await tx.ConsumeAsync(SacrificialBag, 1))
            {
                return "no-bag";
            }

            await tx.AddSilverAsync(SacrificialBagSilver);
            return "ok";
        }, "unavailable", cancellationToken);
        Console.WriteLine($"[finalise2-item] sacrificial-bag character={_character!.Name} result={opened}");
        if (opened != "ok")
        {
            await SendKitBagRefreshAsync(cancellationToken);
            if (opened == "no-guild")
            {
                await SendGainLogAsync("The Sacrificial Bag can only be offered by a guild member.", cancellationToken);
            }

            return true;
        }

        // Step 2: the altar's own donation path moves the Silver to the guild and raises the contribution.
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        var result = await _guilds.TryDonateAsync(_character!.Id, Godswar.Server.Infrastructure.Guilds.GuildDonationKind.Silver,
            SacrificialBagSilver, DateTimeOffset.UtcNow, cancellationToken);
        var donated = result.Outcome == Godswar.Server.Infrastructure.Guilds.GuildDonationOutcome.Accepted;
        Console.WriteLine($"[finalise2-item] sacrificial-bag donate character={_character.Name} outcome={result.Outcome} " +
            $"guild={result.GuildBalance} contribution={result.Contribution}");
        if (!donated)
        {
            // Step 3 (refused): take the credited Silver back and return the bag, so nothing is minted.
            var undone = await RunEventsNpcAsync(async tx =>
            {
                if (await tx.ScalarAsync("""
                        UPDATE public.character_base SET "Money" = "Money" - @n, wallet_revision = wallet_revision + 1
                        WHERE id = @c AND "Money" >= @n RETURNING "Money";
                        """, ("n", SacrificialBagSilver)) is not int)
                {
                    return false;
                }

                return await tx.GrantAsync(SacrificialBag, 1, bound: null);
            }, false, cancellationToken);
            Console.Error.WriteLine($"[finalise2-item] sacrificial-bag donation refused, reverted={undone} " +
                $"character={_character.Name}");
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            await SendGainLogAsync("The altar could not take the Sacrificial Bag.", cancellationToken);
            return true;
        }

        _character.Silver = result.CharacterBalance;
        await RefreshGuildPanelStatusAsync(_character.Id, cancellationToken);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "SacrificialBagWallet");
        await SendGainLogAsync($"Sacrificial Bag offered: {SacrificialBagSilver:N0} Silver to the guild; your " +
            $"contribution is {result.Contribution}.", cancellationToken);
        return true;
    }

    /// <summary>F-54: Zeus' Baptism refunds every spent talent rank and refreshes the talent window in place.</summary>
    private async Task<bool> UseZeusBaptismAsync(CancellationToken cancellationToken)
    {
        if (Application.Characters.CharacterClassChangeControls.Store is not PostgresCharacterClassChangeStore store ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return false;
        }

        // Talent purchases are durable commands (committed when bought), so the database already holds them.
        var (done, after, refund, talents) = await store.ResetTalentsAsync(
            new CommandSubject(_account!.Id, _character!.Id), ownership, ZeusBaptism, cancellationToken);
        Console.WriteLine($"[finalise2-item] talent-reset character={_character.Name} done={done} talents={talents} " +
            $"refund={refund} points={after}");
        if (!done || !await TryRefreshClassChangeInPlaceAsync(cancellationToken))
        {
            await SendKitBagRefreshAsync(cancellationToken);
            return true;
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await SendGainLogAsync($"Talents reset: {refund} talent point(s) returned.", cancellationToken);
        return true;
    }

    /// <summary>
    /// F-56: hair is stored as style * 10 + colour (Characters/&lt;gender&gt;_hair_SSSC.gwo). Colour 4 is the pale grey
    /// set (average texel about 150,180,180 in every style), so Hairdye:Silver sets colour 4. Pretty Wig moves to the
    /// next style 0-9 (styles both genders have; the order is GENERATED). The character is then reloaded, re-sent
    /// and re-placed on the same spot (a same-map scene change), so the client draws the new hair.
    /// </summary>
    private async Task<bool> UseHairItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var character = _character!;
        int hair = character.Hair;
        var next = itemId == SilverHairdye ? hair / 10 * 10 + 4 : (hair / 10 + 1) % 10 * 10 + hair % 10;
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(itemId, 1))
            {
                return false;
            }

            await tx.ScalarAsync("UPDATE public.character_base SET hair_style = @h WHERE id = @c;", ("h", (short)next));
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[finalise2-item] hair character={character.Name} item={itemId} hair {hair}->{next} done={done}");
        if (!done)
        {
            await SendKitBagRefreshAsync(cancellationToken);
            return true;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await TryRefreshClassChangeInPlaceAsync(cancellationToken);
        await TryBeginSameMapSceneTransitionAsync(_character!.PositionX, _character.PositionZ, "hair-change",
            continuationGuard: null, cancellationToken);
        return true;
    }
}
