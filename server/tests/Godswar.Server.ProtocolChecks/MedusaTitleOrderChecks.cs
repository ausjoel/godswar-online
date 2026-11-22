using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// MEDUSA-TITLES (owner's call 2026-11-22): fastest Advanced clear = Medusa Executioners (<= 10 min, +3%), then
// Slayers (<= 15 min, +2%), then Challengers (<= 20 min, +1%). Migration 117 seeded the old order and must stay
// byte-for-byte as it was (its checksum is in every database that ran it); migration 244 swaps the order, but only
// when the whole old seed is still there.
internal static class MedusaTitleOrderChecks
{
    public const string CheckName = "Medusa Advanced title order: Executioners fastest, 117 seed unchanged";

    // SHA-256 of migration 117's SQL as live, the rigs and the public baseline applied it.
    private const string MedusaRewardPolicyChecksum =
        "E5E67CCF0DA35352967B7176CB9527C270C74779FC3E92AF2A051E5EEA6CA1B0";

    public static Task RunAsync()
    {
        var all = PostgresSchemaMigrationCatalog.All.ToList();
        var seed = all.Single(m => m.Id == "20260827_117_medusa_reward_policy");
        Check.Equal(MedusaRewardPolicyChecksum, seed.Checksum,
            "migration 117 (Medusa reward seed) is unchanged; change the title order in a new migration instead");

        var swap = all.Single(m => m.Id == "20261122_244_medusa_title_order");
        Check.True(all.IndexOf(swap) > all.IndexOf(seed), "the title-order swap runs after the seed");
        var sql = swap.Sql;
        bool Has(string text) => sql.Contains(text, StringComparison.Ordinal);

        // Guard: every part of the old seed must be present, or nothing changes.
        Check.True(
            Has("threshold = 600 AND title = 1") &&
            Has("threshold = 1200 AND title = 3") &&
            Has("WHERE title = 1\n          AND physical_attack_basis_points = 300 AND magic_attack_basis_points = 300\n" +
                "          AND physical_defense_basis_points = 300 AND magic_defense_basis_points = 300") &&
            Has("WHERE title = 3\n          AND physical_attack_basis_points = 100 AND magic_attack_basis_points = 100\n" +
                "          AND physical_defense_basis_points = 100 AND magic_defense_basis_points = 100") &&
            Has("THEN") && Has("END IF;"),
            "the swap only runs when both 117 rules and all eight 117 basis points are still in place");

        // Effect: 10 minutes -> tier 3 (Executioners, client 5009) at +3%; 20 minutes -> tier 1 (Challengers,
        // client 5011) at +1%. Slayers (tier 2, 15 minutes, +2%) is not touched.
        Check.True(
            Has("SET title = 3, updated_at = now()\n        WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 600;") &&
            Has("SET title = 1, updated_at = now()\n        WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 1200;"),
            "the 10-minute Advanced rule gives Executioners and the 20-minute rule gives Challengers");
        Check.True(
            Has("physical_attack_basis_points = 100, magic_attack_basis_points = 100,\n" +
                "            physical_defense_basis_points = 100, magic_defense_basis_points = 100,\n" +
                "            updated_at = now()\n        WHERE title = 1;") &&
            Has("physical_attack_basis_points = 300, magic_attack_basis_points = 300,\n" +
                "            physical_defense_basis_points = 300, magic_defense_basis_points = 300,\n" +
                "            updated_at = now()\n        WHERE title = 3;"),
            "Executioners (tier 3) gets +3% and Challengers (tier 1) gets +1%");
        Check.True(!Has("threshold = 900") && !Has("title = 2"), "Slayers (15 minutes, +2%) is left alone");
        Check.True(!Has("client_title_id"), "client title ids (5011 / 5010 / 5009) do not move");
        return Task.CompletedTask;
    }
}
