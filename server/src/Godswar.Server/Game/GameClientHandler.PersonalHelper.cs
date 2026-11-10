using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W2 Personal Helper (Athens_139 / Sparta_139, function 107, client NpcFunBatch.lua): uses one placed item 1-99 times
/// in one go. Only Morning Dews (and the Pet EXP Stones that share their effect), EXP Stones, Talent Stones, Gold Bags
/// and B-Gold Bags (NF_L0_BATCH100T). Each use runs the ordinary right-click path (BatchUse skips the 1-second
/// consumable cooldown). Replies: 211 done, 297 insufficient items, 298 wrong item / amount, 299 pet not summoned.
/// Item slot = argument 6, amount = the typed amount (payload +0x38); the submit packet is logged raw.
/// Hot rate npc.personal_helper.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int PersonalHelperFunction = 107;

    private int BagCount(uint itemId) => Enumerable.Range(0, 96)
        .Select(slot => KitBagSlots.GetItem(_character!.KitBag, slot)).Where(entry => entry.Id == itemId).Sum(entry => (int)entry.Stack);

    private int BagFirstSlot(uint itemId) =>
        Enumerable.Range(0, 96).FirstOrDefault(slot => KitBagSlots.GetItemId(_character!.KitBag, slot) == itemId, -1);

    private bool HasSummonedPet() =>
        (_characterLoadSnapshot?.Pets ?? []).Any(static pet => pet.IsCarried && pet.IsSummoned);

    private bool IsBatchUsable(uint itemId)
    {
        var templates = RequireItemContent().Templates;
        return PetExperienceItemPolicy.IsMorningDew(itemId) ||
            PlayerExperienceItemPolicy.TryResolveAmount(templates, itemId, out _) ||
            ProgressItemPolicy.TryResolve(templates, itemId, out _) ||
            MoneyBagPolicy.TryResolve(templates, itemId, out _);
    }

    private async Task<bool> TryHandlePersonalHelperAsync(NpcSpawnDefinition npc, int subId, GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || !Finalise2Rates.On(Finalise2RateKeys.PersonalHelper) ||
            npc.NpcKey is not ("Athens_139" or "Sparta_139"))
            return false;
        var args = MountFeederArguments(packet);
        var slot = SlotOf(args[6]);
        var amount = DialogAmount(packet);
        if (slot < 0 && amount <= 0) return false; // opening the form
        Console.WriteLine($"[personal-helper] character={_character.Name} sub={subId} args=[{string.Join(',', args)}] amount={amount}");
        var item = slot >= 0 ? KitBagSlots.GetItem(_character.KitBag, slot) : default;
        int reply;
        if (slot < 0 || item.IsEmpty || !IsBatchUsable(item.Id) || amount is < 1 or > 99)
            reply = 298;
        else if (BagCount(item.Id) < amount)
            reply = 297;
        else if (PetExperienceItemPolicy.IsMorningDew(item.Id) && !HasSummonedPet())
            reply = 299;
        else if (!_session.IsSecure && !AllowLegacyPlayerMutationFallback("personal_helper_batch_use"))
            return true;
        else
        {
            var used = 0;
            var itemId = item.Id;
            for (var n = 0; n < amount; n++)
            {
                var current = BagFirstSlot(itemId);
                if (current < 0) break;
                var receipt = await HandleDurableBagItemActivationAsync(
                    PetCommandOperationIdentity.RawLocalServer(Guid.NewGuid(), _commandConnectionId), current,
                    cancellationToken, executionConstraint: BagItemActivationExecutionConstraint.BatchUse);
                if (receipt is not { Succeeded: true }) break;
                used++;
            }
            Console.WriteLine($"[personal-helper] character={_character.Name} item={itemId} asked={amount} used={used}");
            reply = used > 0 ? 211 : 297;
        }
        await SendFinalise2ReplyAsync(npc, PersonalHelperFunction, [reply], "PersonalHelper", cancellationToken);
        return true;
    }
}
