using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> SendPlayerSkillBookProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        if (receipt.Status == PetDurableReceiptStatus.PlayerSkillLearned)
        {
            var skills = _characterLoadSnapshot?.Skills ?? [];
            if (disposition == PetDurableExecutionDisposition.Committed &&
                !MatchesCommittedPlayerSkillLearn(receipt, skills))
            {
                return false;
            }

            var activeSkillInfo = PacketBuilder.ActiveSkillInfo(skills);
            if (activeSkillInfo.Length == 0)
            {
                return false;
            }
            await _session.SendAsync(
                activeSkillInfo,
                cancellationToken,
                "PlayerSkillBookActiveSkillInfo");

            if (receipt.KitBagSlot >= 0 &&
                KitBagSlots.GetItem(
                    _character.KitBag,
                    receipt.KitBagSlot).IsEmpty)
            {
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(
                        receipt.KitBagSlot),
                    cancellationToken,
                    "PlayerSkillBookSlotClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);
            return true;
        }

        await _session.SendAsync(
            PacketBuilder.LocalizedError(
                PlayerSkillBookErrorCode(receipt.Status)),
            cancellationToken,
            "PlayerSkillBookRejected");
        await SendKitBagRefreshAsync(cancellationToken);
        return true;
    }

    private static bool MatchesCommittedPlayerSkillLearn(
        PetDurableReceipt receipt,
        IReadOnlyList<SkillState> skills) =>
        receipt.PlayerSkillLearn is { } learned &&
        skills.Any(skill =>
            skill.SkillId == learned.SkillId &&
            skill.Level == learned.SkillLevel) &&
        (learned.PreviousSkillId is not { } previousSkillId ||
         skills.All(skill => skill.SkillId != previousSkillId));

    private static int PlayerSkillBookErrorCode(
        PetDurableReceiptStatus status) =>
        status switch
        {
            PetDurableReceiptStatus.PlayerSkillBookAlreadyLearned =>
                NativeErrorCodes.SkillAlreadyLearned,
            PetDurableReceiptStatus.PlayerSkillBookWrongClass or
            PetDurableReceiptStatus.PlayerSkillBookLevelRestricted or
            PetDurableReceiptStatus.PlayerSkillBookPriorTierRequired =>
                NativeErrorCodes.SkillRequirementsNotMet,
            _ => NativeErrorCodes.UnavailableSkill
        };

    private static bool IsPlayerSkillBookReceipt(
        PetDurableReceiptStatus status) =>
        status is >= PetDurableReceiptStatus.PlayerSkillLearned and
            <= PetDurableReceiptStatus.PlayerSkillBookInvalidState;
}
