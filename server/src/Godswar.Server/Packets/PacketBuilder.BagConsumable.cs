using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>
    /// The client's bag activation reply (opcode 10051), captured as
    /// <c>S2C 10051</c> 92-byte frames against the reference server:
    /// <c>5c004327 d5030000 00000000 0000 0600 0000 97110000 ffffffff ffffffff
    /// ffffffff ffffffff ffffffff 01010000 …</c> (capture id 183610: player 981,
    /// bag page 0 index 6, item 4503) and <c>5c004327 25020000 00000000
    /// 0000 0300 0000 36100000 …</c> (capture id 145494: player 549, page 0
    /// index 3, item 4150).
    /// </summary>
    /// <remarks>
    /// The body is the shared 92-byte item snapshot: player object ID at +4,
    /// zero at +8, the packed page at +12 and the index inside it at +14, and
    /// the 72-byte native item record at +20. Both captured examples carry
    /// bound/stack <c>01 01</c> at record offset 24 because both items are a
    /// single bound unit, so the frame is the addressed item either way; a
    /// consumed stack is announced by the following kit-bag refresh.
    /// </remarks>
    public static byte[] BagConsumableActivation(
        GameCharacter character,
        int bagSlot,
        uint objectId)
    {
        return KitBagItemSnapshot(character, bagSlot, objectId);
    }

    public static byte[] BagConsumableActivation(string kitBag, int bagSlot, uint objectId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bagSlot, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bagSlot, 96);
        var packet = EquipmentItemSnapshot(bagSlot, KitBagSlots.GetItem(kitBag, bagSlot), objectId);
        WriteSnapshotBagPosition(packet, bagSlot);
        return packet;
    }

    /// <summary>
    /// The money-bag silver grant (opcode 10356). Captured as
    /// <c>S2C 10356</c> 16 bytes, id 145492:
    /// <c>10007428 19000000 25020000 10270000</c> = {25, player 549, 10000},
    /// where the amount is item skill 4600's Magic.ini <c>Power2</c>.
    /// </summary>
    /// <remarks>
    /// The leading 25 is the captured grant kind for a silver bag. Out of scope
    /// for this change is the second observed kind, 49, which the Talent Stone
    /// (skill 4630) uses.
    /// </remarks>
    public static byte[] BagSilverGrant(uint objectId, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.BagSilverGrant);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 25);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, 4),
            amount);
        return packet;
    }

    /// <summary>
    /// The skill-cast visual for a used bag consumable (opcode 10040).
    /// Captured 40-byte <c>S2C 10040</c> frames: id 145490
    /// (<c>28003827 25020000 f8110000 00000000 25020000 0a000000 …</c>,
    /// player 549 casting item skill 4600 with itself as the target) and id
    /// 183605 (player 981 casting 4809). This is the already-proven 40-byte
    /// layout emitted by <see cref="MonsterSkillCastVisual"/>; the consumable
    /// case simply casts on the using player.
    /// </summary>
    public static byte[] BagConsumableSkillCastVisual(
        uint casterObjectId,
        uint targetObjectId,
        uint skillId,
        float casterX,
        float casterZ,
        float targetX,
        float targetZ)
    {
        return MonsterSkillCastVisual(
            casterObjectId,
            targetObjectId,
            skillId,
            casterX,
            casterZ,
            targetX,
            targetZ);
    }

    /// <summary>
    /// The captured order of one bag-consumable activation, with the frames
    /// this server has and has not verified marked on each entry.
    /// </summary>
    public static IReadOnlyList<BagConsumableFrame>
        BagConsumableActivationFrames(
            GameCharacter character,
            BagConsumableEvidence evidence,
            uint objectId,
            float positionX,
            float positionZ)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(evidence);
        var frames = new List<BagConsumableFrame>(4)
        {
            new(
                "SkillCastVisual",
                BagConsumableSkillCastVisual(
                    objectId,
                    objectId,
                    checked((uint)evidence.SkillId),
                    positionX,
                    positionZ,
                    positionX,
                    positionZ),
                IsCaptureVerified: true)
        };
        if (evidence.EffectKind == BagConsumableEffectKind.GrantSilver)
        {
            frames.Add(new(
                "SilverGrant",
                BagSilverGrant(objectId, evidence.Amount),
                IsCaptureVerified: true));
        }
        else if (evidence.EffectKind is
                     BagConsumableEffectKind.RestoreHitPoints or
                     BagConsumableEffectKind.RestoreManaPoints)
        {
            frames.Add(new(
                "VitalsUpdate",
                PlayerVitalsUpdate(
                    objectId,
                    evidence.CurrentHp,
                    evidence.CurrentMp),
                // No potion use exists in the reference capture, so the HP/MP
                // effect frame rests on this server's own proven layout.
                IsCaptureVerified: false));
        }

        frames.Add(new(
            "SkillCastImpact",
            SkillCastImpact(
                objectId,
                objectId,
                checked((uint)evidence.SkillId),
                positionX,
                positionZ),
            IsCaptureVerified: true));
        frames.Add(new(
            "BagItemSnapshot",
            BagConsumableActivation(character, evidence.KitBagSlot, objectId),
            IsCaptureVerified: true));
        return frames;
    }
}

/// <summary>
/// One frame of the bag-consumable activation sequence. Frames whose layout
/// came from the reference-server capture are marked verified; the HP/MP
/// effect frame is this server's own proven vitals update and is not.
/// </summary>
internal readonly record struct BagConsumableFrame(
    string Name,
    byte[] Packet,
    bool IsCaptureVerified);
