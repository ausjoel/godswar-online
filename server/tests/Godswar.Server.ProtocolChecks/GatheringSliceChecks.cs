using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// PROF slice 2 (2026-11-09): the gathering messages 10342/10344/10345 read from Origin.exe (0x658b70, 0x658270,
// 0x658620, 0x4e094b), the CollNpc.ini node table, the Romance Island actors and the Type 1 cooldown rules.
// docs/professions/README.md section 6.
internal static class GatheringSliceChecks
{
    public const string PacketCheckName = "PROF slice 2 gathering messages 10342/10344/10345 have the client's layouts";
    public const string RulesCheckName = "PROF slice 2 CollNpc nodes, tools, drops, island actors and recipe cooldowns";

    private static int I32(byte[] packet, int offset) => BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset));

    public static Task RunPacketsAsync()
    {
        var obtained = PacketBuilder.GatherResult(PacketBuilder.GatherObtained, 12010, 3);
        Check.Equal(80, obtained.Length, "10345 length");
        Check.Equal((ushort)80, BinaryPrimitives.ReadUInt16LittleEndian(obtained), "10345 size word");
        Check.Equal((ushort)10345, BinaryPrimitives.ReadUInt16LittleEndian(obtained.AsSpan(2)), "10345 opcode");
        Check.Equal(0, I32(obtained, 4), "10345 result at +4");
        Check.Equal(12010, I32(obtained, 8), "10345 item record id at +8");
        Check.Equal(-1, I32(obtained, 12), "10345 record attribute 1 empty");
        Check.Equal((byte)3, obtained[8 + 27], "10345 count = record[27] (packet +35, the byte the client prints)");
        Check.Equal((byte)99, PacketBuilder.GatherResult(PacketBuilder.GatherObtained, 12010, 500)[35],
            "10345 count never above 99");
        var refused = PacketBuilder.GatherResult(PacketBuilder.GatherNoAp);
        Check.True(refused.Length == 80 && I32(refused, 4) == 4 && I32(refused, 8) == 0, "10345 AP refusal carries no item");
        Check.Equal(-1, I32(PacketBuilder.GatherResult(PacketBuilder.GatherCloseWindow), 4), "10345 close = -1");
        Check.True(PacketBuilder.GatherWrongTool == 1 && PacketBuilder.GatherNothing == 2 &&
            PacketBuilder.GatherBagFull == 3 && PacketBuilder.GatherError == 5 && PacketBuilder.GatherIllegal == 6,
            "10345 result numbers follow the handler's branches");

        // C2S 10344 as 0x658508 builds it: +4 [ui+0x14], +8 [ui+0x84] (CollNpc id), +12 u8 1.
        var request = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 10344);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), 105);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), 7);
        request[12] = 1;
        Check.True(PacketBuilder.TryReadGatherRequest(request, out var parsed) && parsed.ToolWord == 105 &&
            parsed.NodeId == 7 && parsed.Gathering, "10344 parse: tool word, node, gathering");
        request[12] = 0;
        Check.True(PacketBuilder.TryReadGatherRequest(request, out parsed) && !parsed.Gathering, "10344 stop");
        Check.True(!PacketBuilder.TryReadGatherRequest(request.AsSpan(0, 12), out _), "10344 short frame refused");

        var pose = PacketBuilder.GatherPoseFrame(1, 3, 5123);
        Check.True(pose.Length == 16 && BinaryPrimitives.ReadUInt16LittleEndian(pose.AsSpan(2)) == 10342 &&
            I32(pose, 4) == 1 && I32(pose, 8) == 3 && I32(pose, 12) == 5123, "10342 relay: state, node, object");
        Check.True(PacketBuilder.TryReadGatherPose(pose, out var read) && read.State == 1 && read.NodeId == 3 &&
            read.ObjectId == 5123u, "10342 parse");
        return Task.CompletedTask;
    }

    public static Task RunRulesAsync()
    {
        Check.True(Finalise2Rates.On(Finalise2RateKeys.Gathering), "with no rate row profession.gathering reads on");

        // CollNpc.ini: 16 rows, ids 1..16, Actions 0/1/2 by NpcName (Mine Vein / Nifty Garden / Hunting Range).
        Check.Equal(16, GatheringTables.Nodes.Length, "CollNpc.ini has 16 nodes");
        Check.True(GatheringTables.Nodes.Select(static n => n.Id).Order().SequenceEqual(Enumerable.Range(1, 16)),
            "CollNpc ids are 1..16");
        Check.True(GatheringTables.TryGetNode(1, out var mine) && mine.Kind == GatherKind.Mine &&
            mine.TemplateKey == "Love_Island_001_WarField2" && mine.NpcKey == "Love_Island_001", "node 1 = Mine Vein 001");
        Check.True(GatheringTables.TryGetNode(2, out var hunt) && hunt.Kind == GatherKind.Hunting, "node 2 = Hunting Range");
        Check.True(GatheringTables.TryGetNode(3, out var garden) && garden.Kind == GatherKind.Garden, "node 3 = Nifty Garden");
        Check.True(GatheringTables.TryGetNode(4, out var vault) && vault.NpcKey == "Pans_Labyrinth_012" &&
            GatheringTables.WrongToolLine(vault) == 904 && GatheringTables.DescriptionLine(vault) == 104, "node 4 = Pan's vault");

        // Tools (ItemType 52): Hoe / Shovel / Arrow, five tiers each, PlayLv 1/60/90/110/120.
        Check.Equal(0, GatheringTables.ToolTier(GatherKind.Mine, 12110), "Beginner Hoe works on a Mine Vein");
        Check.Equal(4, GatheringTables.ToolTier(GatherKind.Garden, 12124), "Divine Shovel works on a Nifty Garden");
        Check.Equal(2, GatheringTables.ToolTier(GatherKind.Hunting, 12132), "Refined Arrow works on a Hunting Range");
        Check.Equal(-1, GatheringTables.ToolTier(GatherKind.Mine, 12130), "an Arrow is the wrong tool for a mine");
        Check.Equal(-1, GatheringTables.ToolTier(GatherKind.Hunting, 12030), "a Herb is not a tool");
        Check.True(GatheringTables.ToolLevel(0) == 1 && GatheringTables.ToolLevel(1) == 60 &&
            GatheringTables.ToolLevel(4) == 120, "tool PlayLv bounds");
        Check.True(GatheringTables.WrongToolLine(mine) == 901 && GatheringTables.WrongToolLine(hunt) == 902 &&
            GatheringTables.WrongToolLine(garden) == 903, "wrong-tool lines 901-903");

        // Rolls (GENERATED odds): a miss, a common and a rare result; counts stay 1..5, never above 99.
        Check.True(!GatheringTables.Roll(GatherKind.Mine, 0, 70, 0, 0, 0).Success, "tier 0 misses at roll 70 (70%)");
        var common = GatheringTables.Roll(GatherKind.Garden, 0, 0, 0, 0, 0);
        Check.True(common is { Success: true, ItemId: 12030, Count: 1 }, "tier 0 garden: one Herb");
        var rare = GatheringTables.Roll(GatherKind.Hunting, 4, 0, 0, 99, 4);
        Check.True(rare is { Success: true, ItemId: 12050, Count: 5 }, "tier 4 hunting: Fine Fur x5");
        Check.True(GatheringTables.Roll(GatherKind.Mine, 0, 0, 99, 99, 9).ItemId == 12020 &&
            GatheringTables.Roll(GatherKind.Mine, 0, 0, 99, 99, 9).Count == 1, "tier 0 never gets extra or rare");

        // Island actors: every node's template equals its CollNpc Name, which is what the client matches.
        var actors = RomanceIslandProtocol.Npcs();
        Check.Equal(17, actors.Count, "15 nodes and two transporters");
        Check.Equal(17, actors.Select(static a => a.ObjectId).Distinct().Count(), "actor ids are unique");
        foreach (var node in GatheringTables.Nodes.Where(static n => n.Id != 4))
        {
            var actor = actors.SingleOrDefault(a => a.NpcKey == node.NpcKey);
            Check.True(actor is not null && actor.TemplateKey == node.TemplateKey && actor.MapId == 45,
                $"node {node.Id} spawns on map 45 with template {node.TemplateKey}");
        }

        Check.True(RomanceIslandProtocol.AddNpcs(1, []).Count == 0, "no actors outside map 45");
        Check.True(RomanceIslandProtocol.IsEntryClick("Athens_072", 1, 600) &&
            !RomanceIslandProtocol.IsEntryClick("Athens_072", 1, 218), "Event Transporter 600 is the entry");
        Check.True(GatheringTables.InReach(100f, -100f, 108f, -106f) && !GatheringTables.InReach(0f, 0f, 108f, -106f),
            "gather reach");

        // Type 1 cooldowns: only a successful Type 1 make starts its Time; the remaining seconds round up.
        var timed = ProfessionBlueprintCatalog.All.First(static b => b.Type == 1);
        Check.True(ProfessionTables.CooldownAfterMake(timed, success: true) > 0, "a Type 1 success starts its cooldown");
        Check.Equal(0, ProfessionTables.CooldownAfterMake(timed, success: false), "a failed roll starts none");
        Check.True(ProfessionBlueprintCatalog.TryGet(32002, out var typeZero) && typeZero.Type == 0 &&
            ProfessionTables.CooldownAfterMake(typeZero, true) == 0, "the Type 0 row with a Time gets no cooldown");
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        Check.Equal(3600, ProfessionTables.CooldownRemaining(now.AddSeconds(3599.2), now), "remaining rounds up");
        Check.Equal(0, ProfessionTables.CooldownRemaining(now.AddSeconds(-5), now), "an ended cooldown is 0");
        Check.Equal(0, ProfessionTables.CooldownRemaining(null, now), "no cooldown is 0");
        return Task.CompletedTask;
    }
}
