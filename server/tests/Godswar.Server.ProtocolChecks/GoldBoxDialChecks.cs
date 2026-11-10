using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class GoldBoxDialChecks
{
    public const string CheckName = "Box dial packets match the client layout and the ring lands on the committed reward";

    public static Task RunAsync()
    {
        var box = CompactItemEntry.Empty with { Id = 4405, Quality = 1, Grade = 1, Stack = 3 };
        var pack = new GiftPackDefinition(0, [], [new(2, [
            new(100, 1, ClassMask: 1), new(101, 2, Weight: 5), new(102, 1), new(103, 1, MaleItemId: 203),
            new(104, 1), new(105, 1)])], KeyItemId: 4421);
        var rewards = new List<(uint ItemId, int Quantity, short Bound)> { (102, 1, 0), (101, 2, 1) };
        for (var seed = 0; seed < 50; seed++)
        {
            var (items, target) = GoldBoxDialPlan.Build(pack, rewards, box, profession: 2, male: true,
                id => id != 105, new Random(seed));
            Check.Equal(17, items.Length, "seventeen dial items");
            Check.True(target is >= 0 and < 16, "target on the ring");
            Check.Equal(102u, items[target].Id, "ring stops on the first reward");
            Check.True(items.Take(16).Any(item => item.Id == 101 && item.Stack == 2 && item.Bound == 1), "second reward shown");
            Check.Equal(4405u, items[16].Id, "centre is the box");
            Check.True(items.All(item => !item.IsEmpty && item.Id != 105 && item.Id != 100 && item.Id != 103),
                "no unknown, other-class or other-sex items");

            var inDial = PacketBuilder.GoldBoxInDial(40, items);
            Check.Equal(1240, inDial.Length, "InDial length");
            Check.Equal((ushort)1240, BinaryPrimitives.ReadUInt16LittleEndian(inDial), "InDial size field");
            Check.Equal((ushort)10121, BinaryPrimitives.ReadUInt16LittleEndian(inDial.AsSpan(2)), "InDial opcode");
            Check.Equal((byte)40, inDial[4], "InDial reward slot");
            Check.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(inDial.AsSpan(8)), "no extra-draw item");
            Check.Equal(102u, BinaryPrimitives.ReadUInt32LittleEndian(inDial.AsSpan(16 + target * 72)), "target record");
            Check.Equal(4405u, BinaryPrimitives.ReadUInt32LittleEndian(inDial.AsSpan(16 + 16 * 72)), "centre record");
        }

        var spin = PacketBuilder.GoldBoxReturn(PacketBuilder.GoldBoxReturnSpin, 9, 40);
        Check.Equal(76, spin.Length, "Return length");
        Check.Equal((ushort)10120, BinaryPrimitives.ReadUInt16LittleEndian(spin.AsSpan(2)), "Return opcode");
        Check.Equal((byte)3, spin[4], "spin info");
        Check.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(spin.AsSpan(8)), "spin target");
        Check.Equal((byte)40, spin[12], "spin slot");
        Check.True(Throws(() => PacketBuilder.GoldBoxReturn(5, 0, 0)), "centre-item reply is never built");
        Check.True(Throws(() => PacketBuilder.GoldBoxReturn(3, 16, 0)), "target past the ring refused");
        Check.True(Throws(() => PacketBuilder.GoldBoxInDial(96, PacketBuilder.GoldBoxDialItemCount == 17 ?
            Enumerable.Repeat(box, 17).ToArray() : [])), "reward slot past the bag refused");

        Check.Equal(2, GoldBoxDialPlan.RewardSlot("", 2, box with { Stack = 1 }), "last box: its own slot");
        Check.Equal(0, GoldBoxDialPlan.RewardSlot("", 2, box), "stack of boxes: first empty slot");
        Check.True(GoldBoxDialPlan.IsEnabledFor("all", 8) && GoldBoxDialPlan.IsEnabledFor("3, 8", 8) &&
            !GoldBoxDialPlan.IsEnabledFor("3", 8) && !GoldBoxDialPlan.IsEnabledFor(null, 8), "account gate");
        return Task.CompletedTask;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
