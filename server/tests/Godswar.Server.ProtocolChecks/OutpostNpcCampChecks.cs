using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W23 (the admin 24 Nov: "Athenian cant speak to NPC in delphi forest"). Bits 8-9 of the NPC appearance word are the
// NPC's camp (0x0011 Sparta, 0x0111 Athens, 0x0211 both) and the client refuses a click on the other camp's NPC.
// The Delphi Forest (and every other hand-placed outpost) NPC was stored as 17 = Sparta only.
internal static class OutpostNpcCampChecks
{
    public const string CheckName = "W23 outpost NPCs: open to both camps (appearance 0x0211)";

    private const string MigrationId = "20261124_245_outpost_npc_camp_neutral";

    public static Task RunAsync()
    {
        CaptureCampBits();
        PlacementWord();
        Migration();
        AllFactionsSwitch();
        return Task.CompletedTask;
    }

    // The model the fix rests on, read from the reference capture itself.
    private static void CaptureCampBits()
    {
        Check.True(CapturedNpcPlacements.TryFind("Athens_074", out var athens) && athens.AppearanceType == 0x0111,
            "capture: an Athens city NPC is 0x0111 (camp 1 in bits 8-9)");
        Check.True(CapturedNpcPlacements.TryFind("Sparta_074", out var sparta) && sparta.AppearanceType == 0x0011,
            "capture: a Sparta city NPC is 0x0011 (camp 0)");
        Check.True(CapturedNpcPlacements.TryFind("Athens_141", out var both) && both.AppearanceType == 0x0211,
            "capture: Athens_141 (opened by Athenians on live) is 0x0211 (both camps)");
    }

    private static void PlacementWord()
    {
        Check.Equal(0x0211u, GmNpcDialoguePolicy.EffectiveAppearanceType(0), "a GM row left at 0 is open to both camps");
        Check.Equal(17u, GmNpcDialoguePolicy.EffectiveAppearanceType(17), "an explicit Sparta word is kept");
        Check.Equal(273u, GmNpcDialoguePolicy.EffectiveAppearanceType(273), "an explicit Athens word is kept");
        Check.Equal(529u, GmNpcDialoguePolicy.EffectiveAppearanceType(529), "an explicit both-camps word is kept");

        // The five Delphi Forest NPCs as live stores them after the migration (529), plus one left at 0.
        var rows = new List<GmNpcSpawnOverride>
        {
            new(9, "Delphi Forest - [Warehouse] Belen", 20, "Athens_100", "Athens_100_FemMale5", null, 5238, 529, 132f, -196f, 1f, true),
            new(12, "Delphi Forest - Churchy Female Priest", 20, "Olympia_All_021", "Olympia_All_021_FemSage1", null, 65418, 529, 134.5f, -202f, 1f, true),
            new(19, "Delphi Forest - [Props] Anemone", 20, "Athens_021", "Athens_021_FemMale3", null, 5161, 529, 125.5f, -196f, 1f, true),
            new(20, "Delphi Forest - Origin Enhancer", 20, "Athens_143", "Athens_143_Hallo", null, 5281, 529, 129.5f, -202f, 1f, true),
            new(21, "Delphi Forest - Warehouse Manager", 20, "Athens_134", "Athens_134_Male25", null, 5272, 0, 129.5f, -190f, 1f, true),
        };
        var placed = GmNpcDialoguePolicy.BuildPlacements(GmNpcOverrideSnapshot.Create(rows, []), 20, [], log: false);
        Check.Equal(5, placed.Count, "Delphi Forest: all five GM NPCs placed (map scene key fallback)");
        Check.True(placed.All(static npc => npc.AppearanceType == 0x0211), "Delphi Forest: every placement carries 0x0211");

        // What the client receives: 10020, word = map << 16 | flags, camp byte 2.
        var stream = PacketBuilder.NpcSpawns(placed);
        Check.Equal(placed.Count * 108, stream.Length, "one 108-byte 10020 per NPC");
        for (var i = 0; i < placed.Count; i++)
        {
            var frame = stream.AsSpan(i * 108, 108);
            Check.Equal((ushort)0x2724, BinaryPrimitives.ReadUInt16LittleEndian(frame[2..4]), "frame is 10020");
            Check.Equal(0x00140211u, BinaryPrimitives.ReadUInt32LittleEndian(frame[4..8]),
                $"10020 word for {placed[i].NpcKey} on Delphi Forest is 0x00140211 (both camps)");
        }
    }

    private static void Migration()
    {
        var all = PostgresSchemaMigrationCatalog.All.ToList();
        var migration = all.Single(m => m.Id == MigrationId);
        Check.True(all.IndexOf(migration) > all.FindIndex(m => m.Id == "20261122_244_medusa_title_order"),
            "the camp fix runs after 244");
        var sql = migration.Sql;
        bool Has(string text) => sql.Contains(text, StringComparison.Ordinal);
        Check.True(Has("UPDATE public.gm_npc_spawns") && Has("SET appearance_type = 529,"),
            "the migration sets the both-camps word 529 (0x0211)");
        Check.True(Has("WHERE appearance_type IN (0, 17)"), "guard: only rows still at 0 or Sparta-only 17 change");
        Check.True(Has("map_id IN (6, 7, 10, 12, 14, 15, 20, 21, 22)"),
            "the outpost maps, Delphi Forest (20) among them");
        Check.True(Has("note LIKE 'npc-placement-v2-%'") && Has("note LIKE 'north-givers-20261103 %'") &&
                   Has("note LIKE 'GENERATED 2026-11-22 (B23-S)%'"),
            "guard: only the npc-placement-v2, north-givers and Sanctum exit batches");
        foreach (var deliberate in new[] { "Troy", "WarField", "Parnassus", "Execrativelys", "DELETE", "INSERT" })
        {
            Check.True(!Has(deliberate), $"leaves {deliberate} alone");
        }
    }

    // W24 (the admin d148): with npc.all_factions on, every NPC word in a stream is sent as both camps, except Wonderland's
    // per-camp island chests and anything that is not an NPC word (low byte 0x11).
    private static void AllFactionsSwitch()
    {
        var def = Godswar.Server.Application.Finalise2.Finalise2Rates.Definitions
            .SingleOrDefault(static d => d.Key == "npc.all_factions");
        Check.True(def is not null && def.DefaultValue == 1m, "W24: npc.all_factions exists and defaults to on");
        static byte[] Frame(uint word)
        {
            var f = new byte[108];
            BinaryPrimitives.WriteUInt16LittleEndian(f, 108);
            BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), 0x2724);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), word);
            return f;
        }
        uint[] words = [0x000C0011u, 0x00000111u, 0x000E0211u, 0x00CF0011u, 0x002C0001u, 0x00440002u, 0x00260002u, 0x00260001u];
        uint[] want = [0x000C0211u, 0x00000211u, 0x000E0211u, 0x00CF0011u, 0x002C0211u, 0x00440211u, 0x00260002u, 0x00260001u];
        var stream = words.SelectMany(Frame).ToArray();
        var changed = PacketBuilder.OpenNpcWordsToAllFactions(stream);
        Check.Equal(4, changed, "W24: Parnitha Sparta word, Athens city word, Troy and Parnassus sex-only words opened; Pindus kept");
        for (var i = 0; i < words.Length; i++)
            Check.Equal(want[i], BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(i * 108 + 4, 4)),
                $"W24: word 0x{words[i]:X8} -> 0x{want[i]:X8} (Wonderland 207 and Pindus 38 unchanged)");

        // W25: a camp transporter keeps its camp (Athens_041 is the Athens city Transporter), so the other faction cannot use it.
        Check.True(PacketBuilder.IsTransporterNpcKey("Athens_041") && PacketBuilder.IsTransporterNpcKey("Agate_004"),
            "W25: Athens_041 and Agate_004 are transporters");
        Check.True(!PacketBuilder.IsTransporterNpcKey("Athens_021"), "W25: a props vendor is not a transporter");
        var t = Frame(0x00010111u);
        System.Text.Encoding.ASCII.GetBytes("Athens_041_FemMale12").CopyTo(t.AsSpan(44));
        Check.Equal(0, PacketBuilder.OpenNpcWordsToAllFactions(t), "W25: the Athens Transporter word is not opened");
        Check.Equal(0x00010111u, BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4, 4)), "W25: Athens Transporter stays Athens-only");
    }
}
