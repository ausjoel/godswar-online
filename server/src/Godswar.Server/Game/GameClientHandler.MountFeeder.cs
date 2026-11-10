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
/// </list>
/// The submit packet of these forms has never been captured: the item slots are read where every other item form puts
/// them (argument 6 and 7 = page*100 + slot, PetManager/HolySuit/FactionCrier) and the action from the first path value
/// (menu or page id). Every submission is logged raw so the first real-client try settles it. Mount Gear upgrade (105)
/// is NOT DONE. Hot rate npc.mount_feeder (1 = on).
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int MountFeederFunction = 21;
    private const uint ArgentArmoredDragonShifter = 6205;

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
                106 or 206 => MountFeederAction.BindGear, _ => MountFeederAction.None }
        };
        Console.WriteLine($"[mount-feeder] character={_character.Name} sub={subId} args=[{string.Join(',', args)}] action={action}");
        if (action is MountFeederAction.None or MountFeederAction.GearUpgrade)
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
}
