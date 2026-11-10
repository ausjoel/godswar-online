using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandChestSilentSuccessAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        SetHandlerField(leader.Handler, "_itemContent", TestItemContent.Content);
        var store = new RecordingWonderlandChestStore
        {
            Receipt = new(WonderlandChestClaimStatus.InventoryFull, 0, [])
        };
        leader.Registry.ConfigureWonderlandChests(store);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var chest = leader.Registry.AddWonderlandTreasureChests(leader.Session, [])[0];
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var owned = snapshot.Character!;
        var bag = KitBagSlots.SetSlot(owned.Loadout.KitBag, 11,
            (CompactItemEntry.Empty with { Id = WonderlandChestGemRewardPolicy.SapphireIV, Stack = 1, Bound = 1 })
                .ToCompactString());
        bag = KitBagSlots.SetSlot(bag, 12,
            (CompactItemEntry.Empty with { Id = WonderlandChestGemRewardPolicy.EmeraldIV, Stack = 3, Bound = 1 })
                .ToCompactString());
        var reader = new WonderlandTreasureSnapshots(snapshot with { Character = owned with
            { Loadout = owned.Loadout with { KitBag = bag, InventoryRevision = 41 } } });
        SetHandlerField(leader.Handler, "_characterSnapshots", reader);
        var originalBag = leader.Character.KitBag;
        var before = leader.ReadPackets().Count;
        await ClickWonderlandChestAsync(leader, chest);
        var failed = leader.ReadPackets().Skip(before).ToArray();
        Check.True(reader.Reads == 0 && leader.Character.KitBag == originalBag &&
            failed.All(packet => ReadOpcode(packet) is not (Opcodes.PythonNote or 10185)) &&
            failed.Any(packet => packet.SequenceEqual(PacketBuilder.CapturedNpcFunctionActionResponse(
                chest.InteractionId, 58, 0, 123))) &&
            failed.Any(packet => packet.SequenceEqual(PacketBuilder.ServerNote(
                "Make room in your bag, then click the treasure chest again."))),
            "a full chest claim preserves inventory and explains how to retry");
        foreach (var status in new[] { WonderlandChestClaimStatus.Claimed, WonderlandChestClaimStatus.AlreadyClaimed })
        {
            store.Receipt = new(status, 40, [new(WonderlandChestGemRewardPolicy.SapphireIV, 2, 1),
                new(WonderlandChestGemRewardPolicy.EmeraldIV, 2, 1)]);
            before = leader.ReadPackets().Count;
            await ClickWonderlandChestAsync(leader, chest);
            var packets = leader.ReadPackets().Skip(before).ToArray();
            Check.True(packets.Count(packet => packet.SequenceEqual(PacketBuilder.NpcDialogOpenAck(
                    chest.InteractionId, 58, chest.NpcKey))) == 1 &&
                packets.All(packet => ReadOpcode(packet) is not (Opcodes.ServerNote or Opcodes.NpcFunctionActionResponse)),
                "claim menu remains available while successful treasure claims and replays open no result dialogue");
            var logs = packets.Where(packet => ReadOpcode(packet) == 10185).ToArray();
            var expectedLogs = status == WonderlandChestClaimStatus.Claimed
                ? new[] { PacketBuilder.SystemAddItemWithAcquisitionLog(KitBagSlots.GetItem(bag, 11) with { Stack = 2 }),
                    PacketBuilder.SystemAddItemWithAcquisitionLog(KitBagSlots.GetItem(bag, 12) with { Stack = 2 }) }
                : [];
            Check.True(logs.Length == expectedLogs.Length && logs.Zip(expectedLogs)
                    .All(pair => pair.First.SequenceEqual(pair.Second)),
                "only a newly committed treasure claim uses daily-reward acquisition notices with its actual receipt quantities");
            var firstBag = Array.FindIndex(packets, packet => PacketBuilder.KitBagDetailPages(leader.Character)
                .Concat(PacketBuilder.KitBagSlotIndexes(leader.Character)).Any(expected => expected.SequenceEqual(packet)));
            Check.True(packets.All(packet => ReadOpcode(packet) is not (Opcodes.PythonNote or Opcodes.MoveItem)) &&
                Array.FindLastIndex(packets, packet => ReadOpcode(packet) == 10185) < firstBag &&
                logs.All(log => packets[Array.IndexOf(packets, log) + 1]
                    .SequenceEqual(PacketBuilder.StorageItemKitBagDelete(0))),
                "each native acquisition clears its scratch item before the final bag, with no alternate log or corpse add");
            Check.True(leader.Character.KitBag == bag && reader.Reads == (status == WonderlandChestClaimStatus.Claimed ? 1 : 2),
                "successful claim and replay project the newer owned inventory instead of reconstructing the earlier two-and-two grant");
            foreach (var expected in PacketBuilder.KitBagDetailPages(leader.Character)
                         .Concat(PacketBuilder.KitBagSlotIndexes(leader.Character)))
                Check.Equal(1, packets.Count(packet => packet.SequenceEqual(expected)),
                    "silent chest success still sends each authoritative bag frame exactly once");
        }
        Check.True(store.Requests.Count == 3 && store.Requests.All(request => request == store.Requests[0]),
            "full-bag retry and successful replay retain the same exactly-once treasure claim identity");
    }

    private sealed class WonderlandTreasureSnapshots(CharacterAccountSnapshot current) : ICharacterSnapshotReader
    {
        public int Reads { get; private set; }
        public Task<CharacterAccountSnapshot> ReadAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(current.AccountId, accountId, "chest projection reads only its authenticated account");
            Reads++;
            return Task.FromResult(current);
        }
    }
}
