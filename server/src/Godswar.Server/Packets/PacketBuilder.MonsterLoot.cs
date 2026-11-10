using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int MonsterLootHeaderLength = 12;
    private const int MonsterLootItemLength = 72;

    /// <summary>
    /// Advertises a corpse's drops when no death event carries the ground-item
    /// identity, which is how the Wonderland boss corpse presents its sack.
    /// </summary>
    public static byte[] MonsterLoot(
        uint monsterObjectId,
        IReadOnlyList<MonsterLootEntry> entries) =>
        MonsterLoot(monsterObjectId, Guid.Empty, entries);

    public static byte[] MonsterLoot(
        uint monsterObjectId,
        Guid deathEventId,
        IReadOnlyList<MonsterLootEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // F-166 / LOOT PAGING: HARD GUARD, whatever the rates say. The client's 10029 parser copies count x 72 bytes
        // into an 8-record stack buffer with no clamp, so a ninth record corrupts it. Paged corpses send <= 8 per page.
        if (monsterObjectId == 0 ||
            entries.Count > Godswar.Server.Application.World.Content.CorpseLootLimit.ClientSlots ||
            entries.Any(static entry =>
                entry.ItemId == 0 || entry.Quantity is < 1 or > 255))
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        var packet = new byte[
            MonsterLootHeaderLength + entries.Count * MonsterLootItemLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.MonsterDrops);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            monsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            checked((uint)entries.Count));

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var item = packet.AsSpan(
                MonsterLootHeaderLength + index * MonsterLootItemLength,
                MonsterLootItemLength);
            BinaryPrimitives.WriteUInt32LittleEndian(item, entry.ItemId);
            for (var sentinel = 1; sentinel <= 5; sentinel++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    item.Slice(sentinel * sizeof(uint)),
                    uint.MaxValue);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                item.Slice(24),
                checked(((uint)entry.Quantity << 24) | 0x0000_0101u));
            // Captured ground-item identity at +64/+68. The client echoes both
            // halves back with its pickup request reply so it can clear the
            // matching ground item.
            var groundKey = MonsterLootGroundKey.Resolve(
                deathEventId,
                entry.RuleLootIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(
                item.Slice(64),
                groundKey.High);
            BinaryPrimitives.WriteUInt32LittleEndian(
                item.Slice(68),
                groundKey.Low);
        }
        return packet;
    }

    /// <summary>
    /// Answers the client's corpse click. Captured 2026-09-15 on the reference
    /// server: `C2S 10050` (20 bytes) `{corpseObjectId, 0, dropIndex, 0}` is
    /// answered by `S2C 10050` (16 bytes)
    /// `{playerObjectId, corpseObjectId, dropIndex}`, and only then does the
    /// client request that drop with 10114 plus 10056.
    /// </summary>
    /// <remarks>
    /// The third dword is the index of the drop record inside the opcode-10029
    /// list, and the reply has to echo the index the client asked for: three
    /// captured clicks (01:37:26.770 index 1, 01:37:27.540 index 0,
    /// 02:08:16.835 index 0) each answered with the same index, and each one was
    /// followed 5 ms later by the pickup that named exactly that record's item
    /// and ground key. A reply that always carries 1 leaves a single-drop corpse
    /// with no matching ground item, which is why a click on such a corpse
    /// produced no pickup request at all.
    /// </remarks>
    public static byte[] MonsterLootSourceOpen(
        uint playerObjectId,
        uint monsterObjectId,
        int dropIndex)
    {
        if (playerObjectId == 0 || monsterObjectId == 0 ||
            dropIndex is < 0 or >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(dropIndex));
        }

        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.MoveItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            monsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            checked((uint)dropIndex));
        return packet;
    }

    public static byte[] MonsterLootPickup(
        uint playerObjectId,
        uint monsterObjectId,
        int pickupIndex)
    {
        if (playerObjectId == 0 || monsterObjectId == 0 ||
            pickupIndex is < 0 or >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(pickupIndex));
        }

        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PickupDrops);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            monsterObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            pickupIndex);
        return packet;
    }
}
