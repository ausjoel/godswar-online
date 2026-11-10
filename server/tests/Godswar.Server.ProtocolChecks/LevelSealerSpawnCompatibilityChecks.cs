using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class LevelSealerSpawnCompatibilityChecks
{
    public static void Run()
    {
        var published = NpcContentBaselineV1.LoadDefinitions();
        var sparta = CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility(
            published.Single(static npc => npc.NpcKey == "Sparta_142"));
        var athens = CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility(
            published.Single(static npc => npc.NpcKey == "Athens_142"));

        Check.True(
            sparta.TemplateKey == "Sparta_142_Hallo" &&
            sparta.ObjectId == 5_139u &&
            sparta.InteractionId == 5_139u &&
            sparta.X == 120.007515f &&
            sparta.Z == -138.16507f &&
            sparta.AppearanceType == 17u &&
            sparta.Facing == 3.071875f &&
            athens.TemplateKey == "Athens_142_Hallo" &&
            athens.ObjectId == 5_281u &&
            athens.InteractionId == 5_281u &&
            athens.X == 120.007515f &&
            athens.Z == -138.16507f &&
            athens.AppearanceType == 65_809u &&
            athens.Facing == 3.071875f,
            "Level Sealer uses the captured visible spawn in both capitals");

        CheckLocalSpawnPacket(sparta);
        CheckLocalSpawnPacket(athens);
        CheckLocalDialogPackets(sparta);
        CheckLocalDialogPackets(athens);

        var unrelated = published.Single(static npc => npc.NpcKey == "Sparta_143");
        Check.True(
            ReferenceEquals(
                unrelated,
                CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility(
                    unrelated)),
            "captured Level Sealer compatibility cannot alter another NPC");
    }

    private static void CheckLocalSpawnPacket(NpcSpawnDefinition npc)
    {
        var packet = PacketBuilder.NpcSpawns([npc]);
        var template = Encoding.ASCII.GetString(packet.AsSpan(44))
            .TrimEnd('\0');
        Check.True(
            packet.Length == 108 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == 108 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                0x2724 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)) ==
                (ushort)(npc.AppearanceType & ushort.MaxValue) &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)) ==
                (ushort)npc.MapId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) ==
                npc.ObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 1u &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24)) == 1_521u &&
            template == npc.TemplateKey &&
            !template.StartsWith("gwprivate_", StringComparison.Ordinal),
            $"{npc.NpcKey} uses the established local NPC spawn convention");
    }

    private static void CheckLocalDialogPackets(NpcSpawnDefinition npc)
    {
        var open = PacketBuilder.NpcDialogOpenAck(
            npc.InteractionId,
            116,
            npc.NpcKey);
        var menu = PacketBuilder.NpcFunctionActionResponse(
            npc.InteractionId,
            116,
            101,
            102,
            103);
        Check.True(
            open.Length == 48 &&
            BinaryPrimitives.ReadInt32LittleEndian(open.AsSpan(8)) == 0x200 &&
            BinaryPrimitives.ReadInt32LittleEndian(open.AsSpan(12)) == 116 &&
            Encoding.ASCII.GetString(open.AsSpan(16, 32)).TrimEnd('\0') ==
                npc.NpcKey &&
            menu.Length == 24 &&
            BinaryPrimitives.ReadInt32LittleEndian(menu.AsSpan(8)) == 116 &&
            BinaryPrimitives.ReadInt32LittleEndian(menu.AsSpan(12)) == 101 &&
            BinaryPrimitives.ReadInt32LittleEndian(menu.AsSpan(16)) == 102 &&
            BinaryPrimitives.ReadInt32LittleEndian(menu.AsSpan(20)) == 103,
            $"{npc.NpcKey} uses the local Level Sealer script and menu");
    }
}
