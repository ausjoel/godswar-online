using Godswar.Server.Application.Finalise2;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// F-27 "Build all, do not hide" and F-140 ("Details shows info only; move the exchange to the Exchange page"): the Holy
/// Stone Redeemer's pages (Athens_131 / Sparta_131). The client gives the redeemer three menu entries (NPCDescription.dat
/// SYS_NPC_38-40), all drawn by NpcFunHolyPackage_SetText, so the SubIDs decide the page:
/// <list type="bullet">
/// <item>38 "Holy Stone Package details": 101 (HolyPackage1), information only.</item>
/// <item>39 "Exchange for Holy Stone Package(s)": 102 (HolyPackage2) + 103 (item slot). A Holy Stone Package Voucher
/// (14053) placed and confirmed becomes a bound Holy Stone Package (14054, "bound" in NPCDescription Athens_131): 201
/// (HolyPackage6). Opening the page also uses
/// carried Holy Box vouchers (14046/14047, the rest of the F-90 code that used to run on page 38).</item>
/// <item>40 "Return Holy Stone Package Voucher(s)": 104 (HolyPackage3) + 105 (item slot). A voucher placed and confirmed
/// is taken back for 1,800 Gold: 202 (HolyPackage10).</item>
/// </list>
/// Shared results: 291 (HolyPackage5, no voucher in the slot), 290 (REVIVE296T, bag full). HolyPackage7's limit of 3
/// vouchers per person (292) belonged to IGG's time-limited event and is not applied; 293 (an April deadline) is unused.
/// <para>
/// The 1,800 Gold is the client's own number (HolyPackage10, NPCDescription Athens_131 and the Item Mall price of
/// 14053), not GENERATED. The only player source of 14053 is the Item Mall (Gold); the GM mall is operator-only.
/// </para>
/// The slot is argument 6 (page*100 + slot), confirmed on the rig 2026-11-09 (slot 10 / 11); a page request has
/// sub &lt; 0, a submit sub 0. Every submit is logged raw. Hot rate npc.holy_stone_refund (1 = the
/// three-page menu; 0 = the old single page 38, which converts carried vouchers when opened). The exchange also needs
/// item.finalise2_vouchers.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int HolyStoneExchangeFunction = 39; // NPC_FLAG_SYS_HOLYPACKAGE2, NpcFunHolyPackage.lua
    private const int HolyStoneRefundFunction = 40;   // NPC_FLAG_SYS_HOLYPACKAGE3
    private const int HolyStonePackageVoucher = 14053;
    private const int HolyStonePackage = 14054;
    private const int HolyStoneVoucherRefundGold = 1800; // HolyPackage10 and the Item Mall price
    private const int HolyStoneExchangeDone = 201;   // HolyPackage6
    private const int HolyStoneRefundDone = 202;     // HolyPackage10
    private const int HolyStoneBagFull = 290;        // REVIVE296T
    private const int HolyStoneNoVoucher = 291;      // HolyPackage5

    private static readonly ScriptedNpcDialogue HolyStoneExchangeDialogue = new(
        FunctionNumber: HolyStoneExchangeFunction, OpeningMenu: [102, 103], Steps: new Dictionary<int, int[]>());

    private static readonly ScriptedNpcDialogue HolyStoneRefundDialogue = new(
        FunctionNumber: HolyStoneRefundFunction, OpeningMenu: [104, 105], Steps: new Dictionary<int, int[]>());

    private static bool HolyStoneRedeemerPagesOn => Finalise2Rates.On(Finalise2RateKeys.HolyStoneRefund);

    private async Task<bool> TryHandleHolyStoneRedeemerPageAsync(NpcSpawnDefinition npc, int dialogIndex, int subId,
        GamePacket packet, CancellationToken cancellationToken)
    {
        var exchange = dialogIndex == HolyStoneExchangeFunction;
        if (_character is null || _account is null || !HolyStoneRedeemerPagesOn ||
            npc.NpcKey is not ("Athens_131" or "Sparta_131") ||
            (exchange && !Finalise2Rates.On(Finalise2RateKeys.Vouchers)))
        {
            return false;
        }

        var args = MountFeederArguments(packet);
        var slot = SlotOf(args[6]);
        Console.WriteLine($"[holy-stone-redeemer] character={_character.Name} function={dialogIndex} sub={subId} " +
            $"args=[{string.Join(',', args)}] slot={slot} words:{EventsNpcWords(packet)}");
        try
        {
            if (subId < 0)
            {
                // A page request (no click, no item): the generic answer draws the page. The exchange page first uses
                // carried Holy Box vouchers (F-90; one chat line each says what was given).
                if (exchange)
                {
                    await RedeemCarriedVouchersAsync([HolyBoxTwoVoucher, HolyBoxThreeVoucher], cancellationToken);
                }

                return false;
            }

            // A submit (rig 2026-11-09: OK with an empty slot sends sub=0 and every argument -1) with no voucher in the
            // slot is answered 291; nothing is taken from the bag.
            var result = slot < 0 ? HolyStoneNoVoucher : await RunEventsNpcAsync(async tx =>
            {
                if (await tx.ReadSlotAsync(slot) is not { } item || item.ItemId != HolyStonePackageVoucher)
                {
                    return HolyStoneNoVoucher;
                }

                if (!await tx.ConsumeAsync(HolyStonePackageVoucher, 1))
                {
                    return HolyStoneNoVoucher;
                }

                if (exchange)
                {
                    if (!await tx.GrantAsync(HolyStonePackage, 1, bound: true))
                    {
                        tx.Rollback();
                        return HolyStoneBagFull;
                    }

                    return HolyStoneExchangeDone;
                }

                await tx.ScalarAsync("UPDATE public.character_base SET \"Stone\" = LEAST(\"Stone\"::bigint + @g, " +
                    "2147483647)::integer, wallet_revision = wallet_revision + 1 WHERE id = @c;",
                    ("g", (long)HolyStoneVoucherRefundGold));
                return HolyStoneRefundDone;
            }, HolyStoneNoVoucher, cancellationToken);

            Console.WriteLine($"[holy-stone-redeemer] character={_character.Name} function={dialogIndex} slot={slot} " +
                $"result={result}" +
                (result == HolyStoneRefundDone ? $" gold={HolyStoneVoucherRefundGold}" : "") +
                (result == HolyStoneExchangeDone ? $" item={HolyStonePackage}" : ""));
            if (result is HolyStoneExchangeDone or HolyStoneRefundDone)
            {
                // No notice: the 201/202 page states the result (no pop-up after a consumed item).
                await RefreshAfterEventsNpcAsync([], cancellationToken);
            }

            await SendFinalise2ReplyAsync(npc, dialogIndex, [result], "HolyStoneRedeemer", cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed (the transaction rolled back); the session stays.
            Console.Error.WriteLine($"[holy-stone-redeemer] fault npc={npc.NpcKey} function={dialogIndex}: " +
                $"{error.GetType().Name}: {error.Message}");
        }

        return true;
    }
}
