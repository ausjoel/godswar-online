using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandAcquisitionCheckName =
        "Wonderland daily item notifications preserve full and newer bags without optimistic ghosts";

    public static Task RunWonderlandAcquisitionsAsync()
    {
        var full = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
            full = KitBagSlots.SetSlot(full, slot, (CompactItemEntry.Empty with
                { Id = 1001, Quality = 1, Grade = 1, Stack = 1, Bound = 1 }).ToCompactString());
        // The current snapshot has already consumed the receipt's sack. It is
        // still a fresh claim notification, but must never recreate that item.
        CheckWonderlandAcquisitionProjection(full, full, [new(4450, 1, 1)], [new(4450, 1, 1)]);

        var before = KitBagSlots.SetSlot(full, 25, (CompactItemEntry.Empty with
            { Id = 4450, Quality = 1, Grade = 1, Stack = 1, Bound = 1 }).ToCompactString());
        var after = KitBagSlots.SetSlot(before, 25, CompactItemEntry.Empty.ToCompactString());
        after = KitBagSlots.SetSlot(after, 26, (CompactItemEntry.Empty with
            { Id = 10133, Quality = 1, Grade = 1, Stack = 7, Bound = 1 }).ToCompactString());
        CheckWonderlandAcquisitionProjection(before, after, [new(10133, 25, 1)], [new(10133, 25, 1)]);
        CheckWonderlandAcquisitionProjection(before, after, [], []);
        CheckWonderlandAcquisitionProjection(full, full, [new(10133, 125, 1)],
            [new(10133, 99, 1), new(10133, 26, 1)]);
        CheckWonderlandNativeStackMerge(full);
        return Task.CompletedTask;
    }

    // Item sweep 2026-11-04: a 40-reward gift pack (Newbie Gift Bag 3884 style) used to exceed the 128-packet egress
    // queue in one exact batch and disconnect the player. The chunks must each fit, keep notice/clear pairs together,
    // keep the final hydration in one chunk, and replay to the same native bag as the unsplit projection.
    public const string WonderlandProjectionChunksCheckName =
        "Gift pack and Wonderland reward projections fit the reliable egress queue in chunks (item sweep 2026-11-04)";

    public static Task RunWonderlandProjectionChunksAsync()
    {
        CheckWonderlandProjectionChunks();
        return Task.CompletedTask;
    }

    private static void CheckWonderlandProjectionChunks()
    {
        var before = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 30, (CompactItemEntry.Empty with
            { Id = 3884, Quality = 1, Grade = 1, Stack = 1, Bound = 1 }).ToCompactString());
        var after = GameDefaults.EmptyKitBag;
        var rewards = Enumerable.Range(0, 40).Select(i => new WonderlandChestItemReward((uint)(2000 + i), 1, 1)).ToArray();
        for (var i = 0; i < rewards.Length; i++)
            after = KitBagSlots.SetSlot(after, i, (CompactItemEntry.Empty with
                { Id = rewards[i].ItemId, Quality = 1, Grade = 1, Stack = 1, Bound = 1 }).ToCompactString());
        var character = new GameCharacter { KitBag = after };
        var packets = GameClientHandler.BuildWonderlandRewardProjection(character, before, rewards);
        var tail = PacketBuilder.KitBagDetailPages(character).Length + PacketBuilder.KitBagSlotIndexes(character).Length;
        Check.True(packets.Count > 128, "a 40-reward projection is larger than the 128-packet reliable egress queue");
        var chunks = GameClientHandler.ChunkWonderlandRewardProjection(packets, 112, tail);
        Check.True(chunks.Count > 1 && chunks.All(chunk => chunk.Count <= 113),
            "every admitted chunk fits the egress queue (limit 112, plus at most one leading slot0 clear)");
        var clear = PacketBuilder.StorageItemKitBagDelete(0);
        Check.True(chunks.All(chunk => Enumerable.Range(0, chunk.Count).Where(i => ReadOpcode(chunk[i]) == 10185)
                .All(i => i > 0 && i + 1 < chunk.Count && chunk[i - 1].SequenceEqual(clear) && chunk[i + 1].SequenceEqual(clear))),
            "each native10185 notice keeps a cleared slot0 on both sides inside its own chunk");
        Check.True(chunks[^1].Count == tail && chunks[^1].SequenceEqual(packets.Skip(packets.Count - tail)),
            "the final bag hydration (detail pages and slot indexes) is admitted as one chunk");
        var flat = chunks.SelectMany(chunk => chunk).ToList();
        var model = new WonderlandNativeBagModel(0);
        var unsplit = new WonderlandNativeBagModel(0);
        foreach (var packet in PacketBuilder.KitBagDetailPages(new GameCharacter { KitBag = before }))
        {
            model.Receive(packet);
            unsplit.Receive(packet);
        }
        foreach (var packet in flat) model.Receive(packet);
        foreach (var packet in packets) unsplit.Receive(packet);
        Check.True(model.Acquisitions.SequenceEqual(unsplit.Acquisitions) && model.Slots.Count == unsplit.Slots.Count &&
            unsplit.Slots.All(pair => model.Slots.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "the chunked projection shows the same notices and converges to the same bag as the unsplit one");
        Check.True(GameClientHandler.ChunkWonderlandRewardProjection(packets.Take(100).ToArray(), 112, tail).Count == 1,
            "a projection that fits is still admitted as a single exact batch");
    }

    private static void CheckWonderlandNativeStackMerge(string full)
    {
        var bag = full;
        foreach (var (slot, stack, bound) in new[] { (1, 95, 1), (2, 99, 1), (3, 10, 0), (24, 5, 1) })
            bag = KitBagSlots.SetSlot(bag, slot, (CompactItemEntry.Empty with
                { Id = 10133, Quality = 1, Grade = 1, Stack = (short)stack, Bound = (short)bound }).ToCompactString());
        var character = new GameCharacter { KitBag = bag };
        var packets = GameClientHandler.BuildWonderlandRewardProjection(character, bag, [new(10133, 25, 1)]);
        var model = new WonderlandNativeBagModel(0);
        foreach (var packet in PacketBuilder.KitBagDetailPages(character)) model.Receive(packet);
        foreach (var packet in packets)
        {
            model.Receive(packet);
            if (ReadOpcode(packet) != 10185) continue;
            Check.True(model.Slots[1].Stack == 99 && model.Slots[2].Stack == 99 &&
                model.Slots[3] == new NativeLootItem(10133, 10, 0) &&
                model.Slots[24].Stack == 5 && model.Slots[0] == new NativeLootItem(10133, 21, 1),
                "native acquisition merges four into the eligible page0 stack, preserves full/mismatched/page1 stacks, and inserts21 in slot0");
            Check.True(model.Acquisitions.Single() == new NativeLootItem(10133, 25, 1),
                "native YouObtainItem reads the original25-item record, not its mutated21-item remainder");
        }
        Check.True(model.Slots[0] == new NativeLootItem(1001, 1, 1) && model.Slots[1].Stack == 95 &&
            model.Slots[2].Stack == 99 && model.Slots[3].Stack == 10 && model.Slots[24].Stack == 5,
            "authoritative projection restores the untouched eligible stack as well as the scratch slot after a native merge");
        CheckWonderlandAcquisitionProjection(bag, bag, [new(10133, 25, 1)], [new(10133, 25, 1)]);
    }

    private static void CheckWonderlandAcquisitionProjection(string before, string after,
        IReadOnlyList<WonderlandChestItemReward> rewards, IReadOnlyList<NativeLootItem> expectedNotices)
    {
        var character = new GameCharacter { KitBag = after };
        var packets = GameClientHandler.BuildWonderlandRewardProjection(character, before, rewards);
        var model = new WonderlandNativeBagModel(0);
        foreach (var packet in PacketBuilder.KitBagDetailPages(new GameCharacter { KitBag = before })) model.Receive(packet);
        foreach (var packet in packets) model.Receive(packet);
        Check.True(model.Acquisitions.SequenceEqual(expectedNotices),
            "the native daily route receives receipt quantities, including safe signed-byte stack splits");
        var expected = new WonderlandNativeBagModel(0);
        foreach (var packet in PacketBuilder.KitBagDetailPages(character)) expected.Receive(packet);
        Check.True(model.Slots.Count == expected.Slots.Count && expected.Slots.All(pair =>
                model.Slots.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "full bags, empty consumed source slots, and newer reward stacks converge exactly to authoritative inventory");
        Check.True(packets.All(packet => ReadOpcode(packet) is not
                (Opcodes.PythonNote or Opcodes.ServerNote or Opcodes.NpcFunctionActionResponse or Opcodes.MoveItem)),
            "uniform reward notifications add no alternate chat line, dialogue, or optimistic corpse ACK");
        var firstBag = packets.ToList().FindIndex(packet => ReadOpcode(packet) == 10033);
        var acquisitions = packets.Select((packet, index) => (packet, index))
            .Where(entry => ReadOpcode(entry.packet) == 10185).ToArray();
        var clear = Convert.FromHexString("100044274814000000000000FFFFFFFF");
        foreach (var (packet, index) in acquisitions)
        {
            Check.True(packet.Length == 80 && packet.AsSpan(0, 8)
                    .SequenceEqual(Convert.FromHexString("5000C92700000000")) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(76)) == 5192 &&
                packets[index - 1].SequenceEqual(clear) && packets[index + 1].SequenceEqual(clear) &&
                index + 1 < firstBag,
                "native10185 uses its proven80-byte header/item owner and a cleared slot0 on both sides before hydration");
        }
        foreach (var packet in PacketBuilder.KitBagDetailPages(character).Concat(PacketBuilder.KitBagSlotIndexes(character)))
            Check.Equal(1, packets.Count(candidate => candidate.SequenceEqual(packet)),
                "each authoritative bag frame follows transient acquisition cleanup exactly once");
    }
}
