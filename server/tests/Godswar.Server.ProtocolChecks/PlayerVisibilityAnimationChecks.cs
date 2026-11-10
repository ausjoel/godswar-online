using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerVisibilityAnimationChecks
{
    public static async Task RunAsync()
    {
        await CheckFirstMeetingAsync();
        CheckRealMovementStates();
    }

    private static async Task CheckFirstMeetingAsync()
    {
        var store = new VisibilityStore();
        var registry = new GameSessionRegistry(store);
        var existingTransport = new ScriptedLegacyByteTransport();
        var enteringTransport = new ScriptedLegacyByteTransport();
        await using var existingSession = new ClientSession(existingTransport);
        await using var enteringSession = new ClientSession(enteringTransport);
        var existing = Character(901, 1901, 167f, -97f);
        var entering = Character(902, 1902, 164f, -100f);
        GameHandlerOwnershipTestFences.Bind(registry, existingSession, existing.AccountId, existing);
        GameHandlerOwnershipTestFences.Bind(registry, enteringSession, entering.AccountId, entering);
        const uint existingId = 901;
        const uint enteringId = 902;
        registry.JoinMap(existingSession, existing.AccountId, existing, existingId);
        registry.JoinMap(enteringSession, entering.AccountId, entering, enteringId, worldReady: false);
        var handler = new GameClientHandler(
            enteringSession, store, registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        SetField(handler, "_character", entering);
        SetField(handler, "_account", new AccountIdentity(entering.AccountId, "visibility-check"));
        SetField(handler, "_registered", true);
        try
        {
            var method = typeof(GameClientHandler).GetMethod(
                "SendMapPlayersAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("World-join publication method missing.");
            await ((Task?)method.Invoke(handler, [CancellationToken.None])
                ?? throw new InvalidOperationException("World-join publication returned no task."));

            AssertIdleAppearance(ReadPackets(enteringTransport), existingId, existing,
                expectedPositionCount: 2, "new viewer's initial and activation snapshots");
            AssertIdleAppearance(ReadPackets(existingTransport), enteringId, entering,
                expectedPositionCount: 1, "existing viewer's arrival announcement");
        }
        finally
        {
            registry.Remove(existingSession);
            registry.Remove(enteringSession);
        }
    }

    private static void AssertIdleAppearance(
        IReadOnlyList<byte[]> packets, uint objectId, GameCharacter character,
        int expectedPositionCount, string context)
    {
        var spawnIndex = packets.ToList().FindIndex(packet =>
            Opcode(packet) == 10021 && ReadUInt32(packet, 4) == objectId);
        Check.True(spawnIndex >= 0, $"{context}: remote player spawns");
        var positions = packets.Select((packet, index) => (packet, index))
            .Where(entry => Opcode(entry.packet) == Opcodes.Walk &&
                (ReadUInt32(entry.packet, 4) & 0xffff) == objectId).ToArray();
        Check.Equal(expectedPositionCount, positions.Length, $"{context}: replay count");
        foreach (var (packet, index) in positions)
        {
            Check.True(index > spawnIndex, $"{context}: position follows appearance");
            Check.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)),
                $"{context}: stationary appearance uses native idle, never walking");
            Check.Equal(character.PositionX, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(8)),
                $"{context}: authoritative X");
            Check.Equal(character.PositionZ, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(12)),
                $"{context}: authoritative Z");
        }
    }

    private static void CheckRealMovementStates()
    {
        // Native sender 0x492669 writes 2 while moving; 0x4926F5 writes 0 on
        // stopping. The receiver queues both states even at equal coordinates.
        const uint objectId = 913;
        foreach (var sample in new[]
        {
            Convert.FromHexString("1400D22748140200000027430000C2C20000803F"),
            Convert.FromHexString("1400D22748140000000027430000C2C20000803F")
        })
        {
            var original = sample.ToArray();
            var projected = PacketBuilder.PlayerWorldMovement(sample, objectId);
            Check.Equal((ushort)objectId, BinaryPrimitives.ReadUInt16LittleEndian(projected.AsSpan(4)),
                "real movement remaps the sender to its remote object ID");
            Check.True(projected.AsSpan(6).SequenceEqual(sample.AsSpan(6)),
                "real walk and stop preserve the native animation, position, and facing");
            Check.True(original.SequenceEqual(sample), "forwarding leaves the ingress packet unchanged");
        }
    }

    private static GameCharacter Character(int id, int accountId, float x, float z) => new()
    {
        Id = id, AccountId = accountId, Name = $"Visible{id}", Camp = GameDefaults.SpartaCamp,
        CurrentMap = 0, Level = 90, CurrentHp = 1000, MaxHp = 1000,
        CurrentMp = 100, MaxMp = 100, PositionX = x, PositionZ = z,
        Equipment = GameDefaults.DefaultEquipment(0), KitBag = GameDefaults.EmptyKitBag
    };

    private static IReadOnlyList<byte[]> ReadPackets(ScriptedLegacyByteTransport transport)
    {
        var bytes = transport.WrittenBytes;
        new PacketCipher().Transform(bytes);
        var packets = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            Check.True(length >= 4 && length <= bytes.Length - offset, "visibility packet framing");
            packets.Add(bytes.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return packets;
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));

    private static uint ReadUInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));

    private static void SetField(GameClientHandler handler, string name, object value) =>
        (typeof(GameClientHandler).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
         ?? throw new InvalidOperationException($"Missing field {name}.")).SetValue(handler, value);

    private sealed class VisibilityStore : GameStoreTestStub
    {
        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId, int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterStats?>(null);
    }
}
