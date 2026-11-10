using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class NpcContentAuthorityChecks
{
    // Sanitized map-57 opcode-10020 packets from
    // external-20260907-143201-403.log, source header lines 8178 and 9996.
    // Literal capture bytes keep the test independent of the authored layout.
    private static readonly string[] CapturedArenaV7Spawns =
    [
        "68002427110239004E140000C800000000000000D0F80100D0F801000000D8C2" +
        "000000000000A2429A9914406777707269766174655F4172656E615F3030315F" +
        "46656D4D616C6531340000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "68002427110239004D140000C800000000000000D0F80100D0F801000000DCC2" +
        "000000000000CC429A9914406777707269766174655F4172656E615F3030325F" +
        "4D616C6531380000000000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "68002427110239004F140000C800000000000000D0F80100D0F801000000C6C2" +
        "00000000000088429A9914406777707269766174655F4172656E615F3030335F" +
        "4D616C6531380000000000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "680024271102390051140000C800000000000000D0F80100D0F80100000040C2" +
        "00000000000020429A9914406777707269766174655F4172656E615F3030345F" +
        "4D616C6531380000000000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "680024271102390050140000C800000000000000D0F80100D0F801000000BAC2" +
        "000000000000C8429A9914406777707269766174655F4172656E615F3030355F" +
        "7969736869000000000000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "680024271102390053140000C800000000000000D0F80100D0F8010067FAEDC2" +
        "0000000003FEC5429A9914406777707269766174655F4172656E615F3030365F" +
        "41697244726F7000000000000000000000000000000000000000000000000000" +
        "0000000000000000",
        "680024271102390052140000C800000000000000D0F80100D0F80100A801CFC2" +
        "000000008AC7CE429A9914406777707269766174655F4475656C4172656E615F" +
        "3030315F4D616C65330000000000000000000000000000000000000000000000" +
        "0000000000000000"
    ];

    private static void CheckArenaV7Release()
    {
        var previous = NpcContentBaselineV6.LoadDefinitions();
        var definitions = NpcContentBaselineV7.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);
        Check.Equal(390, definitions.Length, "captured Arena V7 total count");
        Check.Equal(390, NpcContentBaselineV7.ExpectedEntryCount,
            "captured Arena V7 declared count");
        Check.Equal(NpcContentBaselineV7.ExpectedRevision, revision.Sha256,
            "captured Arena V7 golden revision");

        var previousOutside = previous.Where(static npc => npc.MapId != 57)
            .ToArray();
        var currentOutside = definitions.Where(static npc => npc.MapId != 57)
            .ToArray();
        Check.Equal(383, currentOutside.Length,
            "captured Arena V7 preserves every non-Arena actor");
        Check.Equal(previousOutside.Length, currentOutside.Length,
            "captured Arena V7 non-Arena count is unchanged");
        for (var index = 0; index < previousOutside.Length; index++)
        {
            CheckDefinitionEqual(previousOutside[index], currentOutside[index],
                $"captured Arena V7 non-Arena actor {index}");
        }

        var arena = definitions.Where(static npc => npc.MapId == 57).ToArray();
        Check.Equal(7, arena.Length, "captured Arena V7 roster count");
        Check.True(arena.Select(static npc => npc.ObjectId).SequenceEqual(
                new uint[] { 5198, 5197, 5199, 5201, 5200, 5203, 5202 }),
            "captured Arena V7 IDs retain canonical NPC-key order");
        Check.True(arena.All(static npc =>
                npc.ObjectId is >= 1500 and < 8000 &&
                npc.InteractionId == npc.ObjectId),
            "captured Arena V7 uses native NPC identities for interaction");
        for (var index = 0; index < CapturedArenaV7Spawns.Length; index++)
        {
            var expected = DecodeCapturedArenaV7Spawn(
                CapturedArenaV7Spawns[index]);
            CheckDefinitionEqual(expected, arena[index],
                $"captured Arena V7 {expected.NpcKey}");
        }

        Check.True(
            DuelArenaCapturedLayout.UpperArrivalX == -104f &&
            DuelArenaCapturedLayout.UpperArrivalZ == 96f &&
            DuelArenaCapturedLayout.LowerArrivalX == -5f &&
            DuelArenaCapturedLayout.LowerArrivalZ == 32f,
            "captured Arena travel arrivals match the observed scene loads");
    }

    private static NpcSpawnDefinition DecodeCapturedArenaV7Spawn(string hex)
    {
        var packet = Convert.FromHexString(hex);
        Check.Equal(104, packet.Length, "external Arena capture frame length");
        Check.Equal((ushort)104,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "external Arena capture declared length");
        Check.Equal((ushort)10020,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "external Arena capture opcode");
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
        var templateBytes = packet.AsSpan(44);
        var wireTemplate = Encoding.ASCII.GetString(
            templateBytes[..templateBytes.IndexOf((byte)0)]);
        const string prefix = "gwprivate_";
        Check.True(wireTemplate.StartsWith(prefix, StringComparison.Ordinal),
            "external Arena capture uses its private client template prefix");
        var template = wireTemplate[prefix.Length..];
        var npcKey = template[..template.LastIndexOf('_')];
        return new NpcSpawnDefinition(
            checked((short)(packed >> 16)),
            "Arena",
            npcKey,
            template,
            objectId,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(28)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(36)),
            objectId,
            packed & 0xffff,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(40)),
            Detail10077: [],
            Detail10080: []);
    }
}
