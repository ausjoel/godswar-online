using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Game;

/// <summary>
/// ROUND3 summons (the admin F-27 "build all", 2026-11-09): the Sacred Sealer (Athens_130 / Sparta_130, function 34,
/// client NpcFunLegendary.lua, texts Legendary1-11 in LuaText.lua). The page's buttons 102 / 103 / 104 are
/// "Buy 1 Ladon Scroll (180 Gold)", "Buy 1 Cyclops Scroll (900 Gold)", "Buy 1 Medusa Scroll (1,800 Gold)"; the
/// result page is 201 "make more space", 202 "not enough Gold", 203 / 204 / 205 "You bought 1 ... Scroll".
/// The Athens Sealer sells the Athens scrolls 14060-14062 (Camp 1), the Sparta Sealer 14063-14065 (Camp 0).
/// The scrolls themselves are #97's boss scrolls (<see cref="SummonItemPolicy"/>); with this switch on, a scroll
/// boss is fought by its summoner only and drops its Treasure Chest (Ladon 14066 Copper, Cyclops 14067 Silver,
/// Medusa 14068 Gold: "Open to get Ladon's riches"), whose contents are the existing GENERATED gift packs.
/// Switch: npc.sacred_sealer (default 1); 0 = the page shows its buttons and nothing is sold, as before.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int SacredSealerFunction = 34;

    private async Task<bool> HandleSacredSealerAsync(NpcSpawnDefinition npc, int button,
        CancellationToken cancellationToken)
    {
        if (!SacredSealerPolicy.TryGetOffer(npc.NpcKey, button, out var offer))
        {
            return false;
        }

        var result = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.TrySpendGoldAsync(offer.Price) is null)
            {
                return SacredSealerPolicy.NotEnoughGold;
            }
            if (!await tx.GrantAsync(offer.ScrollItemId, 1, bound: null))
            {
                tx.Rollback();
                return SacredSealerPolicy.BagFull;
            }
            return offer.BoughtReply;
        }, SacredSealerPolicy.NotEnoughGold, cancellationToken);
        Console.WriteLine($"[sacred-sealer] character={_character!.Name} npc={npc.NpcKey} button={button} " +
            $"scroll={offer.ScrollItemId} price={offer.Price} result={result}");
        if (result == offer.BoughtReply)
        {
            await RefreshAfterEventsNpcAsync([(offer.ScrollItemId, 1)], cancellationToken);
        }

        await SendFinalise2ReplyAsync(npc, SacredSealerFunction, [result], "SacredSealer", cancellationToken);
        return true;
    }
}

/// <summary>One scroll the Sacred Sealer sells.</summary>
internal readonly record struct SacredSealerOffer(int ScrollItemId, int Price, int BoughtReply);

/// <summary>ROUND3 summons: the Sacred Sealer's prices (client Legendary4-6) and its scrolls' chests.</summary>
internal static class SacredSealerPolicy
{
    public const int BagFull = 201;       // Legendary7
    public const int NotEnoughGold = 202; // Legendary8

    /// <summary>The scroll, Gold price and "bought" reply of a button, by the Sealer's city.</summary>
    public static bool TryGetOffer(string npcKey, int button, out SacredSealerOffer offer)
    {
        var athens = npcKey == "Athens_130";
        if (!athens && npcKey != "Sparta_130" || button is < 102 or > 104)
        {
            offer = default;
            return false;
        }
        var index = button - 102; // 0 Ladon, 1 Cyclops, 2 Medusa
        offer = new SacredSealerOffer((athens ? 14060 : 14063) + index, index switch
        {
            0 => 180,
            1 => 900,
            _ => 1_800
        }, 203 + index);
        return true;
    }

    /// <summary>The Treasure Chest a scroll's boss drops (14066 Copper / 14067 Silver / 14068 Gold), or 0.</summary>
    public static int ChestFor(int scrollItemId) => scrollItemId switch
    {
        14060 or 14063 => 14066,
        14061 or 14064 => 14067,
        14062 or 14065 => 14068,
        _ => 0
    };
}
