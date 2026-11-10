using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandBossLootHandlerCheckName =
        "Wonderland raw boss pickup preserves current inventory, retries full bags, and clears only its owner's loot";

    public static async Task RunWonderlandBossLootHandlerAsync()
    {
        CheckWonderlandNativeLootWire();
        await CheckWonderlandBossPickupAsync(WonderlandChestClaimStatus.Claimed);
        await CheckWonderlandBossPickupAsync(WonderlandChestClaimStatus.AlreadyClaimed);
        await CheckWonderlandBossStaleProjectionAsync();
    }

    private static async Task CheckWonderlandBossPickupAsync(WonderlandChestClaimStatus successStatus)
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var leader = fixture.Party.Leader;
        var follower = fixture.Party.Followers.Single();
        var registry = leader.Registry;
        var store = new WonderlandBossHandlerStore();
        registry.ConfigureWonderlandChests(store);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var boss = runtime.Map.SnapshotMonsters().Single(monster =>
            WonderlandBossLootPolicy.TryResolve(monster.ObjectId, leader.Character.Camp, out _));
        await PlaceWonderlandBossViewerAsync(leader.Handler, leader.Character, leader.Session, registry, boss);
        await PlaceWonderlandBossViewerAsync(follower.Handler, follower.Character, follower.Session, registry, boss);
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        Check.True(store.BossRequests.Count == 0, "a live boss cannot reach the durable sack claim handler");
        KillWonderlandHandlerMonster(fixture, runtime, boss.ObjectId);
        await registry.RefreshWonderlandBossLootAsync(leader.Session, CancellationToken.None);
        await registry.RefreshWonderlandBossLootAsync(follower.Session, CancellationToken.None);
        await CheckWonderlandProgressionDoesNotClearLootAsync(leader, boss);
        Check.True(leader.ReadPackets().Any(packet => IsWonderlandBossDrops(packet, boss.ObjectId)) &&
            follower.Transport.ReadLegacyPackets().Any(packet => IsWonderlandBossDrops(packet, boss.ObjectId)),
            "both admitted viewers initially receive their own sparkling boss sack slot");

        var reader = new WonderlandBossHandlerSnapshots(WonderlandBossSnapshot(revision: 41, sackCount: 7));
        SetHandlerField(leader.Handler, "_characterSnapshots", reader);
        await CheckWonderlandBossPickupRejectionsAsync(leader, boss, store);
        var originalBag = leader.Character.KitBag;
        var beforeFull = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        Check.True(store.BossRequests.Count == 1 && reader.Reads == 0 && leader.Character.KitBag == originalBag &&
            leader.ReadPackets().Skip(beforeFull).All(packet => ReadOpcode(packet) is not (Opcodes.MoveItem or 10185)) &&
            registry.TryResolveWonderlandBossLootPickup(leader.Session, boss.ObjectId, 0, DateTimeOffset.UtcNow, out _),
            "a full bag reads no replacement inventory, clears no loot, and leaves the exact corpse claim retryable");

        store.BossReceipt = new(successStatus, 40, [new(4450, 1, 1)]);
        var beforeSuccess = leader.ReadPackets().Count;
        var observerBefore = follower.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        var packets = leader.ReadPackets().Skip(beforeSuccess).ToArray();
        Check.True(packets.All(packet => ReadOpcode(packet) is not (Opcodes.ServerNote or Opcodes.PythonNote)),
            "successful corpse loot and durable replay update the bag and clear the loot without a popup dialogue");
        var acquisitions = packets.Where(packet => ReadOpcode(packet) == 10185).ToArray();
        Check.True(acquisitions.Length == (successStatus == WonderlandChestClaimStatus.Claimed ? 1 : 0) &&
            acquisitions.All(packet => packet.Length == 80 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 4450 &&
                packet[34] == 1 && packet[35] == 1),
            "fresh corpse rewards use the daily item notice for one bound sack; durable replays do not announce it again");
        var request = store.BossRequests.Last();
        Check.True(store.BossRequests.Count == 2 && reader.Reads == 1 && request.IsValid &&
            request.BossObjectId == boss.ObjectId && request.SackItemId == 4450 &&
            request.Subject.CharacterId == leader.Character.Id && request.WorldInstanceId == runtime.InstanceId &&
            leader.Character.KitBag == reader.Current.Character!.Loadout.KitBag,
            "the current owned inventory snapshot wins over an older one-sack receipt, including durable AlreadyClaimed replay");
        AssertWonderlandBossBagPackets(packets, leader.Character);
        var clear = PacketBuilder.WonderlandBossLootPickupAck(0, boss.ObjectId, 0);
        Check.True(packets.Count(packet => packet.SequenceEqual(clear)) == 1 &&
            follower.Transport.ReadLegacyPackets().Skip(observerBefore).All(packet => !packet.SequenceEqual(clear)) &&
            !registry.TryResolveWonderlandBossLootPickup(leader.Session, boss.ObjectId, 0, DateTimeOffset.UtcNow, out _) &&
            registry.TryResolveWonderlandBossLootPickup(follower.Session, boss.ObjectId, 0, DateTimeOffset.UtcNow, out _),
            "native pickup acknowledgment clears only the claimant's spark and leaves the other party member's entitlement");
        AssertWonderlandNativeLootProjection(packets, boss.ObjectId, leader.Character);
        await registry.RefreshWonderlandBossLootAsync(follower.Session, CancellationToken.None);
        Check.True(follower.Transport.ReadLegacyPackets().Skip(observerBefore).Any(packet =>
            IsWonderlandBossDrops(packet, boss.ObjectId)), "the unclaimed observer still receives the real sack slot");
        var beforeRepeat = leader.ReadPackets().Count;
        for (var slot = 0; slot < 8; slot++)
            await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId, index: slot));
        Check.True(store.BossRequests.Count == 2 && reader.Reads == 1 && leader.ReadPackets().Count == beforeRepeat,
            "native Take All's zero-based slots0..7 cannot repeat a grant or invent unavailable slots");
    }

    private static async Task CheckWonderlandBossPickupRejectionsAsync(InstanceCallerFixture leader,
        MonsterRuntimeSnapshot boss, WonderlandBossHandlerStore store)
    {
        foreach (var packet in new[]
        {
            WonderlandBossPickupPacket(boss.ObjectId, operation: 1),
            WonderlandBossPickupPacket(boss.ObjectId, index: 1),
            WonderlandBossPickupPacket(boss.ObjectId, index: -1),
            WonderlandBossPickupPacket(boss.ObjectId, index: 8),
            WonderlandBossPickupPacket(boss.ObjectId + 99),
            WonderlandBossPickupPacket(boss.ObjectId, declaredLength: 12),
            new GamePacket(PacketBuilder.WonderlandBossLootPickupAck(0x1448, boss.ObjectId, 0)),
            new GamePacket(PacketBuilder.MonsterLootPickup(0x1448, boss.ObjectId, 0)),
            new GamePacket(WonderlandBossPickupPacket(boss.ObjectId).Buffer.Concat(new byte[4]).ToArray())
        })
            await InvokeAsync(leader.Handler, packet);
        leader.Character.CurrentHp = 0;
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        leader.Character.CurrentHp = leader.Character.MaxHp;
        leader.Character.PositionX = boss.X + WonderlandBossLootPolicy.InteractionRadius + 0.1f;
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        leader.Character.PositionX = float.NaN;
        await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId));
        leader.Character.PositionX = boss.X;
        Check.True(store.BossRequests.Count == 0,
            "wrong operation, wrong slots, unknown corpses, malformed frames, death, distance, and NaN reject before persistence");
    }

    private static async Task CheckWonderlandBossStaleProjectionAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        var registry = leader.Registry;
        var store = new WonderlandBossHandlerStore { BossReceipt = new(WonderlandChestClaimStatus.Claimed, 40, [new(4450, 1, 1)]) };
        registry.ConfigureWonderlandChests(store);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var boss = runtime.Map.SnapshotMonsters().Single(monster =>
            WonderlandBossLootPolicy.TryResolve(monster.ObjectId, leader.Character.Camp, out _));
        await PlaceWonderlandBossViewerAsync(leader.Handler, leader.Character, leader.Session, registry, boss);
        KillWonderlandHandlerMonster(fixture, runtime, boss.ObjectId);
        var reader = new WonderlandBossHandlerSnapshots(WonderlandBossSnapshot(revision: 39, sackCount: 7));
        SetHandlerField(leader.Handler, "_characterSnapshots", reader);
        var originalBag = leader.Character.KitBag;
        var before = leader.ReadPackets().Count;
        var rejected = false;
        try { await InvokeAsync(leader.Handler, WonderlandBossPickupPacket(boss.ObjectId)); }
        catch (InvalidDataException) { rejected = true; }
        Check.True(rejected && store.BossRequests.Count == 1 && leader.Character.KitBag == originalBag &&
            leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) is not (Opcodes.MoveItem or 10185)) &&
            registry.TryResolveWonderlandBossLootPickup(leader.Session, boss.ObjectId, 0, DateTimeOffset.UtcNow, out _),
            "a snapshot older than the committed inventory revision cannot replace the bag or clear the corpse entitlement");
    }

    private static bool IsWonderlandBossDrops(byte[] packet, uint bossId) => packet.Length == 84 &&
        ReadOpcode(packet) == Opcodes.MonsterDrops && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == bossId &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 1 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 4450;
}
