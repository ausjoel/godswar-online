using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static void CheckWonderlandNativeBagConvergence(IReadOnlyList<byte[]> packets,
        uint bossId, GameCharacter character)
    {
        var authoritative = new WonderlandNativeBagModel(bossId);
        foreach (var page in PacketBuilder.KitBagDetailPages(character)) authoritative.Receive(page);
        var reward = KitBagSlots.GetItem(character.KitBag, 11);
        var expected = authoritative.Quantity(reward.Id);
        var layouts = Enumerable.Range(0, 96).Where(slot => !authoritative.Slots.ContainsKey(slot))
            .GroupBy(slot => slot / 24).Select(page => page.Last()).ToArray();
        Check.True(layouts.Length >= 3, "the native regression exercises distinct free slots across bag pages");
        foreach (var firstFree in layouts)
        {
            WonderlandNativeBagModel Create()
            {
                var model = new WonderlandNativeBagModel(bossId);
                for (var slot = 0; slot < firstFree; slot++) model.Slots[slot] = new(1001, 1, 0);
                model.Receive(PacketBuilder.MonsterLoot(bossId, [new MonsterLootEntry(0, 0, reward.Id, 1)]));
                return model;
            }

            // Native 10050 adds into its chosen free slot. Native 10033 skips
            // empty records, so the durable slot cannot evict that extra item.
            var old = Create();
            old.Receive(PacketBuilder.WonderlandBossLootPickupAck(0x1448, bossId, 0));
            foreach (var page in PacketBuilder.KitBagDetailPages(character)) old.Receive(page);
            Check.True(old.Quantity(reward.Id) == expected + 1 && old.Slots[firstFree].Bound == 0,
                "the previous local ACK plus snapshot leaves the observed unbound ghost in a different slot");

            var current = Create();
            foreach (var packet in packets) current.Receive(packet);
            Check.True(current.Quantity(reward.Id) == expected && current.Slots[11].Bound == 1 &&
                !current.PanelOpen && !current.Sparkling && current.LootCount == 0,
                "actual pickup packets project only the bound durable award and close the loot panel/spark");
            foreach (var packet in packets) current.Receive(packet);
            Check.True(current.Quantity(reward.Id) == expected && !current.PanelOpen &&
                !current.Sparkling && current.LootCount == 0,
                "replayed projection and controlled clear cannot add a sack or underflow the corpse count");
        }
    }

    // Deliberately models the relevant native branches, not server inventory
    // allocation: consume-item type21 calls4AC8F6/416A50; it merges eligible
    // zero-instance-ID stacks by template/bound within a page before inserting
    // the remainder in that page's first free slot. All modeled items have
    // Overlap99 and instance DWORD0. 4EB8EE skips IDs <= 0;
    // 4DF72A/4851F0 clears loot without a bag add, then 5CA180 closes the panel.
    // 10056 kind-1 records only update quick-equip metadata on an existing item.
    private sealed class WonderlandNativeBagModel(uint bossId)
    {
        public Dictionary<int, NativeLootItem> Slots { get; } = [];
        public List<NativeLootItem> Acquisitions { get; } = [];
        private NativeLootItem _loot;
        public int LootCount { get; private set; }
        public bool PanelOpen { get; private set; }
        public bool Sparkling { get; private set; }
        public int Quantity(uint itemId) => Slots.Values.Where(item => item.Id == itemId).Sum(item => item.Stack);

        public void Receive(byte[] packet)
        {
            var opcode = ReadOpcode(packet);
            if (opcode == 10033 && packet.Length == 888)
            {
                var first = packet[16] * 24 + packet[17];
                for (var index = 0; index < 12; index++)
                {
                    var offset = 24 + index * 72;
                    if (BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset)) <= 0) continue;
                    Slots[first + index] = ReadItem(packet, offset);
                }
            }
            else if (opcode == 10185 && packet.Length == 80)
            {
                var item = ReadItem(packet, 8);
                AddConsumable(item);
                Acquisitions.Add(item);
            }
            else if (opcode == 10052 && packet.Length == 16 &&
                     BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == uint.MaxValue)
            {
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)) * 24 +
                    BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10));
                Slots.Remove(slot);
            }
            else if (opcode == Opcodes.MonsterDrops && packet.Length == 84 &&
                     BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == bossId)
            {
                LootCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
                Check.Equal(1, LootCount, "claimed corpse is rehydrated with exactly one native slot");
                _loot = ReadItem(packet, 12);
                PanelOpen = Sparkling = true;
            }
            else if (opcode == Opcodes.MoveItem && packet.Length == 16 &&
                     BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == bossId)
            {
                var recipient = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
                if (recipient == 0x1448 && LootCount > 0)
                    AddConsumable(_loot);
                if (recipient == 0 || LootCount > 0)
                {
                    Check.True(LootCount > 0, "neutral native clear requires an occupied slot before decrement");
                    LootCount--;
                    PanelOpen = Sparkling = LootCount > 0;
                }
            }
        }

        private void AddConsumable(NativeLootItem incoming)
        {
            var remaining = incoming.Stack;
            for (var page = 0; page < 4; page++)
            {
                for (var slot = page * 24; slot < (page + 1) * 24 && remaining > 0; slot++)
                {
                    if (!Slots.TryGetValue(slot, out var item) || item.Id != incoming.Id ||
                        item.Bound != incoming.Bound || item.Stack >= 99) continue;
                    var merged = Math.Min(remaining, 99 - item.Stack);
                    Slots[slot] = item with { Stack = item.Stack + merged };
                    remaining -= merged;
                }
                if (remaining == 0) return;
                for (var slot = page * 24; slot < (page + 1) * 24; slot++)
                {
                    if (Slots.ContainsKey(slot)) continue;
                    Check.True(remaining <= 99, "a native incoming consumable fits one Overlap99 slot");
                    Slots[slot] = incoming with { Stack = remaining };
                    return;
                }
            }
            throw new InvalidOperationException("The native acquisition has no capacity.");
        }

        private static NativeLootItem ReadItem(byte[] packet, int offset) =>
            new(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset)), packet[offset + 27], packet[offset + 26]);
    }

    private readonly record struct NativeLootItem(uint Id, int Stack, byte Bound);
}
