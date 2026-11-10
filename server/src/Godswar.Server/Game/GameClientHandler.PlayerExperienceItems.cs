using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsPlayerExperienceItemProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.PlayerExperienceAdded or
            PetDurableReceiptStatus.PlayerExperienceMaximumReached ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive &&
            _character is not null && receipt.KitBagSlot >= 0 &&
            PlayerExperienceItemPolicy.IsExperienceItem(KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot));

    private async Task<bool> SendPlayerExperienceItemProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, CancellationToken token)
    {
        if (_character is null) return false;
        if (receipt.Succeeded && KitBagSlots.GetItem(_character.KitBag, receipt.KitBagSlot).IsEmpty)
            await _session.SendAsync(PacketBuilder.StorageItemKitBagDelete(receipt.KitBagSlot), token,
                "PlayerExperiencePillConsumedSlotClear");
        await SendKitBagRefreshAsync(token);

        if (!receipt.Succeeded)
        {
            return true;
        }

        var firstCommit = disposition == PetDurableExecutionDisposition.Committed;
        if (receipt.PlayerExperience is not { } evidence) return false;
        // Always project the freshly read character. A replay must never restore historical EXP.
        if (firstCommit && evidence.NewLevel != evidence.PreviousLevel)
        {
            await RefreshCalculatedStatsAfterExperienceItemAsync(token);
            var maximum = PlayerExperienceCatalog.GetClientExperienceMaximum(_character.Level, _character.FighterLevelSealed);
            await _session.SendAsync(PacketBuilder.PlayerLevelUp(LocalPlayerObjectId, _character.Level,
                maximum, _character.Experience, _character.MaxHp, _character.CurrentHp,
                _character.MaxMp, _character.CurrentMp), token, "PlayerExperiencePillLevelUp");
            await _registry.BroadcastToMapAsync(_character.CurrentMap,
                PacketBuilder.PlayerLevelUp(CurrentPlayerObjectId, _character.Level, maximum,
                    _character.Experience, _character.MaxHp, _character.CurrentHp, _character.MaxMp, _character.CurrentMp),
                token, _session, "PlayerExperiencePillLevelUpWorld");
        }
        await _session.SendAsync(PacketBuilder.ExperienceGain(firstCommit ? evidence.ExperienceGranted : 0,
            _character.Experience), token, "PlayerExperiencePillExperience");
        if (firstCommit) // FINALISE
            await SendGainLogAsync(evidence.NewLevel != evidence.PreviousLevel
                ? $"Obtained {Amount(evidence.ExperienceGranted)} EXP (level {evidence.PreviousLevel} -> {evidence.NewLevel})"
                : $"Obtained {Amount(evidence.ExperienceGranted)} EXP", token);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), token, "PlayerExperiencePillStatus");
        if (firstCommit && evidence.NewLevel != evidence.PreviousLevel)
            await SendQuestLevelUpRefreshAsync(token);
        return true;
    }

    // Same refresh a monster-kill level-up does: new maxima, without restoring older database vitals.
    private async Task RefreshCalculatedStatsAfterExperienceItemAsync(CancellationToken token)
    {
        if (_character is null || _account is null) return;
        try
        {
            var projection = await _characterRuntimeProjections.ReadCalculatedStatsAsync(
                _account.Id, _character.Id, token);
            if (projection is null) return;
            var stats = CharacterLoadSnapshotHydrator.MapCalculatedStats(projection);
            lock (_character.VitalsSync)
            {
                var currentHp = _character.CurrentHp;
                var currentMp = _character.CurrentMp;
                stats.ApplyTo(_character);
                ApplyElementalPassiveStats(_character, stats);
                _character.CurrentHp = Math.Clamp(currentHp, 0, _character.MaxHp);
                _character.CurrentMp = Math.Clamp(currentMp, 0, _character.MaxMp);
                _character.MarkVitalsChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[reward] EXP item stat refresh deferred character={_character.Name}: {ex.Message}");
        }
    }
}
