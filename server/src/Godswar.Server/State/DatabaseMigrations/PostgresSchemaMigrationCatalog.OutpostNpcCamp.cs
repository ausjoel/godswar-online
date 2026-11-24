namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // W23 (the admin 24 Nov: "Athenian cant speak to NPC in delphi forest"). The low word of an NPC's 10020 appearance
    // word carries the NPC's camp in bits 8-9, and the Origin client only lets a player click an NPC of their own
    // camp or a neutral one. The reference capture shows it on every map it covers: Sparta NPCs 0x0011, Athens
    // NPCs 0x0111, NPCs open to both camps 0x0211 (Thebes, Derveni, Megara and the shared capital services).
    // The hand-placed outpost NPCs (npc-placement-v2, 3 Nov), the six north quest givers in Mycenae (3 Nov) and
    // the Aether Sanctum exit transporter (B23-S, 22 Nov) were all stored as 17 = 0x0011 = Sparta only, so an
    // Athenian could see them but never talk to them (no 10067 from any Athenian on these NPCs in any live log).
    // These are our own additions, not original NPCs, on maps both camps use, so they become 529 = 0x0211
    // (both camps). Rows whose camp is deliberate (Pan's Envoy, Troy, Pindus, Parnassus) are not touched.
    // Guarded and idempotent: only rows still at 0 or 17 with the notes those batches wrote; a no-op on a fresh
    // database (no such rows) or where live was already patched by hand. The GM layer reads gm_npc_spawns at
    // startup, after migrations, so the new word is what the first login sees.
    // Undo (data): UPDATE public.gm_npc_spawns SET appearance_type = 17 WHERE updated_by = 'w23-outpost-npc-camp';
    // ROLLBACK to a build without this migration fails closed at start unless the row goes first:
    //   DELETE FROM public.schema_migrations WHERE migration_id = '20261124_245_outpost_npc_camp_neutral';
    private static PostgresSchemaMigration CreateOutpostNpcCampNeutral() => new(
        "20261124_245_outpost_npc_camp_neutral",
        "Open the hand-placed outpost, north quest giver and Sanctum exit NPCs to both camps (0x0211)",
        """
        UPDATE public.gm_npc_spawns
        SET appearance_type = 529,
            updated_at = now(),
            updated_by = 'w23-outpost-npc-camp'
        WHERE appearance_type IN (0, 17)
          AND (
              (map_id IN (6, 7, 10, 12, 14, 15, 20, 21, 22)
               AND (note LIKE 'npc-placement-v2-%' OR note LIKE 'north-givers-20261103 %'))
              OR (map_id = 70
                  AND npc_key = 'Mycenae_All_013'
                  AND note LIKE 'GENERATED 2026-11-22 (B23-S)%'));
        """);
}
