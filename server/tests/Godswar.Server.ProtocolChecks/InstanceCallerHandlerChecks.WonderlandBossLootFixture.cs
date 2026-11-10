using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task PlaceWonderlandBossViewerAsync(GameClientHandler handler, GameCharacter character,
        ClientSession session, GameSessionRegistry registry, MonsterRuntimeSnapshot boss)
    {
        character.PositionX = boss.X;
        character.PositionZ = boss.Z;
        character.CurrentHp = character.MaxHp;
        registry.UpdateCharacter(session, character, advanceWorldRevision: false);
        await (Task)(FindHandlerMethod("RefreshNearbyWorldObjectsAsync").Invoke(handler,
            ["WonderlandBossLootTest", CancellationToken.None]) ?? throw new InvalidOperationException("Expected world refresh task."));
    }

    private static CharacterAccountSnapshot WonderlandBossSnapshot(long revision, short sackCount)
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = snapshot.Character!;
        var bag = KitBagSlots.SetSlot(character.Loadout.KitBag, 11,
            (CompactItemEntry.Empty with { Id = 4450, Stack = sackCount, Bound = 1 }).ToCompactString());
        return snapshot with { Character = character with { Loadout = character.Loadout with
            { KitBag = bag, InventoryRevision = revision } } };
    }

    private static GamePacket WonderlandBossPickupPacket(uint bossId, byte operation = 0,
        int index = 0, ushort declaredLength = 20)
    {
        var bytes = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, declaredLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.MoveItem);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), bossId);
        // Actual ignored live request logged by Tempest for Alpha on September11.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x7701EE77);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), index);
        bytes[16] = operation;
        bytes[17] = 0x0C;
        return new GamePacket(bytes);
    }

    private static void AssertWonderlandBossBagPackets(IReadOnlyList<byte[]> actual, GameCharacter current)
    {
        foreach (var packet in PacketBuilder.KitBagDetailPages(current).Concat(PacketBuilder.KitBagSlotIndexes(current)))
            Check.True(actual.Count(candidate => candidate.SequenceEqual(packet)) == 1,
                "boss loot sends each authoritative current bag frame exactly once");
        Check.True(actual.All(packet => ReadOpcode(packet) is not (10237 or Opcodes.PetExperience or Opcodes.PetOperationResult)),
            "boss pickup emits no pet list, experience, or presence result that could reset a merged companion");
    }

    private sealed class WonderlandBossHandlerSnapshots(CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public CharacterAccountSnapshot Current { get; } = snapshot;
        public int Reads { get; private set; }
        public Task<CharacterAccountSnapshot> ReadAsync(int accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(Current.AccountId, accountId, "boss loot reloads its authenticated account");
            Reads++;
            return Task.FromResult(Current);
        }
    }

    private sealed class WonderlandBossHandlerStore : IWonderlandChestClaimStore
    {
        public List<WonderlandBossLootClaimRequest> BossRequests { get; } = [];
        public WonderlandChestClaimReceipt BossReceipt { get; set; } = new(WonderlandChestClaimStatus.InventoryFull, 0, []);
        public Task<WonderlandChestClaimReceipt> ClaimAsync(WonderlandChestClaimRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Boss clicks cannot claim an island chest.");
        public Task<WonderlandChestClaimReceipt> ClaimBossAsync(WonderlandBossLootClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BossRequests.Add(request);
            return Task.FromResult(BossReceipt);
        }
    }
}
