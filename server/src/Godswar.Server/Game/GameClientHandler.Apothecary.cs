using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// W2 Apothecary (Athens_109 / Sparta_109, function 60, client NpcFunChemist.lua, texts NF_L0_CH*). Rules from the
/// client text NF_L0_CH0001 / CH200:
/// <list type="bullet">
/// <item>Exchange (101), 1,000 Silver each: a Level 1 Ruby/Sapphire/Emerald -> Weak Pet Exp Potion (4 a day); 4 Level 1
/// Crystals -> Strong Pet Exp Potion (4 a day); a Level 2 Ruby/Sapphire/Emerald -> Medium EXP Potion (8 a day). The
/// exchange page id carries today's remaining counts (sub = 11 + 100*weak + 1000*strong + 10000*medium; after an
/// exchange 21 + ...).</item>
/// <item>Transfer (102), 5,000 Silver: 8 potions -> 1 of the next tier (EXP Primary->Weak->Medium->Strong, Elixir of
/// Talent I->II->III->IV->V, Pet Exp Weak->Strong->Super).</item>
/// </list>
/// Item slot from argument 6 (as every item form; the submit packet was not captured, so it is logged raw).
/// Hot rate npc.apothecary.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int ApothecaryFunction = 60;
    private const int ApothecaryExchangeSilver = 1_000;
    private const int ApothecaryTransferSilver = 5_000;

    private static readonly (int[] Inputs, int Need, int Output, string Counter, int Daily)[] ApothecaryExchanges =
    [
        ([4200, 4210, 4220], 1, 4529, "apoth_weak_pet", 4),
        ([4230], 4, 4530, "apoth_strong_pet", 4),
        ([4201, 4211, 4221], 1, 4502, "apoth_medium_exp", 8),
    ];

    private static readonly int[][] ApothecaryTransferChains =
    [
        [4500, 4501, 4502, 4503],
        [4523, 4524, 4525, 4526, 4527],
        [4529, 4530, 4531],
    ];

    private async Task<int> ApothecaryCountsSubAsync(int baseSub, CancellationToken cancellationToken) =>
        await RunEventsNpcAsync(async tx =>
        {
            var left = new int[3];
            for (var i = 0; i < 3; i++)
                left[i] = Math.Clamp(ApothecaryExchanges[i].Daily - await tx.DailyUsedAsync(ApothecaryExchanges[i].Counter), 0, 9);
            return baseSub + 100 * left[0] + 1000 * left[1] + 10000 * left[2];
        }, baseSub, cancellationToken);

    private async Task<bool> TryHandleApothecaryAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || !Finalise2Rates.On(Finalise2RateKeys.Apothecary) ||
            npc.NpcKey is not ("Athens_109" or "Sparta_109"))
            return false;
        var args = MountFeederArguments(packet);
        var slot = SlotOf(args[6]);
        var marker = args[0] >= 0 ? args[0] : subId;
        if (slot < 0)
        {
            if (subId != 101 || args[0] >= 0) return false;
            // The exchange page shows today's remaining counts in its id.
            await SendFinalise2ReplyAsync(npc, ApothecaryFunction, [await ApothecaryCountsSubAsync(11, cancellationToken)],
                "ApothecaryExchangePage", cancellationToken);
            return true;
        }

        var transfer = marker is 102 or 202 or 300 || subId is 102 or 202 or 300;
        Console.WriteLine($"[apothecary] character={_character.Name} sub={subId} args=[{string.Join(',', args)}] " +
            $"action={(transfer ? "transfer" : "exchange")}");
        var result = await RunEventsNpcAsync(tx => transfer ? ApothecaryTransferAsync(tx, slot) : ApothecaryExchangeAsync(tx, slot),
            902, cancellationToken);
        if (result == 21) result = await ApothecaryCountsSubAsync(21, cancellationToken);
        Console.WriteLine($"[apothecary] character={_character.Name} result={result}");
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendFinalise2ReplyAsync(npc, ApothecaryFunction, [result], "ApothecaryResult", cancellationToken);
        return true;
    }

    private static async Task<int> ApothecaryExchangeAsync(EventsNpcTransaction tx, int slot)
    {
        if (await tx.ReadSlotAsync(slot) is not { } placed) return 902;
        var rule = ApothecaryExchanges.FirstOrDefault(r => r.Inputs.Contains(placed.ItemId));
        if (rule.Inputs is null) return 902;
        if (await tx.DailyUsedAsync(rule.Counter) >= rule.Daily) return 909;
        if (await tx.CountAsync(placed.ItemId) < rule.Need) return rule.Need > 1 ? 904 : 902;
        if (!await tx.ConsumeAsync(placed.ItemId, rule.Need)) return 904;
        if (await tx.TrySpendSilverAsync(ApothecaryExchangeSilver) is null) { tx.Rollback(); return 908; }
        if (!await tx.GrantAsync(rule.Output, 1, bound: placed.Bound)) { tx.Rollback(); return 910; }
        await tx.DailyAddAsync(rule.Counter, 1);
        return 21; // success page (counts filled in by the caller)
    }

    private static async Task<int> ApothecaryTransferAsync(EventsNpcTransaction tx, int slot)
    {
        if (await tx.ReadSlotAsync(slot) is not { } placed) return 903;
        var chain = ApothecaryTransferChains.FirstOrDefault(c => Array.IndexOf(c, placed.ItemId) is var i && i >= 0 && i < c.Length - 1);
        if (chain is null) return 903;
        var next = chain[Array.IndexOf(chain, placed.ItemId) + 1];
        if (await tx.CountAsync(placed.ItemId) < 8) return 906;
        if (!await tx.ConsumeAsync(placed.ItemId, 8)) return 906;
        if (await tx.TrySpendSilverAsync(ApothecaryTransferSilver) is null) { tx.Rollback(); return 908; }
        if (!await tx.GrantAsync(next, 1, bound: placed.Bound)) { tx.Rollback(); return 910; }
        return 300; // "successfully transferred" page
    }
}
