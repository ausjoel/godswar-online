using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Seasons;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W3 seasonal events: the Thanksgiving Envoy (Athens_012 / Sparta_012, function 108, client NpcFunThank.lua). Only
/// while the "thanksgiving" season is open (public.seasonal_events, editor). From the client's own texts:
/// 10 Savory Turkey Meat (3945) or 10 Tender Turkey Meat (3946) -> 100 bound Gold, 300 with a Thanksgiving Greetings
/// Card (3948) in the second slot (NF_L0_THANK1400/1500); 10 Thanksgiving Gift Certificates (3947) -> "You've claimed
/// the rewards!" (1100; the reward is not in the client: 200 bound Gold, 500 with a card, GENERATED). 5 exchanges a
/// day (2000), Lv20+ (1000). Like the Mount Feeder, the item slots are read from arguments 6/7 (page*100 + slot).
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int ThanksgivingFunction = 108;
    private const int SavoryTurkeyMeat = 3945, TenderTurkeyMeat = 3946, ThanksgivingCertificate = 3947, ThanksgivingCard = 3948;

    private static readonly ScriptedNpcDialogue ThanksgivingEnvoyDialogue = new(
        FunctionNumber: ThanksgivingFunction,
        OpeningMenu: [1, 2, 3],
        Steps: new Dictionary<int, int[]> { [1] = [11], [2] = [12], [3] = [13] });

    private async Task<bool> TryHandleThanksgivingAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || npc.NpcKey is not ("Athens_012" or "Sparta_012") ||
            !SeasonalEvents.IsOpen(SeasonalEvents.Thanksgiving, DateTimeOffset.UtcNow))
            return false;
        var args = MountFeederArguments(packet);
        var path = DialogPath(packet);
        var opening = path.Length == 0 || path.All(static value => value < 0);
        if (_character.Level < 20 && opening)
        {
            await SendFinalise2ReplyAsync(npc, ThanksgivingFunction, [1000], "ThanksgivingLevel", cancellationToken);
            return true;
        }

        var first = SlotOf(args[6]);
        if (first < 0) return false; // no item placed: the catalog draws the pages
        // Real client (gwhost, 2026-11-08): the submit carries the page-1 choice as the sub id (1/2/3) and
        // args = [0, -1 x5, slot1, slot2, ...]; args[0] is 0, not the form id. The form id is the fallback.
        static int ItemFor(int value) => value switch
        {
            1 or 11 => SavoryTurkeyMeat,
            2 or 12 => TenderTurkeyMeat,
            3 or 13 => ThanksgivingCertificate,
            _ => 0
        };
        var item = ItemFor(subId) is var fromSub and not 0 ? fromSub : ItemFor(args[0]);
        Console.WriteLine($"[thanksgiving] character={_character.Name} sub={subId} args=[{string.Join(',', args)}] item={item}");
        int result;
        if (_character.Level < 20) result = 1000;
        else if (item == 0) result = 1300;
        else result = await RunEventsNpcAsync(tx => ThanksgivingExchangeAsync(tx, item, first, SlotOf(args[7])), 1300,
            cancellationToken);
        Console.WriteLine($"[thanksgiving] character={_character.Name} item={item} result={result}");
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendFinalise2ReplyAsync(npc, ThanksgivingFunction, [result], "ThanksgivingResult", cancellationToken);
        return true;
    }

    private static async Task<int> ThanksgivingExchangeAsync(EventsNpcTransaction tx, int item, int slot, int cardSlot)
    {
        var missing = item == ThanksgivingCertificate ? 2200 : 1200;
        if (await tx.ReadSlotAsync(slot) is not { } placed) return missing;
        if (placed.ItemId != item) return 1300;
        if (await tx.DailyUsedAsync("thanksgiving") >= 5) return 2000;
        var withCard = cardSlot >= 0 && await tx.ReadSlotAsync(cardSlot) is { ItemId: ThanksgivingCard };
        if (!await tx.ConsumeAsync(item, 10)) return missing;
        if (withCard && !await tx.ConsumeAsync(ThanksgivingCard, 1))
        {
            tx.Rollback();
            return 1300;
        }

        var gold = item == ThanksgivingCertificate ? (withCard ? 500 : 200) : (withCard ? 300 : 100);
        await tx.AddBindingGoldAsync(gold);
        await tx.DailyAddAsync("thanksgiving", 1);
        return item == ThanksgivingCertificate ? 1100 : withCard ? 1500 : 1400;
    }
}
