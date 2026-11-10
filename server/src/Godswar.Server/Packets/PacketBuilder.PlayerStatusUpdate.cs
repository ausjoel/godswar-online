using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort PlayerStatusUpdateOpcode = 0x27B6;
    private const int PlayerStatusMapIdOffset = 42;
    private const int PlayerStatusMovementSpeedMultiplierOffset = 56;
    private const int PlayerStatusCurrentHpOffset = 104;
    private const int PlayerStatusCurrentMpOffset = 108;
    // Wire offset 60 is copied to GameData+0x290. Although that dword looks
    // unused in the status panel, the native NPC interaction path reads byte
    // GameData+0x292 as the local interaction identity/faction. Writing a
    // float here can therefore make every NPC unselectable client-side.
    private const int PlayerStatusInteractionIdentityOffset = 60;
    private const int PlayerStatusCampOffset = 62;
    // MSG_SYN_GAMEDATA copies wire offset 8 to GameData+0x25C (the block copy at
    // 0x5B5545 pushes GameData+0x25C), so the character panel's two guild rows
    // read these slots:
    //   wire 76 -> GameData+0x2A0, ONE byte: the PersonalInfoUI DutyText row
    //              (公会职位). The panel reads it with `movzx edx, byte ptr` and
    //              formats it "G%d"; the attribute writer 0x4A8A00 fills it for
    //              attribute id 0x0A (`mov byte ptr [ebx+0x2A0], cl`).
    //   wire 80 -> GameData+0x2A4, a dword: the ContributeText row (公会贡献),
    //              formatted "%"; the same writer fills it for id 0x0B.
    // GameData+0x2A8 (wire 84) is written for id 0x0C, but the panel never reads
    // it, so the captured template's 40 there is left alone.
    // Measured 2026-10-02 in the client: the first attempt wrote the duty to +80,
    // and the client showed it in the CONTRIBUTION row with the position row
    // empty - which is what sent this back to the disassembly.
    private const int PlayerStatusGuildDutyOffset = 76;
    private const int PlayerStatusGuildContributionOffset = 80;
    private const int PlayerStatusSilverOffset = 120;
    private const int PlayerStatusGoldOffset = 124;
    private const int PlayerStatusMedusaHonorOffset = 128;
    private const int PlayerStatusExchangePointOffset = 132;
    private const int PlayerStatusBindingGoldOffset = 136;
    private const int PlayerStatusExchangeMedalOffset = 140;
    private const int PlayerStatusPhysicalDefenseOffset = 164;
    private const int PlayerStatusMagicDefenseOffset = 172;
    private const int PlayerStatusHitOffset = 176;
    private const int PlayerStatusDodgeOffset = 180;
    private const int PlayerStatusCriticalOffset = 184;
    private const int PlayerStatusCriticalResistanceOffset = 188;
    private const int PlayerStatusAttackIntervalShortOffset = 114;
    private const int PlayerStatusAttackIntervalDwordOffset = 224;
    private const int PlayerStatusTalentPointsOffset = 228;
    private const int PlayerStatusPkModeOffset = 232;

    private static readonly byte[] PlayerStatusUpdateTemplate = Convert.FromHexString(
        "EC00B6271D01000074657374696E6739000000000000000000000000000000000000000000000000" +
        "0100000000002543000000000000C2C20000803F0000000000000000000000000000000000000000" +
        "000000002800000001000000010000000000000001000000330500007C0100000100DC0535000000" +
        "000000000000000000000000000000000000000000000000330500007C010000320000002F000000" +
        "14000000220000000F00000006000000140000001D00000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000001000000DC0500000000000005000000");

    public static byte[] PlayerStatusUpdate(GameCharacter character, uint objectId)
    {
        return PlayerStatusUpdate(
            character,
            objectId,
            ClientStatusAggregate.Empty);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        float movementSpeedMultiplier)
    {
        return PlayerStatusUpdate(
            character,
            LocalPlayerObjectId,
            ClientStatusAggregate.Empty with
            {
                MovementSpeedMultiplier = movementSpeedMultiplier
            });
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        float movementSpeedMultiplier)
    {
        return PlayerStatusUpdate(
            character,
            objectId,
            ClientStatusAggregate.Empty with
            {
                MovementSpeedMultiplier = movementSpeedMultiplier
            });
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        ClientStatusAggregate aggregate)
    {
        return PlayerStatusUpdate(
            character,
            LocalPlayerObjectId,
            aggregate);
    }

    public static byte[] PlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        ClientStatusAggregate aggregate)
    {
        if (character.Camp is not (
                GameDefaults.SpartaCamp or
                GameDefaults.AthensCamp))
        {
            throw new ArgumentOutOfRangeException(
                nameof(character),
                character.Camp,
                "The character camp must be Sparta (0) or Athens (1).");
        }

        if (!float.IsFinite(aggregate.MovementSpeedMultiplier) ||
            aggregate.MovementSpeedMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.MovementSpeedMultiplier,
                "The movement-speed multiplier must be finite and positive.");
        }

        if (!float.IsFinite(aggregate.EquippedRidingSpeedBonus) ||
            aggregate.EquippedRidingSpeedBonus is < 0f or > 9f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.EquippedRidingSpeedBonus,
                "The equipped riding-speed bonus must be finite and between zero and nine.");
        }

        var packet = PlayerStatusUpdateTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerStatusUpdateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        PatchReferencePlayerPacket(packet, character, nameOffset: 8);
        // MSG_SYN_GAMEDATA copies wire offset 8 to GameData+0x25C.
        // Therefore wire 104/108 land on GameData+0x2BC/+0x2C0, the
        // native current-HP/current-MP fields. Keep these explicit here so a
        // wallet-only local status refresh cannot replay the captured
        // template fighter's vitals.
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusCurrentHpOffset, sizeof(int)),
            character.CurrentHp);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusCurrentMpOffset, sizeof(int)),
            character.CurrentMp);
        // MSG_SYN_GAMEDATA copies wire offset 8 to GameData+0x25C. Preserve
        // the current-map word at GameData+0x27E across status refreshes.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerStatusMapIdOffset, sizeof(ushort)),
            character.CurrentMap);
        // MSG_SYN_GAMEDATA copies this field into the local GameData movement
        // speed. Opcode 10167 carries the status/aura snapshot, but it does not
        // update the locomotion value used by client movement prediction.
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusMovementSpeedMultiplierOffset, 4),
            aggregate.MovementSpeedMultiplier);
        // Preserve the non-camp bytes in this native interaction-identity
        // dword. Byte 62 is the proven local/remote camp byte; projecting a
        // valid 0/1 keeps player hostility and NPC faction checks coherent.
        packet.AsSpan(PlayerStatusInteractionIdentityOffset, sizeof(uint)).Clear();
        packet[PlayerStatusCampOffset] = character.Camp;
        var stats = CharacterStats.FromCharacter(character);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusPhysicalDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.PhysicalDefense,
                aggregate.PhysicalDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusMagicDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.MagicDefense,
                aggregate.MagicDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusHitOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Hit,
                aggregate.Hit));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusDodgeOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Dodge,
                aggregate.Dodge));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusCriticalOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Critical,
                aggregate.CriticalAppend));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(
                PlayerStatusCriticalResistanceOffset,
                sizeof(int)),
            SaturatingStatusValue(
                stats.CriticalResistance,
                aggregate.CriticalResistance));
        var attackInterval = (ushort)Math.Clamp(
            stats.BasicAttackIntervalMilliseconds,
            1,
            ushort.MaxValue);
        // The stock handler copies the same authored interval from both wire
        // locations into native GameData. Neither field is an attack range.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerStatusAttackIntervalShortOffset, 2),
            attackInterval);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerStatusAttackIntervalDwordOffset, 4),
            attackInterval);
        var guildPanel = GuildPanelStatusCache.For(character.Id);
        // Both the local panel and TargetInfoWnd read the same object byte at
        // GameData+0x2A0. A remote status frame follows PlayerTitleInfo in the
        // visibility bundle, so it must preserve the duty instead of replacing
        // it with the template's zero.
        packet[PlayerStatusGuildDutyOffset] = guildPanel.Duty;
        if (objectId == LocalPlayerObjectId)
        {
            // Remaining contribution is private to the character's own panel;
            // only that local GameData block receives the contribution value.
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusGuildContributionOffset, 4),
                Math.Max(0, guildPanel.Contribution));
            // MSG_SYN_GAMEDATA copies wire offset 8 to GameData+0x25C.
            // Money/Stone/Honor/BindingGold at
            // +0x2CC/+0x2D0/+0x2D4/+0x2DC therefore map to physical wire
            // offsets 120/124/128/136.
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusSilverOffset, 4),
                Math.Max(0, character.Silver));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusGoldOffset, 4),
                Math.Max(0, character.Gold));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusMedusaHonorOffset, 4),
                Math.Max(0, character.MedusaHonorPoints));
            // The Point Exchanger's sibling balances sit in the same GameData
            // block as honor, so the panel only repaints them when this packet
            // carries them too.
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusExchangePointOffset, 4),
                Math.Max(0, character.ExchangePoint));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusBindingGoldOffset, 4),
                Math.Max(0, character.BindingGold));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusExchangeMedalOffset, 4),
                Math.Max(0, character.ExchangeMedal));
        }

        if (packet.Length >= PlayerStatusTalentPointsOffset + 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(PlayerStatusTalentPointsOffset, 4),
                character.TalentPoints);
        }

        return packet;
    }

    public static byte[] RemotePlayerStatusUpdate(
        GameCharacter character,
        uint objectId,
        ClientStatusAggregate aggregate,
        byte? pkMode,
        byte? viewerCamp = null) // ITEMS-HERMES: the receiving player's camp, for the Hermes Hood
    {
        if (objectId == LocalPlayerObjectId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectId),
                objectId,
                "A remote player status cannot use the local-player object id.");
        }

        if (pkMode is not (null or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pkMode),
                pkMode,
                "Only exact training-dummy PK mode 1 may override the captured default.");
        }

        var packet = PlayerStatusUpdate(character, objectId, aggregate);
        if (MaskedNames.IsMasked(character.Id)) // W1 Cypress Leave: other players read the name here too
        {
            PacketText.WriteFixedAscii(packet.AsSpan(8, 32), MaskedNames.MaskName);
        }
        if (pkMode.HasValue)
        {
            packet[PlayerStatusPkModeOffset] = pkMode.Value;
        }
        // ITEMS-HERMES (F-56): a hooded player reads as the viewer's own camp (green name).
        packet[PlayerStatusCampOffset] = HermesHoods.ShownCamp(character, viewerCamp);

        return packet;
    }
}
