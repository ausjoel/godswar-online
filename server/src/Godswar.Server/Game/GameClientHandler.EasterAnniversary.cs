using Godswar.Server.Application.Seasons;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using S = Godswar.Server.Application.Seasons.EasterAnniversarySeason;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasons easter / igg: the Easter Envoy (function 99, NpcFunRevive.lua), the Easter Bunny Envoy (51,
/// NpcFunRelive.lua) and the IGG Anniversary Envoy (104, NpcFunIgg.lua), only while their season is open and their
/// switch is on (<see cref="EasterAnniversarySeason"/>). Replies are the scripts' own result numbers; no pop-up
/// follows an exchange (the admin's rule), only the result line. Nothing here is sent at world entry: every frame answers
/// a click.
/// <para>
/// Submissions: as at the Thanksgiving Envoy (gwhost 2026-11-08) a form submit carries the page-1 choice as the sub
/// id, 0 in the next word and the item slot in argument 6 (page*100 + slot). A page click carries just the entry.
/// Every request's words are logged, because the 902-907 numbers exist under both Index 2 and 3. The IGG envoy has
/// no message box at all: the stock Set_MessageBox has no IGG branch, so each of its clicks is a purchase.
/// </para>
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>The Easter Bunny Envoy (NpcFunRelive.lua, NPC_FLAG_SYS_RELIVE = 51): cage, eggs, my submission.</summary>
    private static readonly ScriptedNpcDialogue EasterBunnyEnvoyDialogue = new(
        FunctionNumber: S.BunnyEnvoyFunction,
        OpeningMenu: [901, 101, 102, 103],
        Steps: new Dictionary<int, int[]> { [102] = [201, 202] });

    private async Task<bool> TryHandleEasterAnniversaryAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null || _account is null) return false;
        var now = DateTimeOffset.UtcNow;
        var kind = dialogIndex switch
        {
            S.EasterEnvoyFunction when S.IsEasterEnvoy(npc.NpcKey) && S.EasterOpen(now) => 1,
            S.BunnyEnvoyFunction when S.IsBunnyEnvoy(npc.NpcKey) && S.EasterOpen(now) => 2,
            // Always intercepted (closed -> 197 in AnniversaryEnvoyAsync): the catalog's [100] says "obtained".
            S.AnniversaryEnvoyFunction when S.IsAnniversaryEnvoy(npc.NpcKey) => 3,
            _ => 0
        };
        if (kind == 0) return false;

        var path = DialogPath(packet);
        if (path.Length == 0 && subId > 0) path = [subId];
        var args = MountFeederArguments(packet);
        var slot = SlotOf(args[6]);
        var submit = slot >= 0 || path.Length >= 2;
        var click = path.LastOrDefault(static value => value > 0);
        Console.WriteLine($"[easter-igg] npc={npc.NpcKey} function={dialogIndex} character={_character.Name} " +
            $"sub={subId} path=[{string.Join(',', path)}] slot={slot}{EventsNpcWords(packet)}");
        try
        {
            return kind switch
            {
                1 => await EasterEnvoyAsync(npc, path, click, submit, slot, cancellationToken),
                2 => await BunnyEnvoyAsync(npc, path, click, submit, slot, cancellationToken),
                _ => await AnniversaryEnvoyAsync(npc, path, subId, cancellationToken)
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An event fault must never end the session; the transaction did not commit.
            Console.Error.WriteLine($"[easter-igg] fault npc={npc.NpcKey}: {error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private Task EasterSeasonReplyAsync(NpcSpawnDefinition npc, int function, int[] reply, string reason,
        CancellationToken token) => SendFinalise2ReplyAsync(npc, function, reply, reason, token);

    /// <summary>Empty kit-bag slots (checked before a page whose submit grants items: Index 3 has no bag-full line).</summary>
    private int EasterSeasonFreeBagSlots() => Enumerable.Range(0, 96)
        .Count(slot => KitBagSlots.GetItemId(_character!.KitBag, slot) == 0);

    // ---- Easter Envoy, function 99 ----------------------------------------------------------------------------------

    private async Task<bool> EasterEnvoyAsync(NpcSpawnDefinition npc, int[] path, int click, bool submit, int slot,
        CancellationToken token)
    {
        const int f = S.EasterEnvoyFunction;
        if (path.Length == 0) return false; // the catalog's page 1 [101, 102, 103]
        if (submit)
        {
            var choice = path[0];
            int result;
            if (choice == 102)
                result = slot < 0 ? 297 : await RunEventsNpcAsync(tx => EasterEggExchangeAsync(tx, slot), 297, token);
            else if (choice == 103)
                result = slot < 0 ? 299 : await RunEventsNpcAsync(tx => EasterPackageAsync(tx, slot), 299, token);
            else
                return false;
            Console.WriteLine($"[easter-igg] easter envoy character={_character!.Name} choice={choice} result={result}");
            if (result is >= 1001 and <= 1007 or 303) await RefreshAfterEventsNpcAsync([], token);
            await EasterSeasonReplyAsync(npc, f, [result], "EasterEnvoyResult", token);
            return true;
        }

        var page = click switch
        {
            // GENERATED: the Easter Isles (Labyrint3) are not built, so the Isles button answers with the Isles-hours
            // line (REVIVE201T) instead of a teleport.
            101 => 201,
            102 => EasterSeasonFreeBagSlots() >= 1 ? 202 : 296,
            103 => EasterSeasonFreeBagSlots() >= 4 ? 203 : 296, // the package is up to 4 stacks
            _ => 0
        };
        if (page == 0) return false;
        await EasterSeasonReplyAsync(npc, f, [page], "EasterEnvoyPage", token);
        return true;
    }

    private static async Task<int> EasterEggExchangeAsync(EventsNpcTransaction tx, int slot)
    {
        if (await tx.ReadSlotAsync(slot) is not { ItemId: S.EasterEgg }) return 297;
        var (eggs, pills, reply) = S.EggExchange(await tx.DailyUsedAsync("easter_egg") + 1);
        if (!await tx.ConsumeAsync(S.EasterEgg, eggs)) return 297;
        if (!await tx.GrantAsync(S.ExpPill, pills, bound: null))
        {
            tx.Rollback();
            return 297;
        }

        await tx.DailyAddAsync("easter_egg", 1);
        return reply;
    }

    private static async Task<int> EasterPackageAsync(EventsNpcTransaction tx, int slot)
    {
        if (await tx.ReadSlotAsync(slot) is not { ItemId: S.StrangeEasterEgg }) return 299;
        if (await tx.DailyUsedAsync("easter_package") >= 1) return 298;
        if (!await tx.ConsumeAsync(S.StrangeEasterEgg, 1)) return 299;
        var bunny = Random.Shared.Next(100) < Math.Clamp(
            (int)Application.Rates.ServerRates.Current.Get(S.PackageBunnyPercentKey), 0, 100);
        foreach (var (item, count) in S.PackageContents(bunny))
        {
            if (await tx.GrantAsync(item, count, bound: true)) continue;
            tx.Rollback();
            return 296;
        }

        await tx.DailyAddAsync("easter_package", 1);
        return 303;
    }

    // ---- Easter Bunny Envoy, function 51 ----------------------------------------------------------------------------

    private async Task<bool> BunnyEnvoyAsync(NpcSpawnDefinition npc, int[] path, int click, bool submit, int slot,
        CancellationToken token)
    {
        const int f = S.BunnyEnvoyFunction;
        var character = _character!;
        if (path.Length == 0)
        {
            // NF_L0_RL_900: Lv20+ only. Otherwise the rule text (901) over the three entries.
            await EasterSeasonReplyAsync(npc, f, character.Level < 20 ? [900] : [901, 101, 102, 103], "BunnyEnvoyOpen", token);
            return true;
        }

        if (character.Level < 20)
        {
            await EasterSeasonReplyAsync(npc, f, [900], "BunnyEnvoyLevel", token);
            return true;
        }

        if (submit && path[0] == 102 || click == 201)
        {
            if (click == 202)
                return await BunnyWeakBunnyAsync(npc, token);
            var outcome = slot < 0
                ? (906, S.BunnyPrize.Experience, 0L, 0)
                : await RunEventsNpcAsync(tx => BunnyTurnInAsync(tx, slot), (906, S.BunnyPrize.Experience, 0L, 0), token);
            var (result, prize, amount, zodiacX100) = outcome;
            Console.WriteLine($"[easter-igg] bunny turn-in character={character.Name} result={result} prize={prize} " +
                $"amount={amount}");
            if (result == 913) await PayBunnyPrizeAsync(prize, amount, zodiacX100, token);
            await EasterSeasonReplyAsync(npc, f, [result], "BunnyEnvoyResult", token);
            return true;
        }

        switch (click)
        {
            case 101:
            {
                // "Claim Bunny Cage": one claim a day (NF_L0_RL_900), 40 cages (912). GENERATED note: there are no
                // hopping bunnies in this world yet (the Easter Isles are not built), so the cages have no target.
                var result = await RunEventsNpcAsync(async tx =>
                {
                    if (await tx.DailyUsedAsync("easter_cage") >= 1) return 905;
                    if (!await tx.GrantAsync(S.BunnyCage, S.BunnyCagesPerClaim, bound: true)) return 904;
                    await tx.DailyAddAsync("easter_cage", 1);
                    return 912;
                }, 904, token);
                if (result == 912) await RefreshAfterEventsNpcAsync([], token);
                await EasterSeasonReplyAsync(npc, f, [result], "BunnyEnvoyCage", token);
                return true;
            }
            case 102:
                await EasterSeasonReplyAsync(npc, f, [201, 202], "BunnyEnvoyEggs", token);
                return true;
            case 202:
                return await BunnyWeakBunnyAsync(npc, token);
            case 103:
            {
                // NF_L0_RL_915/916: my turn-ins today, and the day's leader's (n*1000+1 / n*1000+2).
                var (own, lead) = await RunEventsNpcAsync(async tx =>
                {
                    var mine = await tx.DailyUsedAsync("easter_bunny");
                    var top = await tx.ScalarAsync(
                        "SELECT COALESCE(MAX(used), 0)::int FROM public.finalise2_npc_daily WHERE day = @d AND counter = @k;",
                        ("d", EventsNpcDay(EventsNpcNow)), ("k", "easter_bunny")) is int value ? value : 0;
                    return (mine, Math.Max(mine, top));
                }, (0, 0), token);
                await EasterSeasonReplyAsync(npc, f, [own * 1000 + 1, lead * 1000 + 2], "BunnyEnvoySubmission", token);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task<(int Result, S.BunnyPrize Prize, long Amount, int ZodiacX100)> BunnyTurnInAsync(
        EventsNpcTransaction tx, int slot)
    {
        var none = (906, S.BunnyPrize.Experience, 0L, 0);
        if (await tx.ReadSlotAsync(slot) is not { ItemId: S.EasterEggs }) return none;
        var used = await tx.DailyUsedAsync("easter_bunny");
        if (used >= S.BunnyDailyExchanges) return (911, S.BunnyPrize.Experience, 0L, 0);
        var (prize, amount) = S.BunnyReward(used + 1, _character!.Level);
        // A pet prize with no pet out, or Zodiac energy into a full store, pays the EXP prize instead (GENERATED).
        if (prize == S.BunnyPrize.PetExperience && !HasSummonedPet() || amount <= 0)
            (prize, amount) = (S.BunnyPrize.Experience, S.BunnyReward(1, _character.Level).Amount * (1 + used / 4));
        if (!await tx.ConsumeAsync(S.EasterEggs, 1)) return none;
        var zodiacX100 = 0;
        if (prize == S.BunnyPrize.ZodiacEnergy)
        {
            zodiacX100 = await AddEasterZodiacEnergyAsync(tx, checked((int)amount));
            if (zodiacX100 <= 0)
                (prize, amount) = (S.BunnyPrize.Experience, S.BunnyReward(1, _character.Level).Amount * (1 + used / 4));
        }

        if (prize == S.BunnyPrize.TalentPoints) await tx.AddTalentPointsAsync(checked((int)amount));
        await tx.DailyAddAsync("easter_bunny", 1);
        return (913, prize, amount, zodiacX100);
    }

    /// <summary>Zodiac energy up to the storage limit, in hundredths added (0 = full). Own SQL, so it does not clash
    /// with the Mid-autumn branch's transaction helper.</summary>
    private static async Task<int> AddEasterZodiacEnergyAsync(EventsNpcTransaction tx, int energy)
    {
        if (energy <= 0) return 0;
        var level = await tx.ScalarAsync("SELECT zodiac_level FROM public.character_base WHERE id = @c FOR UPDATE;");
        if (level is null or DBNull) return 0;
        var capacityX100 = (long)ZodiacEnergyCatalog.GetStorageLimit(Math.Clamp(Convert.ToInt32(level), 1, 30)) * 100L;
        var gained = await tx.ScalarAsync("""
            WITH cur AS (SELECT zodiac_energy::bigint * 100 + zodiac_energy_remainder_x100 AS b
                         FROM public.character_base WHERE id = @c)
            UPDATE public.character_base AS cb
            SET zodiac_energy = (LEAST(@cap, cur.b + @add) / 100)::int,
                zodiac_energy_remainder_x100 = (LEAST(@cap, cur.b + @add) % 100)::int,
                progression_reward_revision = cb.progression_reward_revision + 1
            FROM cur WHERE cb.id = @c AND cur.b < @cap
            RETURNING (LEAST(@cap, cur.b + @add) - cur.b)::int;
            """, ("cap", capacityX100), ("add", (long)energy * 100L));
        return gained is int value ? value : 0;
    }

    private async Task PayBunnyPrizeAsync(S.BunnyPrize prize, long amount, int zodiacX100, CancellationToken token)
    {
        await RefreshAfterEventsNpcAsync([], token);
        switch (prize)
        {
            case S.BunnyPrize.Experience:
                await PayEventsNpcExperienceAsync(amount, "EasterBunnyEnvoy", token);
                break;
            case S.BunnyPrize.TalentPoints:
                await _session.SendAsync(PacketBuilder.TalentPointGain(checked((int)amount)), token, "EasterBunnyTalent");
                break;
            case S.BunnyPrize.PetExperience:
                try
                {
                    var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(_account!.Id, _character!.Id,
                        Guid.NewGuid(), checked((int)amount), token);
                    if (pet is { HasPetProjection: true })
                        await _session.SendAsync(PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                            token, "EasterBunnyPetExperience");
                    else
                        Console.WriteLine($"[easter-igg] pet experience not applied status={pet?.Status}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[easter-igg] pet experience failed: {ex.Message}");
                }

                break;
            case S.BunnyPrize.ZodiacEnergy:
                await _session.SendAsync(PacketBuilder.ZodiacEnergyIncrease(_character!.ZodiacEnergy, zodiacX100), token,
                    "EasterBunnyZodiac");
                await SendZodiacFullSyncAsync(token);
                break;
        }
    }

    /// <summary>
    /// "Claim Bunnies" (202): the Weak Bunny for the most Easter Eggs turned in (NpcFunRelive 909/910/914). GENERATED:
    /// the Weak Bunny is an Easter Bunny Egg (10154, the Easter Bunny pet), and the round is a realm day, so the
    /// leader of yesterday (ties included) claims one today.
    /// </summary>
    private async Task<bool> BunnyWeakBunnyAsync(NpcSpawnDefinition npc, CancellationToken token)
    {
        var yesterday = EventsNpcDay(EventsNpcNow).AddDays(-1);
        var result = await RunEventsNpcAsync(async tx =>
        {
            var mine = await tx.ScalarAsync(
                "SELECT used FROM public.finalise2_npc_daily WHERE character_id = @c AND day = @d AND counter = @k;",
                ("d", yesterday), ("k", "easter_bunny")) is int own ? own : 0;
            var top = await tx.ScalarAsync(
                "SELECT COALESCE(MAX(used), 0)::int FROM public.finalise2_npc_daily WHERE day = @d AND counter = @k;",
                ("d", yesterday), ("k", "easter_bunny")) is int value ? value : 0;
            if (mine <= 0 || mine < top) return 909;
            if (await tx.DailyUsedAsync("easter_weak_bunny") >= 1) return 910;
            if (!await tx.GrantAsync(S.EasterBunnyEgg, 1, bound: true)) return 904;
            await tx.DailyAddAsync("easter_weak_bunny", 1);
            return 914;
        }, 909, token);
        if (result == 914) await RefreshAfterEventsNpcAsync([], token);
        await EasterSeasonReplyAsync(npc, S.BunnyEnvoyFunction, [result], "BunnyEnvoyWeakBunny", token);
        return true;
    }

    // ---- IGG Anniversary Envoy, function 104 ------------------------------------------------------------------------

    /// <summary>
    /// One click = one purchase (NpcFunIgg.lua evidence in <see cref="S.IsAnniversaryPurchase"/>): charge the price
    /// (or the GENERATED Key Pieces) and grant one Anniversary Gold Sack in ONE transaction, then draw page 100.
    /// Any failure rolls the whole transaction back, so nothing is ever charged without the sack. Closed season or
    /// switch off: 197, nothing charged (the catalog's [100] would claim a grant, so this envoy never falls through).
    /// </summary>
    private async Task<bool> AnniversaryEnvoyAsync(NpcSpawnDefinition npc, int[] path, int subId, CancellationToken token)
    {
        int result;
        var paidWith = "";
        if (!S.AnniversaryOpen(DateTimeOffset.UtcNow))
        {
            result = S.AnniversaryClosed;
        }
        else if (!S.IsAnniversaryPurchase(subId, path))
        {
            Console.WriteLine($"[easter-igg] anniversary envoy: not a purchase sub={subId} path=[{string.Join(',', path)}]");
            return true; // no page owns it; never charge on a guess
        }
        else
        {
            var price = Math.Max(0, (int)Application.Rates.ServerRates.Current.Get(S.SackGoldKey));
            var pieces = Math.Max(0, (int)Application.Rates.ServerRates.Current.Get(S.SackKeyPiecesKey));
            result = await RunEventsNpcAsync(async tx =>
            {
                if (pieces > 0 && await tx.CountAsync(S.KeyPieces) >= pieces)
                {
                    if (!await tx.ConsumeAsync(S.KeyPieces, pieces))
                    {
                        tx.Rollback();
                        return S.AnniversaryNoGold;
                    }

                    paidWith = "key pieces";
                }
                else
                {
                    if (await tx.TrySpendGoldAsync(price) is null)
                    {
                        tx.Rollback();
                        return S.AnniversaryNoGold; // igg198t: not enough gold
                    }

                    paidWith = "gold";
                }

                if (await tx.GrantAsync(S.AnniversaryGoldSack, 1, bound: null)) return S.AnniversaryObtained;
                tx.Rollback(); // the charge is undone with the failed grant
                paidWith = "";
                return S.AnniversaryBagFull; // NF_L0_MOON200: not enough empty space
            }, S.AnniversaryClosed, token);
            if (result != S.AnniversaryObtained) paidWith = "";
        }

        Console.WriteLine($"[easter-igg] anniversary sack character={_character!.Name} result={result} paid={paidWith}");
        if (result == S.AnniversaryObtained) await RefreshAfterEventsNpcAsync([], token);
        await EasterSeasonReplyAsync(npc, S.AnniversaryEnvoyFunction, [result], "AnniversaryEnvoySack", token);
        return true;
    }
}
