using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class DuelArenaSpawnStreamChecks
{
    public const string CheckName =
        "Duel Arena V6 upper-lobby NPC spawn stream";

    private const int AppearancePacketLength = 108;
    private const ushort AppearanceOpcode = 10_020;
    private const uint ArenaNpcObjectType =
        ((uint)DuelArenaTransporterProtocol.MapId << 16) | 17u;
    private const int TemplateOffset = 44;

    public static Task RunAsync()
    {
        var arena = NpcContentBaselineV6.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId)
            .ToArray();

        CheckCollisionSafety(arena);

        var tracker = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            arena,
            static npc => npc.ObjectId,
            static npc => npc.X,
            static npc => npc.Z,
            "Duel Arena NPC");
        Check.True(
            tracker.TryCalculate(
                playerX: DuelArenaLobbyLayoutV6.ObservedCheckpointX,
                playerZ: DuelArenaLobbyLayoutV6.ObservedCheckpointZ,
                out var initial),
            "AresDev's observed upper-lobby position resolves to an AOI cell");

        ExpectedSpawn[] expected =
        [
            new(
                DuelArenaNpcRoster.VendorNpcId,
                DuelArenaNpcRoster.VendorTemplateKey,
                DuelArenaLobbyLayoutV6.VendorSpawnX,
                DuelArenaLobbyLayoutV6.VendorSpawnZ),
            new(
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                "Arena_003_Male18",
                DuelArenaTransporterProtocol.GatekeeperSpawnX,
                DuelArenaTransporterProtocol.GatekeeperSpawnZ),
            new(
                DuelArenaNpcRoster.WardNpcId,
                DuelArenaNpcRoster.WardTemplateKey,
                DuelArenaLobbyLayoutV5.WardSpawnX,
                DuelArenaLobbyLayoutV5.WardSpawnZ),
            new(
                DuelArenaNpcRoster.PhysicianNpcId,
                DuelArenaNpcRoster.PhysicianTemplateKey,
                DuelArenaLobbyLayoutV5.PhysicianSpawnX,
                DuelArenaLobbyLayoutV5.PhysicianSpawnZ)
        ];

        Check.Equal(
            expected.Length,
            initial.Entering.Count,
            "upper-lobby initial NPC count");
        for (var index = 0; index < expected.Length; index++)
        {
            var npc = initial.Entering[index];
            var wanted = expected[index];
            Check.True(
                npc.ObjectId == wanted.ObjectId &&
                npc.InteractionId == wanted.ObjectId &&
                npc.TemplateKey == wanted.TemplateKey &&
                npc.X == wanted.X &&
                npc.Z == wanted.Z &&
                npc.Detail10077.Length == 0 &&
                npc.Detail10080.Length == 0,
                $"upper-lobby actor {wanted.ObjectId} has its exact " +
                "identity, stock template, coordinates, and plain spawn " +
                "shape");
        }

        var stream = PacketBuilder.NpcSpawns(initial.Entering);
        Check.Equal(
            expected.Length * AppearancePacketLength,
            stream.Length,
            "four unadorned upper-lobby appearance frames");

        var offset = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                offset + AppearancePacketLength <= stream.Length,
                $"appearance frame {index + 1} is complete");
            var packet = stream.AsSpan(offset, AppearancePacketLength);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(packet);
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
            var objectType =
                BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
            var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
            var x = BinaryPrimitives.ReadSingleLittleEndian(packet[28..]);
            var y = BinaryPrimitives.ReadSingleLittleEndian(packet[32..]);
            var z = BinaryPrimitives.ReadSingleLittleEndian(packet[36..]);
            var template = Encoding.ASCII
                .GetString(packet[TemplateOffset..])
                .TrimEnd('\0');
            var wanted = expected[index];

            Check.True(
                length == AppearancePacketLength &&
                opcode == AppearanceOpcode &&
                objectType == ArenaNpcObjectType &&
                objectId == wanted.ObjectId &&
                x == wanted.X &&
                y == 0f &&
                z == wanted.Z &&
                template == wanted.TemplateKey,
                $"appearance frame {index + 1} independently decodes as " +
                $"map-57 NPC {wanted.ObjectId} at its exact upper-lobby " +
                "placement");
            offset = checked(offset + length);
        }

        Check.True(
            offset == stream.Length &&
            PacketBuilder.CountCityNpcSpawnPackets(stream) == expected.Length,
            "the upper-lobby stream ends exactly after four opcode-10020 " +
            "frames");
        return Task.CompletedTask;
    }

    private static void CheckCollisionSafety(
        IReadOnlyList<NpcSpawnDefinition> arena)
    {
        Check.True(
            arena.Count == 5 &&
            arena.Select(static npc => npc.ObjectId).Distinct().Count() ==
                arena.Count &&
            arena.All(static npc =>
                npc.ObjectId == npc.InteractionId &&
                !WorldObjectIds.IsReservedForPlayer(npc.ObjectId) &&
                !WorldObjectIds.IsMonster(npc.ObjectId)),
            "all five Arena actors have unique non-player, non-monster " +
            "object IDs");
    }

    private sealed record ExpectedSpawn(
        uint ObjectId,
        string TemplateKey,
        float X,
        float Z);
}
