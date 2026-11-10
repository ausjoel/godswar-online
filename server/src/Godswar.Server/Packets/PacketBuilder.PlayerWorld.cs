using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int PlayerWorldPkModeOffset = 0x50;
    private const int PlayerWorldMapIdOffset = 0xB0;

    public static byte[] EquipmentVisualRefresh(GameCharacter character)
    {
        return EquipmentVisualRefresh(
            character,
            LocalPlayerObjectId,
            fashionAppearances: null);
    }

    public static byte[] EquipmentVisualRefresh(GameCharacter character, uint objectId)
    {
        return EquipmentVisualRefresh(
            character,
            objectId,
            fashionAppearances: null);
    }

    public static byte[] EquipmentVisualRefresh(
        GameCharacter character,
        FashionAppearanceCatalog? fashionAppearances)
    {
        return EquipmentVisualRefresh(
            character,
            LocalPlayerObjectId,
            fashionAppearances);
    }

    public static byte[] EquipmentVisualRefresh(
        GameCharacter character,
        uint objectId,
        FashionAppearanceCatalog? fashionAppearances)
    {
        var packet = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D9);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Captured 0x27D9 packets carry the avatar hair/model byte followed by
        // the one-based gender, not a constant hair id and profession.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)character.Gender + 1u);

        var equipment = ParseEquipment(character);
        var stylish = EquipmentSlots.GetItem(
            EquipmentFor(character),
            character.Profession,
            EquipmentSlots.Stylish);
        if (!character.FashionHidden &&
            !stylish.IsEmpty &&
            fashionAppearances is not null &&
            fashionAppearances.TryGet(stylish.Id, out var fashion))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(8, 4),
                fashion.ResolveHair(character.Hair));
            for (var index = 0; index < FashionAppearanceCatalog.PartCount; index++)
            {
                var itemId = fashion.PartIds[index];
                // Native 0x27D9 indices 10 and 11 are the separately rendered
                // held weapon and shield. Stock fashion definitions leave both
                // at zero, so retain the authoritative equipped held items while
                // letting the fashion definition exclusively own body indices.
                if (index is EquipmentSlots.Weapon or EquipmentSlots.Shield)
                {
                    itemId = index < equipment.Length ? equipment[index].Id : 0;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(
                    packet.AsSpan(16 + (index * sizeof(uint)), sizeof(uint)),
                    itemId);
            }

            return packet;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), character.Hair);
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Shield; slot++)
        {
            var itemId = slot < equipment.Length ? equipment[slot].Id : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16 + (slot * 4), 4), itemId);
        }

        return packet;
    }

    /// <summary>ITEMS-HERMES: a copy of a 0x2725 spawn frame with another camp in byte 53 (the shared frame is untouched).</summary>
    public static byte[] WithPlayerWorldSpawnCamp(byte[] spawnPacket, byte camp)
    {
        if (spawnPacket.Length <= 53 ||
            BinaryPrimitives.ReadUInt16LittleEndian(spawnPacket.AsSpan(2, 2)) != PlayerWorldSpawnOpcode)
        {
            throw new ArgumentException("Not a player-spawn frame.", nameof(spawnPacket));
        }

        var copy = spawnPacket.ToArray();
        copy[53] = camp;
        return copy;
    }

    public static byte[] PlayerWorldSpawn(
        GameCharacter character,
        uint objectId,
        IReadOnlyList<ClientStatusEffect>? effects = null,
        byte? pkMode = null,
        byte? viewerCamp = null) // ITEMS-HERMES: the receiving player's camp, for the Hermes Hood
    {
        effects ??= [];
        if (effects.Count > PlayerWorldStatusMaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effects),
                effects.Count,
                $"The player-spawn packet supports at most {PlayerWorldStatusMaximumCount} statuses.");
        }

        var packet = new byte[PlayerWorldExtendedLength];
        PlayerWorldSpawnTemplate.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerWorldSpawnOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)Math.Max(character.Id, 0));
        PacketText.WriteFixedAscii(packet.AsSpan(12, 32), MaskedNames.DisplayName(character)); // W1 Cypress Leave
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(44, 4), Math.Max(1, character.CurrentHp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48, 4), Math.Max(1, character.EffectiveMaxHp));
        packet[52] = character.Gender;
        packet[53] = HermesHoods.ShownCamp(character, viewerCamp); // ITEMS-HERMES (F-56)
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(54, 2),
            (ushort)Math.Clamp(character.Level, 1, ushort.MaxValue));
        packet[56] = character.Face;
        packet[58] = ToWorldProfessionByte(character.Profession);
        packet[59] = character.Hair;
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(60, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(64, 4), character.PositionZ);
        // The third captured coordinate is terrain height. It is not persisted by
        // GameCharacter, so use the neutral value rather than shifting Z into it.
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(68, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(72, 4), 1f);
        if (pkMode.HasValue)
        {
            // Native 0x2725 decoding reads this unsigned byte into the remote
            // player's PK-mode field. Retain the captured template value when
            // callers do not explicitly project a mode.
            packet[PlayerWorldPkModeOffset] = pkMode.Value;
        }

        // Native 0x2725 rejects a remote player when this map differs from the
        // local player's current map. The captured template came from map 0,
        // so instance appearances must replace it with the authoritative map.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerWorldMapIdOffset, sizeof(ushort)),
            character.CurrentMap);

        PatchPlayerWorldAppearance(packet, character);
        packet.AsSpan(
            PlayerWorldStatusIdsOffset,
            PlayerWorldNativeLength - PlayerWorldStatusIdsOffset).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(PlayerWorldStatusCountOffset, sizeof(ushort)),
            checked((ushort)effects.Count));
        for (var index = 0; index < effects.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(
                    PlayerWorldStatusIdsOffset + (index * sizeof(uint)),
                    sizeof(uint)),
                effects[index].StatusId);
        }

        return packet;
    }

    public static byte[] PlayerAppearanceExtras(GameCharacter character, uint objectId)
    {
        // This legacy call site has no authoritative pet snapshot. A zero pet
        // ID makes the native handler ignore the packet for remote players.
        return BuildPetWorldPresence(petId: 0, objectId);
    }

    public static byte[] PetWorldPresence(
        uint petId,
        uint ownerObjectId)
    {
        if (petId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(petId),
                petId,
                "A visible pet must have a non-zero native ID.");
        }

        return BuildPetWorldPresence(petId, ownerObjectId);
    }

    public static byte[] PetWorldPresence(
        PetBootstrapSnapshot pet,
        uint ownerObjectId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (!pet.IsCarried || !pet.IsSummoned || pet.ContributesToCharacter)
        {
            throw new ArgumentException(
                "Only a separately summoned pet has a world presence.",
                nameof(pet));
        }

        var packet = BuildPetWorldPresence(
            checked((uint)pet.PetId),
            ownerObjectId);
        packet[12] = checked((byte)pet.SpeciesId);
        packet[13] = pet.Sex;
        packet[14] = checked((byte)pet.Level);
        PacketText.WriteFixedAscii(packet.AsSpan(32, 32), pet.Name);
        return packet;
    }

    private static byte[] BuildPetWorldPresence(
        uint petId,
        uint ownerObjectId)
    {
        var packet = new byte[108];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2808);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), petId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            ownerObjectId);
        // Captured 10248 remote-presence packets put species, sex, level, and
        // the fixed pet name at +12, +13, +14, and +32 respectively. Unmodeled
        // bytes retain the neutral baseline; +64 is the captured presence flag.
        packet[64] = 1;
        return packet;
    }

    public static byte[] PlayerTitleInfo(GameCharacter character, uint objectId)
    {
        var packet = new byte[80];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        var guildPanel = GuildPanelStatusCache.For(character.Id);
        // The client's case for this opcode (0x4ecc94, "_MSG_CD_INFO") resolves the
        // object id at body+0 to a player object and then copies, from the body:
        //   body+0x04 (64 bytes) -> object+0xB35, the guild name the target window
        //                           reads for its Union row;
        //   body+0x44 (byte)     -> object+0xA34, the "this player is in a guild"
        //                           flag;
        //   body+0x45 (byte)     -> object+0x9A0;
        //   body+0x46 (byte)     -> object+0x2A0, the duty the target window prints
        //                           as "G%d";
        //   body+0x48 (dword)    -> object+0x99C, the selected title id.
        // The flag is not optional: the target window's fill code starts with
        // `cmp byte [edi+0xa34], 0 ; je <clear both rows>` (0x62c432), so without it
        // the guild name and the duty are both erased again - measured 2026-10-02,
        // when the name and duty travelled but the two rows stayed empty.
        var hasGuild = guildPanel.Duty != 0 ||
            !string.IsNullOrEmpty(guildPanel.GuildName);
        PacketText.WriteFixedAscii(packet.AsSpan(8, 64), guildPanel.GuildName);
        packet[72] = hasGuild ? (byte)1 : (byte)0;
        packet[74] = guildPanel.Duty;
        // The selected title ID is the final dword in this 80-byte packet.
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(76, sizeof(uint)),
            character.SelectedTitleId);
        return packet;
    }

    public static byte[] PlayerWorldMovement(ReadOnlySpan<byte> clientWalkPacket, uint objectId)
    {
        var packet = clientWalkPacket.ToArray();
        if (packet.Length < 8)
        {
            return packet;
        }

        var clientMovementState = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var serverMovementState = (clientMovementState & 0xFFFF0000) | (objectId & 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), serverMovementState);
        return packet;
    }

    public static byte[] PlayerWorldPosition(GameCharacter character, uint objectId)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00020000 | (objectId & 0xFFFF));
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), 1f);
        return packet;
    }

    public static byte[] RemoveWorldObjects(params uint[] objectIds)
    {
        var packet = new byte[8 + (Math.Max(0, objectIds.Length) * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), WorldObjectRemoveOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)objectIds.Length);

        for (var i = 0; i < objectIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + (i * 4), 4), objectIds[i]);
        }

        return packet;
    }

    private static void PatchPlayerWorldAppearance(byte[] packet, GameCharacter character)
    {
        packet.AsSpan(PlayerWorldVisualFlagsOffset, PlayerWorldVisualFlagsLength).Clear();
        packet.AsSpan(PlayerWorldAttributeCountsOffset, PlayerWorldAttributeCountsLength).Clear();
        packet.AsSpan(PlayerWorldEquipmentIdsOffset, PlayerWorldEquipmentIdsLength * 2).Clear();
        packet.AsSpan(PlayerWorldFullVisualQualityOffset, PlayerWorldEquipmentIdsLength).Clear();
        packet.AsSpan(PlayerWorldFullVisualGradeOffset, PlayerWorldEquipmentIdsLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerWorldFullVisualMarkerOffset, sizeof(uint)),
            PlayerWorldFullVisualMarker);

        var visualIndex = 0;
        var equipmentMask = 0u;
        foreach (var (slot, item) in PlayerWorldEquipmentItems(character))
        {
            if (item.IsEmpty || visualIndex >= PlayerWorldEquipmentIdsLength)
            {
                continue;
            }

            if (slot < sizeof(uint) * 8)
            {
                equipmentMask |= 1u << slot;
            }

            packet[PlayerWorldVisualFlagsOffset + visualIndex] = PackWorldItemVisual(item);
            packet[PlayerWorldFullVisualQualityOffset + visualIndex] =
                (byte)Math.Clamp(item.Quality, (short)0, (short)byte.MaxValue);
            packet[PlayerWorldFullVisualGradeOffset + visualIndex] =
                (byte)Math.Clamp(item.Grade, (short)0, (short)byte.MaxValue);
            if (visualIndex < PlayerWorldAttributeCountsLength)
            {
                packet[PlayerWorldAttributeCountsOffset + visualIndex] = WorldItemAttributeCount(item);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(PlayerWorldEquipmentIdsOffset + (visualIndex * 2), 2),
                (ushort)Math.Min(item.Id, ushort.MaxValue));
            visualIndex++;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerWorldEquipmentMaskOffset, sizeof(uint)),
            equipmentMask);
    }

    private static byte PackWorldItemVisual(CompactItemEntry item)
    {
        // Captures pair each equipment id with (grade << 4) | quality. The
        // packed quality nibble safely carries the supported Q13 forge ceiling;
        // the GWX1 tail still carries the uncapped full-byte visual values.
        var grade = (int)Math.Clamp(item.Grade, (short)0, CapturedWorldVisualGradeCap);
        var quality = (int)Math.Clamp(item.Quality, (short)0, CapturedWorldVisualQualityCap);
        return (byte)((grade << 4) | quality);
    }

    private static byte WorldItemAttributeCount(CompactItemEntry item)
    {
        var count = 0;
        count += HasWorldItemAttribute(item.Attribute1) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute2) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute3) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute4) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute5) ? 1 : 0;
        count += HasWorldItemAttribute(item.ClassAttribute1) ? 1 : 0;
        count += HasWorldItemAttribute(item.ElementalAttribute1) ? 1 : 0;
        count += HasWorldItemAttribute(item.ElementalAttribute2) ? 1 : 0;
        return (byte)count;
    }

    private static bool HasWorldItemAttribute(int? attribute)
    {
        // Captured item records use -1 as the absent sentinel; compact records use null.
        return attribute is >= 0;
    }
}
