using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandChestsCheckName =
        "Wonderland native treasure chests require settled island clears, proximity, and exact admitted ownership";

    public static async Task RunWonderlandChestsAsync()
    {
        await CheckWonderlandChestSilentSuccessAsync();
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var leader = fixture.Party.Leader;
        var registry = leader.Registry;
        var store = new RecordingWonderlandChestStore();
        registry.ConfigureWonderlandChests(store);
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var chests = registry.AddWonderlandTreasureChests(leader.Session, []);
        CheckWonderlandChestDefinitions(chests, leader.Character.Camp);
        var first = chests[0];
        var initialPackets = leader.ReadPackets().Count;
        await ClickWonderlandChestAsync(leader, first, claim: false);
        Check.True(store.Requests.Count == 0 && leader.ReadPackets().Skip(initialPackets).Any(packet =>
                packet.SequenceEqual(PacketBuilder.NpcDialogOpenAck(first.InteractionId, 58, first.NpcKey))),
            "a native chest advertises its claim button without granting anything on open");
        var beforeLockedClaim = leader.ReadPackets().Count;
        await InvokeAsync(leader.Handler, CreateWonderlandChestClaimPacket(first));
        var lockedReplies = leader.ReadPackets().Skip(beforeLockedClaim).ToArray();
        Check.True(store.Requests.Count == 0 && lockedReplies.Length == 1 && lockedReplies[0].SequenceEqual(
                PacketBuilder.PersonalGameLog("Defeat this island's required enemies before opening its treasure chest.")),
            "claiming a locked chest sends only the daily-style text log, with no result dialogue, fake item, or inventory write");
        foreach (var position in new[]
        {
            (Hp: 0, X: first.X, Z: first.Z),
            (Hp: leader.Character.MaxHp, X: first.X + WonderlandTreasureChestPolicy.InteractionRadius + 0.01f, Z: first.Z),
            (Hp: leader.Character.MaxHp, X: float.NaN, Z: first.Z)
        })
        {
            leader.Character.CurrentHp = position.Hp;
            leader.Character.PositionX = position.X;
            leader.Character.PositionZ = position.Z;
            var before = leader.ReadPackets().Count;
            await InvokeAsync(leader.Handler, CreateWonderlandChestPacket(first));
            Check.True(store.Requests.Count == 0 && leader.ReadPackets().Skip(before).All(packet =>
                    ReadOpcode(packet) != Opcodes.NpcDialogOpen),
                "dead, distant, and non-finite chest clicks cannot open the native interaction or claim a reward");
        }

        MoveToWonderlandChest(leader, first, useEntrance: true);
        fixture.Titles.FailuresRemaining = 1;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        MoveToWonderlandChest(leader, first);
        var unready = await registry.ClaimWonderlandChestAsync(leader.Session, first,
            WonderlandNow(runtime), CancellationToken.None);
        Check.True(unready.Status == WonderlandChestClaimStatus.Unavailable && store.Requests.Count == 0 &&
            runtime.Map.SnapshotMonsters().Count(monster => monster.IsAlive) > 4,
            "a boss clear keeps optional mobs alive but its chest waits for the durable island milestone");
        await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
        leader.Character.CurrentHp = 0;
        await RejectWonderlandChestAsync(leader, first, store, WonderlandNow(runtime), "dead member");
        leader.Character.CurrentHp = leader.Character.MaxHp;
        leader.Character.PositionX = first.X + WonderlandTreasureChestPolicy.InteractionRadius + 0.01f;
        await RejectWonderlandChestAsync(leader, first, store, WonderlandNow(runtime), "distant member");
        leader.Character.PositionX = float.NaN;
        await RejectWonderlandChestAsync(leader, first, store, WonderlandNow(runtime), "non-finite position");
        MoveToWonderlandChest(leader, first);
        await ClickWonderlandChestAsync(leader, first, claim: false);
        foreach (var malformed in new[]
        {
            CreateWonderlandChestClaimPacket(first, length: 91),
            CreateWonderlandChestClaimPacket(first, dialog: 57),
            CreateWonderlandChestClaimPacket(first, subId: 0),
            CreateWonderlandChestClaimPacket(first, echo: 57)
        })
            await InvokeAsync(leader.Handler, malformed);
        Check.True(store.Requests.Count == 0, "malformed claim actions cannot consume treasure");
        await InvokeAsync(leader.Handler, CreateWonderlandChestPacket(first with { ObjectId = 0 }));
        await InvokeAsync(leader.Handler, CreateWonderlandChestClaimPacket(first));
        Check.True(store.Requests.Count == 0, "opening another NPC invalidates the prior chest claim context");
        await RejectWonderlandChestAsync(leader, first with { TemplateKey = "Fane_016_Male15" }, store,
            WonderlandNow(runtime), "wrong chest identity");
        await RejectWonderlandChestAsync(leader, first with { X = first.X + 1 }, store,
            WonderlandNow(runtime), "altered chest position");
        var malformedBefore = leader.ReadPackets().Count;
        await ClickWonderlandChestAsync(leader, first, packetLength: 47);
        Check.True(store.Requests.Count == 0 && leader.ReadPackets().Skip(malformedBefore).All(packet =>
                ReadOpcode(packet) != Opcodes.NpcDialogOpen),
            "a non-canonical native NPC-open packet cannot open the chest or claim treasure");

        for (var island = 1; island <= 8; island++)
        {
            var chest = chests[island - 1];
            if (island > 1)
            {
                MoveToWonderlandChest(leader, chest, useEntrance: true);
                await ClearWonderlandHandlerIslandAsync(fixture, runtime);
            }
            var before = leader.ReadPackets().Count;
            await ClickWonderlandChestAsync(leader, chest);
            var replies = leader.ReadPackets().Skip(before).ToArray();
            var openIndex = Array.FindIndex(replies, packet => packet.SequenceEqual(
                PacketBuilder.NpcDialogOpenAck(chest.InteractionId, 58, chest.NpcKey)));
            var resultIndex = Array.FindIndex(replies, packet => packet.SequenceEqual(PacketBuilder.ServerNote(
                "Your treasure is not ready yet. Please try again shortly.")));
            var context = registry.GetWorldInstanceSessions(runtime.InstanceId)
                .Single(member => ReferenceEquals(member.Session, leader.Session));
            var request = store.Requests.Last();
            var milestone = fixture.Titles.Requests.Last(value => value.IslandNumber == island);
            Check.True(store.Requests.Count == island && request.IsValid && request.WorldInstanceId == runtime.InstanceId &&
                request.RealmId == context.RealmId && request.Island == island && request.PartyCamp == leader.Character.Camp &&
                request.MilestoneHash == milestone.RequestHash && request.Subject.AccountId == context.AccountId &&
                request.Subject.CharacterId == leader.Character.Id && request.Ownership == context.Ownership &&
                openIndex >= 0 && resultIndex > openIndex && replies.Any(packet => packet.SequenceEqual(
                    PacketBuilder.CapturedNpcFunctionActionResponse(chest.InteractionId, 58, 0, 150))),
                $"native island {island} chest reaches persistence with the exact frozen milestone and current owner");
        }
        var final = chests[7];
        var last = store.Requests.Last();
        await ClickWonderlandChestAsync(leader, final);
        Check.True(store.Requests.Last() == last && store.Requests.Count == 9,
            "a repeated click supplies the same durable claim key for the store's exactly-once replay");
        var follower = fixture.Party.Followers[0];
        follower.Character.PositionX = final.X;
        follower.Character.PositionZ = final.Z;
        follower.Character.CurrentHp = follower.Character.MaxHp;
        registry.UpdateCharacter(follower.Session, follower.Character, advanceWorldRevision: false);
        await registry.ClaimWonderlandChestAsync(follower.Session, final, WonderlandNow(runtime), CancellationToken.None);
        Check.True(store.Requests.Count == 10 && store.Requests.Last().Subject.CharacterId == follower.Character.Id &&
            store.Requests.Last().MilestoneHash == last.MilestoneHash,
            "each admitted party member has an independent chest entitlement for the same frozen island clear");
        await CheckWonderlandCompletedTreasureWindowAsync(fixture, runtime, final, store);
    }

    private static void CheckWonderlandChestDefinitions(IReadOnlyList<NpcSpawnDefinition> chests, byte camp)
    {
        Check.True(chests.Count == 8 && chests.Select(npc => npc.ObjectId).Distinct().Count() == 8,
            "exactly eight separate native treasure identities are added to the dungeon");
        for (var index = 0; index < 8; index++)
        {
            var chest = chests[index];
            var position = WonderlandTreasureChestPolicy.GetPosition(index + 1, camp);
            Check.True(WonderlandTreasureChestPolicy.TryGetIsland(chest, out var island) && island == index + 1 &&
                chest.ObjectId == WonderlandTreasureChestPolicy.FirstObjectId + index &&
                chest.X == position.X && chest.Z == position.Z &&
                chest.AppearanceType == (index == 4 ? 0x11u | ((uint)camp << 8) : 0x0211u) && chest.SceneKey == "Fane",
                "each chest exposes the native clickable mesh at its audited island location");
        }
        Check.True(chests[4].NpcKey == (camp == 0 ? "Fane_013" : "Fane_012") &&
            WonderlandTreasureChestPolicy.AddChests([], (byte)(1 - camp))[4].NpcKey != chests[4].NpcKey,
            "island five uses the enemy marshal chest identity for the admitted party faction");
    }

    private static async Task RejectWonderlandChestAsync(InstanceCallerFixture actor, NpcSpawnDefinition chest,
        RecordingWonderlandChestStore store, DateTimeOffset now, string reason)
    {
        var before = store.Requests.Count;
        var receipt = await actor.Registry.ClaimWonderlandChestAsync(actor.Session, chest, now, CancellationToken.None);
        Check.True(receipt.Status == WonderlandChestClaimStatus.NotEligible && store.Requests.Count == before,
            $"{reason} is rejected before any durable chest claim");
    }

    private static void MoveToWonderlandChest(InstanceCallerFixture actor, NpcSpawnDefinition chest, bool useEntrance = false)
    {
        WonderlandTreasureChestPolicy.TryGetIsland(chest, out var island);
        var point = useEntrance ? WonderlandTerrainPolicy.GetIsland(island).Entrance :
            new WonderlandPosition(chest.X, 0, chest.Z);
        actor.Character.PositionX = point.X;
        actor.Character.PositionZ = point.Z;
        actor.Character.CurrentHp = actor.Character.MaxHp;
        actor.Registry.UpdateCharacter(actor.Session, actor.Character, advanceWorldRevision: false);
    }

    private static async Task ClickWonderlandChestAsync(InstanceCallerFixture actor, NpcSpawnDefinition chest,
        int packetLength = 48, bool claim = true)
    {
        MoveToWonderlandChest(actor, chest);
        await (Task)(FindHandlerMethod("RefreshNearbyWorldObjectsAsync").Invoke(actor.Handler,
            ["WonderlandChestTest", CancellationToken.None]) ?? throw new InvalidOperationException("Expected NPC refresh task."));
        await InvokeAsync(actor.Handler, CreateWonderlandChestPacket(chest, packetLength));
        if (claim && packetLength == 48)
            await InvokeAsync(actor.Handler, CreateWonderlandChestClaimPacket(chest));
    }

    private static GamePacket CreateWonderlandChestClaimPacket(NpcSpawnDefinition chest,
        int length = 92, int dialog = 58, int subId = -1, int echo = 58)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), chest.InteractionId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), dialog);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), echo);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        bytes.AsSpan(20).Fill(0xA5); // Native tail bytes are deliberately not claim inputs.
        return new GamePacket(bytes);
    }

    private static GamePacket CreateWonderlandChestPacket(NpcSpawnDefinition chest, int packetLength = 48)
    {
        var bytes = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), chest.ObjectId);
        return new GamePacket(bytes);
    }

    private sealed class RecordingWonderlandChestStore : IWonderlandChestClaimStore
    {
        public List<WonderlandChestClaimRequest> Requests { get; } = [];
        public WonderlandChestClaimReceipt Receipt { get; set; } = new(WonderlandChestClaimStatus.Unavailable, 0, []);
        public Task<WonderlandChestClaimReceipt> ClaimAsync(WonderlandChestClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Receipt);
        }
    }
}
