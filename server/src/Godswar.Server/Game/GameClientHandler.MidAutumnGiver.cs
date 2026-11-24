using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// B27 (the admin d139): the Mid-autumn Gift Pack Giver's dragon-piece exchanges (Athens_115 / Sparta_115, function 66,
/// client NpcFunSummerVacation.lua). Page 1 buttons 101 and 102; page 3 shows Summer_Page3_T(n - 299).
/// <list type="bullet">
/// <item>101 -> page 201 (Summer_Page2_T2, "Exchange for Magic Jade" button). The button click (path ending 201, the
/// capture's +16 = 101 / +20 = 201 layout) takes one each of the 8 Blue Crystal Dragon pieces (Head, Neck, Horn, Eye,
/// Scale, Wing, Claw, Ear; 14014-14021) and gives Magic Jade: Blue Crystal Dragon (11061), bound: 304 (T5). Without a
/// full set: 303 (T4), nothing taken.</item>
/// <item>102 -> page 202 (Summer_Page2_T3, one item slot). A placed piece (argument 6, page*100 + slot, as every
/// other item form) is taken, 1 at a time, for EXP: 305 (T6, "fabulous rewards in Exp"). Nothing or not a piece
/// placed: 300 (T1 "Please put in the required items."). The amount is GENERATED (no client or retail number):
/// <see cref="DragonPieceExperiencePerLevel"/> x character level, through the event-rate EXP path.</item>
/// </list>
/// Pieces come from the Mid-autumn Festival Gift Pack (14012, one random piece; IGG news aid=1236). Hot switch:
/// item.finalise2_vouchers (the switch that already runs this NPC's voucher exchange). The result is the client's own
/// page-3 line; no pop-up follows (the admin's rule).
/// </summary>
internal sealed partial class GameClientHandler
{
    internal static readonly int[] BlueCrystalDragonPieces = [14014, 14015, 14016, 14017, 14018, 14019, 14020, 14021];
    internal const int BlueCrystalDragonJade = 11061;
    internal const long DragonPieceExperiencePerLevel = 2_000; // GENERATED (B27, d139): 2 Delicate Mooncakes at the Lunar Priest

    private async Task<bool> TryHandleMidAutumnGiverAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || npc.NpcKey is not ("Athens_115" or "Sparta_115") ||
            !Finalise2Rates.On(Finalise2RateKeys.Vouchers))
            return false;
        var path = DialogPath(packet);
        var args = MountFeederArguments(packet);
        var slot = SlotOf(args[6]);
        var jadeClick = path is [.., 201] && path.Length >= 2 || subId == 201 || args[0] == 101 && args[1] == 201;
        var pieceSubmit = slot >= 0 && (subId is 102 or 202 || args[0] is 102 or 202);
        if (!jadeClick && !pieceSubmit) return false; // pages and the voucher path run as before
        Console.WriteLine($"[mid-autumn-giver] character={_character.Name} sub={subId} path=[{string.Join(',', path)}] " +
            $"args=[{string.Join(',', args)}]");
        int result;
        long experience = 0;
        if (jadeClick)
        {
            result = await RunEventsNpcAsync(DragonJadeExchangeAsync, 303, cancellationToken);
        }
        else
        {
            experience = DragonPieceExperiencePerLevel * Math.Max(1, _character.Level);
            result = await RunEventsNpcAsync(tx => DragonPieceExchangeAsync(tx, slot), 300, cancellationToken);
        }

        Console.WriteLine($"[mid-autumn-giver] character={_character.Name} {(jadeClick ? "jade" : "piece")} result={result}" +
            (result == 305 ? $" exp={experience}" : ""));
        if (result is 304 or 305)
        {
            await RefreshAfterEventsNpcAsync(result == 304 ? [(BlueCrystalDragonJade, 1)] : [], cancellationToken);
            if (result == 305) await PayEventsNpcExperienceAsync(experience, "DragonPiece", cancellationToken);
        }

        await SendFinalise2ReplyAsync(npc, SummerGiftEnvoyFunction, [result], "MidAutumnGiverResult", cancellationToken);
        return true;
    }

    private static async Task<int> DragonJadeExchangeAsync(EventsNpcTransaction tx)
    {
        foreach (var piece in BlueCrystalDragonPieces)
            if (await tx.CountAsync(piece) < 1) return 303;
        foreach (var piece in BlueCrystalDragonPieces)
        {
            if (await tx.ConsumeAsync(piece, 1)) continue;
            tx.Rollback();
            return 303;
        }

        if (await tx.GrantAsync(BlueCrystalDragonJade, 1, bound: true)) return 304;
        tx.Rollback();
        return 303; // bag full: nothing taken (the client has no bag-full line on this page)
    }

    private static async Task<int> DragonPieceExchangeAsync(EventsNpcTransaction tx, int slot)
    {
        if (await tx.ReadSlotAsync(slot) is not { } placed || Array.IndexOf(BlueCrystalDragonPieces, placed.ItemId) < 0)
            return 300;
        return await tx.ConsumeAsync(placed.ItemId, 1) ? 305 : 300;
    }
}
