using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static void CheckWonderlandNativeLootWire()
    {
        // Origin5CA9AB/5CA9B2 and main dispatch table prove asymmetric10050.
        // Undefined native bytes deliberately contain nonzero data in this fixture.
        Check.True(WonderlandBossPickupPacket(46100).Buffer.SequenceEqual(Convert.FromHexString(
                "1400422714B4000077EE017700000000000C0000")) &&
            PacketBuilder.WonderlandBossLootPickupAck(0x1448, 46100, 0).SequenceEqual(Convert.FromHexString(
                "100042274814000014B4000000000000")),
            "native loot uses a20-byte10050 request and16-byte10050 reply, with distinct field layouts");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.MonsterLoot(46100,
            Enumerable.Range(0, 9).Select(index => new Godswar.Server.Game.MonsterLootEntry(index, index, 4450, 1)).ToArray()),
            "native eight-slot loot buffer cannot receive a ninth item");
    }

    private static void AssertWonderlandNativeLootProjection(IReadOnlyList<byte[]> packets, uint bossId,
        GameCharacter character)
    {
        var all = packets.ToArray();
        var localAck = PacketBuilder.WonderlandBossLootPickupAck(0x1448, bossId, 0);
        var firstBagFrame = PacketBuilder.KitBagDetailPages(character).First();
        var authoritativeBag = Array.FindIndex(all, packet => packet.SequenceEqual(firstBagFrame));
        Check.True(authoritativeBag >= 0 && !all.Any(packet => packet.SequenceEqual(localAck)),
            "durable pickup and AlreadyClaimed replay never emit the untracked native corpse-to-bag insertion");
        var neutral = Array.FindIndex(all, packet => packet.SequenceEqual(
            PacketBuilder.WonderlandBossLootPickupAck(0, bossId, 0)));
        Check.True(neutral > authoritativeBag && all[neutral - 1].Length == 84 &&
            ReadOpcode(all[neutral - 1]) == Opcodes.MonsterDrops &&
            BinaryPrimitives.ReadUInt32LittleEndian(all[neutral - 1].AsSpan(4)) == bossId &&
            BinaryPrimitives.ReadUInt32LittleEndian(all[neutral - 1].AsSpan(8)) == 1,
            "a known one-slot presentation precedes the bag-neutral ACK so full bags and replay clear UI without count underflow");
        Check.True(all.All(packet => ReadOpcode(packet) != Opcodes.PickupDrops),
            "Wonderland never emits the ignored10048 acknowledgment");
        CheckWonderlandNativeBagConvergence(all, bossId, character);
    }
}
