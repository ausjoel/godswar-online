using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W2 Mount Feeder (Athens_116 / Sparta_116, function 21; the admin F-52 "re-skin then level-up"). Client script
/// NpcFunHorseFeeder.lua; result texts NF_HF_T301-317 and T988-999 (LuaText.lua).
/// <list type="bullet">
/// <item>Upgrade (100/200): mount in item slot 1; Lv N -> N+10 uses N+10 Soul Stones of that level (NF_HF_T200/303-310).</item>
/// <item>Degrade (102/201): Lv41+ -> Lv40, the Soul Stones used are refunded, mount and stones bound (T201/T315).</item>
/// <item>Alter appearance (103/203): mount in slot 1, Mount Shifter in slot 2 -> same level in the shifter's family,
/// bound; the old family's shifter is given back (T203/T994).</item>
/// <item>Exchange for a shifter (104/204): a Lv40 mount (not Argent Armored Dragon) -> its family's shifter (T204/T996).</item>
/// <item>Bind mount gear (106/206): unbound mount gear -> bound (T206/T988/T989).</item>
/// <item>B27 (the admin d138) Upgrading Mount Gear (105/205): mount gear + 1 Golden Gem (4259) -> the next grade of the same
/// line (item id + 1: PlayLv +10, Boorish Lv40 ... Faithcreed Lv120), bound (NF_HF_T205). Results T990 done, T991 already
/// the top grade (x08), T992 wrong items. Each appended attribute keeps its kind; a tier the new grade does not allow is
/// raised to the lowest tier it does allow (item_allowed_attributes), so no stat is dropped (GENERATED rule).</item>
/// </list>
/// The submit packet of these forms has never been captured: the item slots are read where every other item form puts
/// them (argument 6 and 7 = page*100 + slot, PetManager/HolySuit/FactionCrier) and the action from the first path value
/// (menu or page id). Every submission is logged raw so the first real-client try settles it. Hot rate npc.mount_feeder
/// (1 = on).
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int MountFeederFunction = 21;
    private const uint ArgentArmoredDragonShifter = 6205;
    private const int GoldenGem = 4259; // B27 (d138): Material4259, "raise the levels of your mount's gear"

    private enum MountFeederAction { None, Upgrade, Degrade, Alter, Exchange, BindGear, GearUpgrade }

    private static int[] MountFeederArguments(GamePacket packet)
    {
        var payload = packet.Payload;
        // Payload: npc, dialog, dialog, sub (offset 12), then 18 arguments from offset 16 (FactionCrierProtocol layout).
        var args = new int[18];
        for (var i = 0; i < args.Length; i++)
            args[i] = 16 + i * 4 + 4 <= payload.Length ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16 + i * 4, 4)) : -1;
        return args;
    }

    private static int SlotOf(int coordinate) =>
        coordinate is >= 0 && coordinate / 100 < 4 && coordinate % 100 < 24 ? coordinate / 100 * 24 + coordinate % 100 : -1;

    private async Task<bool> TryHandleMountFeederAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || !Finalise2Rates.On(Finalise2RateKeys.MountFeeder) ||
            npc.NpcKey is not ("Athens_116" or "Sparta_116"))
            return false;
        var args = MountFeederArguments(packet);
        var first = SlotOf(args[6]);
        if (first < 0) return false; // no item placed: the ordinary pages are drawn as before
        var marker = args[0] >= 0 ? args[0] : subId;
        var action = marker switch
        {
            100 or 200 => MountFeederAction.Upgrade,
            102 or 201 => MountFeederAction.Degrade,
            103 or 203 => MountFeederAction.Alter,
            104 or 204 => MountFeederAction.Exchange,
            106 or 206 => MountFeederAction.BindGear,
            105 or 205 => MountFeederAction.GearUpgrade,
            _ => subId switch { 100 or 200 => MountFeederAction.Upgrade, 102 or 201 => MountFeederAction.Degrade,
                103 or 203 => MountFeederAction.Alter, 104 or 204 => MountFeederAction.Exchange,
                106 or 206 => MountFeederAction.BindGear, 105 or 205 => MountFeederAction.GearUpgrade, // B27
                _ => MountFeederAction.None }
        };
        Console.WriteLine($"[mount-feeder] character={_character.Name} sub={subId} args=[{string.Join(',', args)}] action={action}");
        if (action is MountFeederAction.None) // B27: GearUpgrade is built (d138)
        {
            await SendFinalise2ReplyAsync(npc, MountFeederFunction, [993], "MountFeederUnsupported", cancellationToken);
            return true;
        }

        var second = SlotOf(args[7]);
        var result = await RunEventsNpcAsync(tx => MountFeederAsync(tx, action, first, second), 993, cancellationToken);
        Console.WriteLine($"[mount-feeder] character={_character.Name} action={action} result={result}");
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendFinalise2ReplyAsync(npc, MountFeederFunction, [result], "MountFeederResult", cancellationToken);
        return true;
    }

    private static async Task<int> MountFeederAsync(EventsNpcTransaction tx, MountFeederAction action, int slot, int secondSlot)
    {
        if (action == MountFeederAction.GearUpgrade) return await MountGearUpgradeAsync(tx, slot, secondSlot); // B27
        var item = await tx.ReadSlotAsync(slot);
        if (action == MountFeederAction.BindGear)
        {
            if (item is not { } gear || !MountFamilyCatalog.IsMountGear((uint)gear.ItemId) || gear.Bound) return 988;
            await tx.ReplaceItemAsync(gear.Id, gear.ItemId, bound: true);
            return 989;
        }
        if (item is not { } mount) return action == MountFeederAction.Upgrade ? 301 : 999;
        if (!MountFamilyCatalog.TryFind((uint)mount.ItemId, out var family, out var level))
            return action == MountFeederAction.Upgrade ? 312 : 999;
        switch (action)
        {
            case MountFeederAction.Upgrade:
            {
                if (level < 40) return 316;              // a legacy Lv38 mount: not converted
                if (level >= 120) return 302;            // full potential
                var target = level + 10;
                if (target < 50) return 312;
                var stone = (int)MountFamilyCatalog.SoulStoneFor(target);
                if (!await tx.ConsumeAsync(stone, target)) return 303 + (target - 50) / 10;
                await tx.ReplaceItemAsync(mount.Id, (int)family.ItemAt(target), mount.Bound);
                return 311;
            }
            case MountFeederAction.Degrade:
            {
                if (level <= 40) return 999;
                for (var step = 50; step <= level; step += 10)
                {
                    if (!await tx.GrantAsync((int)MountFamilyCatalog.SoulStoneFor(step), step, bound: true))
                    {
                        tx.Rollback();
                        return 314;
                    }
                }
                await tx.ReplaceItemAsync(mount.Id, (int)family.ItemAt(40), bound: true);
                return 315;
            }
            case MountFeederAction.Alter:
            {
                if (level < 40) return 999;
                if (await tx.ReadSlotAsync(secondSlot) is not { } shifter ||
                    MountFamilyCatalog.ByShifter((uint)shifter.ItemId) is not { } into) return 998;
                if (into == family) return 997;
                if (!await tx.ConsumeAsync(shifter.ItemId, 1)) return 998;
                await tx.ReplaceItemAsync(mount.Id, (int)into.ItemAt(level), bound: true);
                if (!await tx.GrantAsync((int)family.ShifterId, 1, bound: true))
                {
                    tx.Rollback();
                    return 314;
                }
                return 994;
            }
            case MountFeederAction.Exchange:
            {
                if (level != 40 || family.ShifterId == ArgentArmoredDragonShifter) return 995;
                await tx.DeleteItemAsync(mount.Id);
                if (!await tx.GrantAsync((int)family.ShifterId, 1, bound: mount.Bound))
                {
                    tx.Rollback();
                    return 314;
                }
                return 996;
            }
        }
        return 993;
    }

    /// <summary>
    /// B27 (the admin d138): NF_HF_T205. The gear and the Golden Gem are found by content in the two slots (either order).
    /// 992 = not mount gear plus a Golden Gem; 991 = already the line's top grade (x08, Lv120); 990 = upgraded.
    /// </summary>
    private static async Task<int> MountGearUpgradeAsync(EventsNpcTransaction tx, int slot, int secondSlot)
    {
        var a = await tx.ReadSlotAsync(slot);
        var b = secondSlot >= 0 && secondSlot != slot ? await tx.ReadSlotAsync(secondSlot) : null;
        var gear = a is { } x && MountFamilyCatalog.IsMountGear((uint)x.ItemId) ? a
            : b is { } y && MountFamilyCatalog.IsMountGear((uint)y.ItemId) ? b : null;
        var gem = a is { ItemId: GoldenGem } ? a : b is { ItemId: GoldenGem } ? b : null;
        if (gear is not { } piece) return 992;
        var next = MountGearNextGrade(piece.ItemId);
        if (next == 0) return 991;
        if (gem is null || !await tx.ConsumeAsync(GoldenGem, 1)) return 992;
        await tx.ReplaceItemAsync(piece.Id, next, bound: true);
        await tx.ScalarAsync(MountGearAttributeSql, ("id", piece.Id), ("i", next));
        return 990;
    }

    /// <summary>B27: the next grade of a mount gear line (x00-x07 -> +1), 0 at the top (x08) or for anything else.</summary>
    internal static int MountGearNextGrade(int itemId) =>
        MountFamilyCatalog.IsMountGear((uint)itemId) && itemId % 100 < 8 ? itemId + 1 : 0;

    /// <summary>
    /// B27: every appended attribute (attribute1-5, id = kind*10 + tier) that the new item does not allow moves to the
    /// lowest allowed tier of the same kind at or above its own (or the highest allowed when none is above); NULLs and
    /// kinds the item does not list stay as they are.
    /// </summary>
    private static readonly string MountGearAttributeSql =
        "UPDATE public.character_items ci SET " + string.Join(", ", Enumerable.Range(1, 5).Select(static n =>
            $"attribute{n} = COALESCE((SELECT min(a.attribute_id) FROM public.item_allowed_attributes a WHERE a.item_id = @i " +
            $"AND a.attribute_id / 10 = ci.attribute{n} / 10 AND a.attribute_id >= ci.attribute{n}), " +
            $"(SELECT max(a.attribute_id) FROM public.item_allowed_attributes a WHERE a.item_id = @i " +
            $"AND a.attribute_id / 10 = ci.attribute{n} / 10), ci.attribute{n})")) +
        " WHERE ci.id = @id AND ci.user_id = @c RETURNING ci.id;";
}
