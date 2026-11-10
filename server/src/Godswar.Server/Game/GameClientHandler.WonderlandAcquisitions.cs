using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendWonderlandRewardProjectionAsync(string bagBefore,
        IReadOnlyList<WonderlandChestItemReward> freshRewards, CancellationToken token)
    {
        if (_character is null) return;
        var packets = BuildWonderlandRewardProjection(_character, bagBefore, freshRewards);
        token.ThrowIfCancellationRequested();
        // Item sweep 2026-11-04: the whole projection is >= 105 packets (8 detail pages + 96 slot indexes + clears)
        // plus 2 per reward notice, and the reliable egress queue holds 128 packets. Any gift pack with more than
        // about 11 reward stacks was rejected and the player was disconnected (live: Newbie Gift Bag 3884).
        // Admit it in chunks that each fit the queue; each chunk waits for the previous one to drain. A notice
        // never leaves its trailing scratch clear, and a chunk that starts with a notice re-clears slot0 first.
        // An admitted chunk must finish even if the caller cancels afterward.
        var limit = Math.Max(16, _session.ReliableEgressQueueItems - 16);
        var hydration = PacketBuilder.KitBagDetailPages(_character).Length + PacketBuilder.KitBagSlotIndexes(_character).Length;
        foreach (var chunk in ChunkWonderlandRewardProjection(packets, limit, hydration))
        {
            var batch = chunk.Select(packet => (ReadOnlyMemory<byte>)packet).ToArray();
            var admitted = false;
            Task write = Task.CompletedTask;
            for (var attempt = 0; attempt < 120 && !_session.IsDisconnected; attempt++)
            {
                if (_session.TryAdmitExactBatch(batch, out write)) { admitted = true; break; }
                await Task.Delay(25, CancellationToken.None);   // queue still holds earlier traffic: let it drain
            }
            if (!admitted)
            {
                _session.Disconnect();
                throw new IOException("The committed Wonderland inventory projection could not be admitted.");
            }
            await write;
        }
    }

    /// <summary>Splits a reward projection into egress batches of at most <paramref name="limit"/> packets, keeping
    /// each 10185 notice with its trailing slot0 clear, re-clearing slot0 before a chunk that starts with a notice, and
    /// keeping the trailing <paramref name="tail"/> packets (the final bag hydration) in one batch when they fit.</summary>
    internal static IReadOnlyList<IReadOnlyList<byte[]>> ChunkWonderlandRewardProjection(IReadOnlyList<byte[]> packets, int limit,
        int tail = 0)
    {
        if (packets.Count <= limit) return [packets];
        if (tail < 0 || tail > limit || tail > packets.Count) tail = 0;
        var tailStart = packets.Count - tail;
        var scratchClear = PacketBuilder.StorageItemKitBagDelete(0);
        var units = new List<byte[][]>();
        for (var index = 0; index < tailStart; index++)
        {
            var isNotice = packets[index].Length >= 4 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packets[index].AsSpan(2)) == 10185;
            if (isNotice && index + 1 < tailStart)
            {
                units.Add([packets[index], packets[index + 1]]);
                index++;
            }
            else units.Add([packets[index]]);
        }
        if (tail > 0) units.Add(packets.Skip(tailStart).ToArray());
        var chunks = new List<IReadOnlyList<byte[]>>();
        var current = new List<byte[]>();
        foreach (var unit in units)
        {
            if (current.Count > 0 && current.Count + unit.Length > limit)
            {
                chunks.Add(current);
                current = [];
            }
            if (current.Count == 0 && unit.Length == 2) current.Add(scratchClear);
            current.AddRange(unit);
        }
        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    internal static IReadOnlyList<byte[]> BuildWonderlandRewardProjection(GameCharacter character,
        string bagBefore, IReadOnlyList<WonderlandChestItemReward> freshRewards)
    {
        var packets = PacketBuilder.KitBagMutationDeletionAcknowledgements(bagBefore, character.KitBag).ToList();
        if (freshRewards.Count > 0)
        {
            // Use the same native acquisition as Online Award. It both shows
            // the item notification and temporarily mutates the native bag.
            // Consumables first merge eligible page0 stacks, then put any
            // remainder into its first free slot. Reserve slot0 even in a full
            // bag; each <=99 reward fits there without advancing to page1.
            // Cleanup removes that remainder; final hydration restores merges.
            var scratchClear = PacketBuilder.StorageItemKitBagDelete(0);
            if (!packets.Any(packet => packet.AsSpan().SequenceEqual(scratchClear))) packets.Add(scratchClear);
            foreach (var reward in freshRewards)
            {
                if (reward.ItemId == 0 || reward.Quantity <= 0 || reward.Bound is < 0 or > 1)
                    throw new InvalidDataException("Wonderland acquisition requires a valid committed item reward.");
                var item = Enumerable.Range(0, KitBagItemGrantPlanner.SlotCount)
                    .Select(slot => KitBagSlots.GetItem(character.KitBag, slot))
                    .FirstOrDefault(candidate => candidate.Id == reward.ItemId && candidate.Bound == reward.Bound);
                // A newer snapshot may already have consumed the grant. The
                // receipt still describes the fresh award, while final hydration
                // must retain that newer inventory. The grant planner uses Q1/G1.
                if (item.IsEmpty) item = CompactItemEntry.Empty with
                    { Id = reward.ItemId, Quality = 1, Grade = 1, Bound = reward.Bound };
                var remaining = (int)reward.Quantity;
                while (remaining > 0)
                {
                    var quantity = checked((short)Math.Min(remaining, 99));
                    packets.Add(PacketBuilder.SystemAddItemWithAcquisitionLog(item with { Stack = quantity }));
                    packets.Add(scratchClear);
                    remaining -= quantity;
                }
            }
        }
        // Empty10033 records cannot remove an optimistic item; all transient
        // acquisitions above have already been evicted before the final snapshot.
        packets.AddRange(PacketBuilder.KitBagDetailPages(character));
        packets.AddRange(PacketBuilder.KitBagSlotIndexes(character));
        return packets;
    }
}
