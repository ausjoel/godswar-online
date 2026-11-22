using Godswar.Server.Application.Combat;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// B22 B23-F (the admin d86/d87): the class field skills (Earthsplitter 100-104, Storm of Spears 360-364, Holy Pyre
/// 870-874). The family predicate, the hot PvP share combat.field_pvp_percent (default 50) and the book drop rule
/// (a Flame Blast book of rank N brings the killer's own class book of rank N, Mages get nothing extra).
/// The field itself and the PvP pass were proven on the rig (Development/batch-2026-11-22/B23-F/evidence).
/// </summary>
internal static class ClassFieldSkillChecks
{
    public const string CheckName = "B22 B23-F class field skills, PvP share and book drops";

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckFamilies();
            CheckPvpShare();
            CheckBooks();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckFamilies()
    {
        Check.Equal(15, ClassFieldSkills.SkillIds.Count, "15 class field skills (3 classes x 5 ranks)");
        foreach (var id in new[] { 100, 104, 360, 364, 870, 874 })
        {
            Check.True(ClassFieldSkills.IsField(id), $"{id} is a class field");
        }
        foreach (var id in new[] { 99, 105, 359, 365, 869, 875, 570, 574, 579, 40, 860 })
        {
            Check.True(!ClassFieldSkills.IsField(id), $"{id} is not a class field (Flame Blast stays the Mage's own)");
        }
        Check.True(ClassFieldSkills.TryGetFamily(102, out var w, out var wn) && w == 0 && wn == "Earthsplitter",
            "100-104 = Warrior Earthsplitter");
        Check.True(ClassFieldSkills.TryGetFamily(363, out var c, out var cn) && c == 1 && cn == "Storm of Spears",
            "360-364 = Champion Storm of Spears");
        Check.True(ClassFieldSkills.TryGetFamily(870, out var p, out var pn) && p == 2 && pn == "Holy Pyre",
            "870-874 = Priest Holy Pyre");
    }

    private static void CheckPvpShare()
    {
        var defs = ServerRateCatalog.Definitions.ToDictionary(static d => d.Key, StringComparer.Ordinal);
        Check.True(defs.TryGetValue(ClassFieldSkills.PvpPercentKey, out var pvp) && pvp.DefaultValue == 50m &&
                   pvp.MinimumValue == 0m && pvp.MaximumValue == 100m,
            "combat.field_pvp_percent is registered, default 50, range 0-100");
        Check.True(defs.TryGetValue(ClassFieldBookLootRule.SwitchKey, out var books) && books.DefaultValue == 1m,
            "drop.class_field_books is registered, default on");
        Check.Equal(50m, ClassFieldSkills.PvpPercent(), "default share on players is 50%");

        var hit = new CombatResolution(5, 1, 0, CombatDamageChannel.Physical, CombatHitOutcome.Normal, 411,
            new(9_000, 100, 0, CombatRollEvidence.NotRolled), default);
        Check.Equal(206u, ClassFieldSkills.ScaleForPlayers(50m, hit).Damage, "50%: 411 -> 206 (rounded half up)");
        Check.Equal(411u, ClassFieldSkills.ScaleForPlayers(100m, hit).Damage, "100%: full damage");
        Check.Equal(1u, ClassFieldSkills.ScaleForPlayers(5m, hit with { Damage = 3 }).Damage,
            "a landed hit on a player never drops below 1");
        var miss = hit with { Outcome = CombatHitOutcome.Miss, Damage = 0 };
        Check.Equal(miss, ClassFieldSkills.ScaleForPlayers(50m, miss), "a miss stays a miss");

        ServerRates.Install(ServerRatesSnapshot.Create(
            [new(ClassFieldSkills.PvpPercentKey, 0m)], 2, DateTimeOffset.UtcNow, "check", "check"));
        Check.Equal(0m, ClassFieldSkills.PvpPercent(), "slider 0 = monsters only (the PvP pass is skipped)");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [new(ClassFieldSkills.PvpPercentKey, 250m)], 3, DateTimeOffset.UtcNow, "check", "check"));
        Check.True(ClassFieldSkills.PvpPercent() <= 100m, "the share is never above 100%");
        ServerRates.ResetToDefaults();
    }

    private static void CheckBooks()
    {
        Check.Equal(6540u, ClassFieldBookLootRule.BookItemId(0, 1), "Earthsplitter 1 book = 6540");
        Check.Equal(6549u, ClassFieldBookLootRule.BookItemId(1, 5), "Storm of Spears 5 book = 6549");
        Check.Equal(6552u, ClassFieldBookLootRule.BookItemId(2, 3), "Holy Pyre 3 book = 6552");
        Check.Equal(0u, ClassFieldBookLootRule.BookItemId(3, 1), "no Mage class-field book");

        var rates = ServerRates.Current;
        IReadOnlyList<RolledMonsterLoot> table =
        [
            new(0, 4000, 1, null, ChanceBasisPoints: 5_000),
            new(5, 5429, 1, null, ChanceBasisPoints: 12), // Flame Blast 3 book
        ];
        var warrior = ClassFieldBookLootRule.Add(table, table, 0, rates);
        Check.True(warrior.Count == 3 && warrior[2].ItemId == 6542 && warrior[2].ChanceBasisPoints == 12 &&
                   warrior[2].LootIndex is not (0 or 5),
            "a Warrior killer gets Earthsplitter 3 with the Flame Blast book's rarity, in a free loot index");
        Check.True(ClassFieldBookLootRule.Add(table, table, 2, rates).Last().ItemId == 6552,
            "a Priest killer gets Holy Pyre 3");
        Check.Equal(2, ClassFieldBookLootRule.Add(table, table, 3, rates).Count, "a Mage killer gets nothing extra");
        IReadOnlyList<RolledMonsterLoot> plain = [new(0, 4000, 1, null)];
        Check.Equal(1, ClassFieldBookLootRule.Add(plain, plain, 1, rates).Count, "no Flame Blast book, no field book");
        var off = ServerRatesSnapshot.Create([new(ClassFieldBookLootRule.SwitchKey, 0m)], 4, DateTimeOffset.UtcNow,
            "check", "check");
        Check.Equal(2, ClassFieldBookLootRule.Add(table, table, 0, off).Count, "drop.class_field_books 0 = off");
    }
}
