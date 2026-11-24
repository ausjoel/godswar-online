using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// B22 B27-C (the admin d136): the Class Shifter's weapon shift (page-one 101) and armour shift (page-one 102).
/// <summary>
/// B27-C: the Class Shifter's equipment pages, answered with the client's own forms (NpcFunWork.lua Index 2).
/// <list type="bullet">
/// <item>101 "Please shift my weapon": item slot 1 + NF_L0_WK127, item slot 2 + the permit note, and the four "Turn
/// into a weapon for a [class]" buttons. Real client (B27-C rig): picking a class and pressing OK sends sub 101 with
/// the button in +16 and the two item slots in +40 / +44.</item>
/// <item>102 "Please shift my armor": item slot 1 + NF_L0_WK129, item slot 2 + the permit note; the window's OK sends
/// sub 102 with +16 = 0 and the slots in +40 / +44.</item>
/// </list>
/// The rules are <see cref="EquipmentShiftPolicy"/>. Every click is logged raw. Hot rate npc.class_shift_equipment
/// (1 = on); off, the old menu-only answers run unchanged.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleEquipmentShiftAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int subId,
        int selection,
        int pageSelection,
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (dialogue.FunctionNumber != ClassChangePolicy.FunctionNumber || _character is null ||
            !EquipmentShiftPolicy.Enabled())
        {
            return false;
        }

        var args = MountFeederArguments(packet);
        var request = EquipmentShiftPolicy.Classify(subId, selection, pageSelection, args[0], out var button);
        if (request == EquipmentShiftPolicy.Request.None)
        {
            return false;
        }

        if (request is EquipmentShiftPolicy.Request.WeaponPage or EquipmentShiftPolicy.Request.ArmourPage)
        {
            await SendScriptedNpcDialogueReplyAsync(
                npc, dialogue, dialogIndex, selection,
                request == EquipmentShiftPolicy.Request.WeaponPage
                    ? EquipmentShiftPolicy.WeaponPage
                    : EquipmentShiftPolicy.ArmourPage,
                cancellationToken);
            return true;
        }

        var isWeapon = request == EquipmentShiftPolicy.Request.Weapon;
        var firstSlot = SlotOf(args[6]);
        var level = _character.Level;
        var freeLevel = ClassChangePolicy.FreeLevel(Application.Rates.ServerRates.Current);
        var needsPermit = ClassChangePolicy.RequiresPermit(level, freeLevel);
        var profession = _character.Profession;
        var name = _character.Name;
        Console.WriteLine(
            $"[equipment-shift] character={name} {(isWeapon ? "weapon" : "armour")} sub={subId} " +
            $"profession={profession} level={level} free-level={freeLevel} args=[{string.Join(',', args)}] slot={firstSlot}");
        int result;
        if (isWeapon)
        {
            if (!EquipmentShiftPolicy.TryResolveWeaponButton(button, out var target))
            {
                result = EquipmentShiftPolicy.UnavailableLine; // OK with no class picked: "Please make a choice"
            }
            else if (firstSlot < 0)
            {
                result = EquipmentShiftPolicy.NoWeaponLine;
            }
            else
            {
                result = await RunEventsNpcAsync(
                    tx => ShiftWeaponAsync(tx, name, firstSlot, target, needsPermit),
                    EquipmentShiftPolicy.UnavailableLine,
                    cancellationToken);
            }
        }
        else
        {
            result = firstSlot < 0
                ? EquipmentShiftPolicy.NoArmourLine
                : await RunEventsNpcAsync(
                    tx => ShiftArmourAsync(tx, name, firstSlot, profession, needsPermit),
                    EquipmentShiftPolicy.UnavailableLine,
                    cancellationToken);
        }

        if (result is EquipmentShiftPolicy.ShiftedLine or EquipmentShiftPolicy.ArmourShiftedLine)
        {
            // Repaint the bag (the item changed in place; no notice follows the used permit, the admin's rule).
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        Console.WriteLine($"[equipment-shift] character={name} sub={subId} result={result}");
        await SendScriptedNpcDialogueReplyAsync(npc, dialogue, dialogIndex, selection, [result], cancellationToken);
        return true;
    }

    private static async Task<int> ShiftWeaponAsync(EventsNpcTransaction tx, string character, int slot,
        byte targetProfession, bool needsPermit)
    {
        if (await tx.ReadShiftSlotAsync(slot) is not { } item)
        {
            return EquipmentShiftPolicy.NoWeaponLine;
        }

        if (!EquipmentShiftPolicy.TryResolveTarget((uint)item.ItemId, targetProfession, out var targetItem,
                out var sourceProfession))
        {
            EquipmentShiftPolicy.IsExcluded((uint)item.ItemId, out var reason);
            Console.WriteLine($"[equipment-shift] character={character} item={item.ItemId} refused " +
                $"({(reason.Length > 0 ? reason : "not a shiftable weapon")})");
            return EquipmentShiftPolicy.WrongItemLine;
        }

        if (sourceProfession == targetProfession)
        {
            return EquipmentShiftPolicy.SameClassLine;
        }

        if (needsPermit && !await tx.ConsumeAsync(EquipmentShiftPolicy.PermitItemId, 1))
        {
            return EquipmentShiftPolicy.PermitMissingLine;
        }

        var attributes = EquipmentShiftPolicy.ShiftAttributes(item.Attributes, sourceProfession, targetProfession);
        await tx.ShiftItemAsync(item.Id, (int)targetItem, attributes);
        Console.WriteLine(
            $"[equipment-shift] character={character} weapon row={item.Id} slot={slot} item={item.ItemId}->{targetItem} " +
            $"profession={sourceProfession}->{targetProfession} bound={(item.Bound ? 1 : 0)}->1 " +
            $"attributes=[{Ids(item.Attributes)}]->[{Ids(attributes)}] permit={(needsPermit ? 1 : 0)}");
        return EquipmentShiftPolicy.ShiftedLine;
    }

    private static async Task<int> ShiftArmourAsync(EventsNpcTransaction tx, string character, int slot,
        byte profession, bool needsPermit)
    {
        if (await tx.ReadShiftSlotAsync(slot) is not { } item)
        {
            return EquipmentShiftPolicy.NoArmourLine;
        }

        var kind = await tx.ScalarAsync("SELECT kind FROM public.item_templates WHERE id = @i;", ("i", item.ItemId))
            as string;
        if (!EquipmentShiftPolicy.IsArmourKind(kind))
        {
            Console.WriteLine($"[equipment-shift] character={character} item={item.ItemId} kind={kind} refused (not armour)");
            return EquipmentShiftPolicy.NoArmourLine;
        }

        var attributes = EquipmentShiftPolicy.ConvertArmourAttributes(item.Attributes, profession);
        if (EquipmentShiftPolicy.IsClassSuit((uint)item.ItemId) || attributes.SequenceEqual(item.Attributes))
        {
            // NF_L0_WK123 "Wrong choice of item(s) or there is no need to transfer"; nothing is used.
            Console.WriteLine($"[equipment-shift] character={character} item={item.ItemId} refused " +
                $"({(EquipmentShiftPolicy.IsClassSuit((uint)item.ItemId) ? "Class Suit" : "nothing to convert")})");
            return EquipmentShiftPolicy.WrongItemLine;
        }

        if (needsPermit && !await tx.ConsumeAsync(EquipmentShiftPolicy.PermitItemId, 1))
        {
            return EquipmentShiftPolicy.PermitMissingLine;
        }

        await tx.ShiftItemAsync(item.Id, item.ItemId, attributes);
        Console.WriteLine(
            $"[equipment-shift] character={character} armour row={item.Id} slot={slot} item={item.ItemId} kind={kind} " +
            $"profession={profession} bound={(item.Bound ? 1 : 0)}->1 attributes=[{Ids(item.Attributes)}]->" +
            $"[{Ids(attributes)}] permit={(needsPermit ? 1 : 0)}");
        return EquipmentShiftPolicy.ArmourShiftedLine;
    }

    private static string Ids(IEnumerable<short?> attributes) =>
        string.Join(',', attributes.Select(static a => a?.ToString() ?? "-"));
}
