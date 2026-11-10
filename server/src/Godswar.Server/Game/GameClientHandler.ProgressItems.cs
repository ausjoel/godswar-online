using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsProgressItemProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.ProgressItemUsed or PetDurableReceiptStatus.ProgressItemBalanceFull or
            PetDurableReceiptStatus.ProgressItemNoGuild ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive && _character is not null &&
        receipt.KitBagSlot is >= 0 and < 96 && ProgressItemPolicy.TryResolve(RequireItemContent().Templates,
            KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot), out _);

    private (uint Target, float X, float Z)? _sigilTarget;

    // The stone is used up only when a live monster in reach is named. The frame's first two words carry the client's target.
    private bool TrySelectSigilTarget(GamePacket packet, in SkillCombatDefinition combat)
    {
        _sigilTarget = null;
        if (_character is null || packet.Payload.Length < 8 || _character.CurrentHp <= 0 ||
            _registry.PlayerRuntimeMode != PlayerRuntimeMode.Ecs) return false;
        for (var offset = 0; offset < 8; offset += 4)
        {
            var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(offset, 4));
            if (objectId == uint.MaxValue || objectId == LocalPlayerObjectId) continue;
            if (!_registry.TryCapturePlayerMonsterTarget(_session, _character.CurrentMap, objectId, out var target, out _) ||
                !target.IsSpawned || !target.IsAlive || !_registry.IsMonsterVisibleTo(_session, objectId, target.SpawnGeneration) ||
                !SkillCombatResolver.IsWithinRange(_character.PositionX, _character.PositionZ, target.X, target.Z, combat)) continue;
            _sigilTarget = (objectId, target.X, target.Z);
            return true;
        }
        return false;
    }

    // Runs the normal hostile skill path, as if the client had cast the stone's skill: same hit, kill, EXP and loot.
    private async Task CastSigilStoneAsync(uint itemId, CancellationToken token)
    {
        if (_character is null || _sigilTarget is not { } target ||
            !SigilStonePolicy.TryResolve(RequireItemContent().Templates, itemId, out var combat)) return;
        _sigilTarget = null;
        var buffer = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), checked((uint)combat.SkillId));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), target.Target);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(24), _character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(28), _character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(32), target.X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(36), target.Z);
        var cast = new SkillCastRequest(LocalPlayerObjectId, checked((uint)combat.SkillId), target.Target,
            _character.PositionX, _character.PositionZ, target.X, target.Z, true);
        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
            await HandleHostileMonsterAreaSkillCastAsync(new GamePacket(buffer), cast, combat, true, token);
        else
            await HandleHostileMonsterSingleSkillCastEcsAsync(new GamePacket(buffer), cast, combat, true, null, token);
    }

    /// <summary>
    /// Firework of Love (3949) casts skill 4825 "Romantic Firework", whose own Magic.ini entry has no
    /// EffectFile; the client ships three follow-up skills 4827-4829 ("Romantic Firework 1/2/3") that
    /// play scene_jiehunlihua_a(1-3).gwm. The impact therefore carries those three back to back (GENERATED
    /// reading of the client data: no capture of the original order or spacing exists).
    /// </summary>
    internal static uint[] FireworkImpactSkillIds(int skillId) =>
        skillId == 4825 ? [4827u, 4828u, 4829u] : [checked((uint)skillId)];

    private async Task<bool> SendProgressItemProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, string previousKitBag, CancellationToken token)
    {
        if (_character is null) return false;
        var firstCommit = disposition == PetDurableExecutionDisposition.Committed;
        if (receipt.Status == PetDurableReceiptStatus.ProgressItemUsed)
        {
            if (receipt.ProgressItem is not { IsValid: true } evidence) return false;
            if (firstCommit && KitBagSlots.GetItemId(previousKitBag, receipt.KitBagSlot) == evidence.ItemTemplateId)
                await _session.SendAsync(PacketBuilder.BagConsumableActivation(previousKitBag,
                    receipt.KitBagSlot, LocalPlayerObjectId), token, "ProgressItemActivation");
            if (KitBagSlots.GetItem(_character.KitBag, receipt.KitBagSlot).IsEmpty)
                await _session.SendAsync(PacketBuilder.StorageItemKitBagDelete(receipt.KitBagSlot), token,
                    "ProgressItemConsumedSlotClear");
            await SendKitBagRefreshAsync(token);
            // Duplicates get the current balances only, never a historical delta.
            await _session.SendAsync(BuildLocalPlayerStatusUpdate(), token, "ProgressItemCurrentBalances");
            if (firstCommit && evidence.Kind switch // FINALISE
                {
                    ProgressItemKind.Gold => "Gold",
                    ProgressItemKind.BindingGold => "Bound Gold",
                    ProgressItemKind.Honor => "Honor",
                    ProgressItemKind.ExchangePoint => "Exchange Points",
                    ProgressItemKind.TalentPoints => "TP",
                    ProgressItemKind.GuildSilver => "Guild Silver",
                    ProgressItemKind.GuildGold => "Guild Gold",
                    ProgressItemKind.Silver => "Silver",
                    _ => null
                } is { } unit)
                await SendGainLogAsync($"Obtained {Amount(evidence.Amount)} {unit}", token);
            if (evidence.Kind == ProgressItemKind.Title)
                await _registry.PublishItemTitleAsync(_session, checked((uint)evidence.Amount), evidence.NewRevision, token);
            if (firstCommit && evidence.Kind == ProgressItemKind.Firework &&
                ProgressItemPolicy.TryResolve(RequireItemContent().Templates, evidence.ItemTemplateId, out var firework))
            {
                var worldId = CurrentPlayerObjectId;
                var fireworkSkill = checked((uint)firework.SkillId);
                await _session.SendAsync(PacketBuilder.BagConsumableSkillCastVisual(LocalPlayerObjectId, LocalPlayerObjectId,
                    fireworkSkill, _character.PositionX, _character.PositionZ,
                    _character.PositionX, _character.PositionZ), token, "FireworkCastSelf");
                await _registry.BroadcastToMapAsync(_character.CurrentMap, PacketBuilder.BagConsumableSkillCastVisual(
                    worldId, worldId, fireworkSkill, _character.PositionX, _character.PositionZ,
                    _character.PositionX, _character.PositionZ), token, _session, "FireworkCastWorld");
                // Captured consumable order is cast visual (10040) then impact (10046): the client
                // plays a skill's Magic.ini EffectFile on the impact, so the cast alone shows only
                // the action. Fireworks sent the cast alone until now.
                var impacts = FireworkImpactSkillIds(firework.SkillId);
                foreach (var impactSkill in impacts)
                {
                    await _session.SendAsync(PacketBuilder.SkillCastImpact(LocalPlayerObjectId, LocalPlayerObjectId,
                        impactSkill, _character.PositionX, _character.PositionZ), token, "FireworkImpactSelf");
                    await _registry.BroadcastToMapAsync(_character.CurrentMap, PacketBuilder.SkillCastImpact(worldId, worldId,
                        impactSkill, _character.PositionX, _character.PositionZ), token, _session, "FireworkImpactWorld");
                }
            }
            else if (firstCommit && evidence.Kind == ProgressItemKind.StatusBuff)
            {
                if (StatusItemPolicy.TryResolve(RequireItemContent().Templates, evidence.ItemTemplateId, out var statusItem))
                {
                    await _registry.ApplyRuntimeStatusAndPublishAsync(_session, statusItem, DateTimeOffset.UtcNow,
                        "status-item", token);
                    if (StatusItemPolicy.IsPeace(evidence.ItemTemplateId))
                        _character.PeaceUntil = DateTimeOffset.UtcNow + statusItem.Duration;
                }
            }
            else if (firstCommit && evidence.Kind == ProgressItemKind.Sigil)
            {
                await CastSigilStoneAsync(evidence.ItemTemplateId, token);
            }
            else if (firstCommit && evidence.Kind == ProgressItemKind.Teleport)
            {
                if (TeleportItemPolicy.TryResolve(RequireItemContent().Templates, evidence.ItemTemplateId, out var scroll))
                {
                    if (scroll.MapId != TeleportItemPolicy.RandomMapId)
                        await OperatorTeleportSelfAsync(scroll.MapId, scroll.X, scroll.Z, token);
                    else if (TeleportItemPolicy.TryPickRandom(_character.CurrentMap, out var x, out var z))
                        await OperatorTeleportSelfAsync(_character.CurrentMap, x, z, token);
                }
            }
            return true;
        }
        await SendKitBagRefreshAsync(token);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), token, "ProgressItemCurrentBalances");
        return true;
    }
}
