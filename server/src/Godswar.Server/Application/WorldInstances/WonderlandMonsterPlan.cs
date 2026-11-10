using System.Collections.Immutable;
using Godswar.Server.Domain.Characters;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>Captured Fane HP/levels with authored combat stats; IDs never repeat between islands.</summary>
internal static class WonderlandMonsterPlan
{
    public const uint FirstObjectId = 46_000;
    private const float ArrowTowerAttackRange = 25f;
    private const float BuffBirdAttackRange = 12f;

    public static ImmutableArray<WonderlandSpawnPolicy> Create(int island, int partySize, byte partyCamp)
    {
        WonderlandEncounterPolicy.ValidatePartySize(partySize);
        if (partyCamp is not (FactionPortalSkillPolicy.SpartaCamp or FactionPortalSkillPolicy.AthensCamp))
            throw new ArgumentOutOfRangeException(nameof(partyCamp));
        var geometry = WonderlandTerrainPolicy.GetIsland(island);
        var result = ImmutableArray.CreateBuilder<WonderlandSpawnPolicy>();
        void Add(string key, WonderlandMonsterRole role, uint hp, int pa, int ma, int pd, int md,
            bool boss = false, bool required = false, byte? camp = null, bool magic = false,
            bool stationary = false, float range = 3f, double interval = 1.92, int dodge = 2000,
            int? capturedLevel = null)
        {
            var allied = camp.HasValue && camp == partyCamp;
            // September 13 external 10020 fields +12/+24 pin levels and HP.
            // The user explicitly requires identical HP for every party size.
            var level = capturedLevel ?? (boss || stationary || role == WonderlandMonsterRole.ChestGuard ? 200 : 120);
            var stats = new WonderlandCombatStats(hp,
                level, pa, ma, pd, md, 3500, dodge, magic,
                magic && !stationary ? 5f : range, TimeSpan.FromSeconds(interval));
            result.Add(new(FirstObjectId + checked((uint)(island * 100 + result.Count)), island,
                key, role, boss, required && !allied, allied, camp,
                geometry.SpawnPositions[result.Count], stats, stationary,
                role is WonderlandMonsterRole.Petbird or WonderlandMonsterRole.PutridBird
                    ? BuffBirdAttackRange
                    : stationary ? range : Math.Max(stats.AttackRange, 24f),
                stationary ? range + 2f : 64f));
        }
        switch (island)
        {
            case 1:
                // Base ratings remain available to supporting mechanics.
                // WonderlandCapturedAttackPolicy separately calibrates ordinary
                // attacks against the complete capture's reference target.
                Add("B_boss_xerxer_001", WonderlandMonsterRole.AlphaDemon, 8_000_000, 15147, 6000, 3000, 2500,
                    true, true, interval: 1.92);
                for (var i = 0; i < 4; i++)
                    Add("B_normalC_AthensTower_001", WonderlandMonsterRole.ArrowTower, 2_000_000, 10147, 0, 3000, 2500,
                        stationary: true, range: ArrowTowerAttackRange);
                for (var i = 0; i < 8; i++)
                    Add("B_normale_robber_003", WonderlandMonsterRole.DemonicStooge, 100_000, 8000, 0, 2500, 2500);
                for (var i = 0; i < 2; i++)
                    Add("B_normalg_flamingo_001", WonderlandMonsterRole.DemonicRaider, 100_000, 0, 34211, 3000, 3000,
                        magic: true, interval: 1.92);
                for (var i = 0; i < 2; i++)
                    Add("B_normalg_famale_001", WonderlandMonsterRole.DemonicAssaulter, 200_000, 10000, 0, 3000, 3000);
                break;
            case 2:
                Add("B_bosse_dryad_001", WonderlandMonsterRole.Derskey, 4_500_000, 14000, 4000, 4000, 2500, true, true);
                Add("B_bosse_gadsguard_007", WonderlandMonsterRole.Monkeyface, 4_500_000, 4000, 14000, 2500, 4000,
                    true, true, magic: true);
                for (var i = 0; i < 4; i++)
                    Add("B_normale_sprider_007", WonderlandMonsterRole.Wolfspider, 100_000, 8000, 8000, 2500, 2500);
                for (var i = 0; i < 4; i++)
                    Add("B_normale_cyclops_004", WonderlandMonsterRole.Troll, 500_000, 8000, 8000, 2500, 2500);
                for (var i = 0; i < 4; i++)
                    Add("B_normalf_wraith_002", WonderlandMonsterRole.Spirit, 100_000, 8000, 8000, 2500, 2500);
                foreach (var key in new[] { "B_normalg_wraith_003", "B_normalg_wraith_005" })
                    for (var i = 0; i < 3; i++)
                        Add(key, WonderlandMonsterRole.Spirit, 100_000, 8000, 8000, 2500, 2500);
                break;
            case 3:
                Add("B_bosse_flamingo_001", WonderlandMonsterRole.FlameRooster, 100_000_000, 5000, 18000, 4500, 4500,
                    true, true, magic: true, interval: 2);
                foreach (var key in new[] { "B_normale_stymphalianbird_002", "B_normale_stymphalianbird_005" })
                    for (var i = 0; i < 12; i++)
                        Add(key, WonderlandMonsterRole.Petbird, 5_000_000, 4000, 4000, 2000, 2000,
                            range: BuffBirdAttackRange);
                Add("B_normalC_AthensTower_001", WonderlandMonsterRole.ArrowTower, 2_000_000, 10147, 0, 3000, 2500,
                    stationary: true, range: ArrowTowerAttackRange);
                break;
            case 4:
                Add("B_bosse_male_001", WonderlandMonsterRole.RockSpirit, 9_000_000, 20000, 5000, 7000, 5500, true, true);
                for (var i = 0; i < 6; i++)
                    Add("B_normale_female_001", WonderlandMonsterRole.StoneGuardian, 150_000, 10000, 10000, 3000, 3000);
                for (var i = 0; i < 3; i++)
                    Add("B_normalD_wraith_002", WonderlandMonsterRole.ManaDefSpoiler, 100_000, 10000, 10000, 3000, 3000,
                        magic: true);
                break;
            case 5:
                // The friendly marshal must be close enough to help a normal
                // lure, without requiring the two models to almost overlap.
                Add("B_bosse_greecewarrior_001", WonderlandMonsterRole.AthenianMarshal,
                    partyCamp == FactionPortalSkillPolicy.AthensCamp ? 8_500_000u : 42_500_000u, 18000, 8000,
                    10000, 8000, true, true, FactionPortalSkillPolicy.AthensCamp,
                    range: partyCamp == FactionPortalSkillPolicy.AthensCamp ? 12f : 3f);
                Add("B_bosse_greecewarrior_002", WonderlandMonsterRole.SpartanMarshal,
                    partyCamp == FactionPortalSkillPolicy.SpartaCamp ? 8_500_000u : 42_500_000u, 18000, 8000,
                    10000, 8000, true, true, FactionPortalSkillPolicy.SpartaCamp,
                    range: partyCamp == FactionPortalSkillPolicy.SpartaCamp ? 12f : 3f);
                for (var i = 0; i < 12; i++)
                {
                    var camp = i < 6 ? FactionPortalSkillPolicy.AthensCamp : FactionPortalSkillPolicy.SpartaCamp;
                    var mage = i % 6 < 3;
                    var key = camp == FactionPortalSkillPolicy.AthensCamp
                        ? mage ? "B_normale_mage007" : "B_normale_mage017"
                        : mage ? "B_normale_mage006" : "B_normale_mage014";
                    Add(key, WonderlandMonsterRole.ExpeditionSoldier, 100_000, 10000, 10000, 4000, 4000,
                        camp: camp, magic: mage);
                }
                break;
            case 6:
                Add("B_bosse_dragon_014", WonderlandMonsterRole.PlatinumDragon, 10_000_000, 18000, 22000,
                    8500, 8500, true, true);
                for (var i = 0; i < 3; i++)
                    Add("B_normale_stub_003", WonderlandMonsterRole.FiringHoop, 1_000_000, 0, 0, 2500, 2500,
                        stationary: true, range: 0, capturedLevel: 120);
                break;
            case 7:
                Add("B_bosse_dracoladon_003", WonderlandMonsterRole.MultiHead, 10_000_000, 24000, 12000,
                    8000, 7000, true, true, dodge: 18000);
                for (var i = 0; i < 5; i++)
                    Add("B_normale_stymphalianbird_003", WonderlandMonsterRole.PutridBird, 500_000, 4000, 4000, 2000, 2000,
                        range: BuffBirdAttackRange);
                for (var i = 0; i < 2; i++)
                    Add("B_norale_Tower_001", WonderlandMonsterRole.LostTower, 2_000_000, 26000, 0, 4000, 4000,
                        stationary: true, range: 30, interval: 1.5);
                for (var i = 0; i < 3; i++)
                    Add("B_normale_wraith_001", WonderlandMonsterRole.Spirit, 500_000, 8000, 8000, 2500, 2500);
                break;
            case 8:
                Add("B_bosse_bull_001", WonderlandMonsterRole.Minotaur, 5_000_000, 24000, 6000, 11000, 8000, true, true);
                Add("B_bosse_pan_002", WonderlandMonsterRole.Deer, 5_000_000, 6000, 26000, 7000, 10000,
                    true, true, magic: true);
                Add("B_bossf_dracoladon_003", WonderlandMonsterRole.DragonKing, 8_500_000, 22000, 26000,
                    9000, 9000, true, true, magic: true);
                Add("B_bosse_kingofscorpion_01", WonderlandMonsterRole.ScorpionKing, 8_000_000, 28000, 10000,
                    10500, 9500, true, true);
                for (var i = 0; i < 8; i++)
                    Add("B_normalf_wraith_001", WonderlandMonsterRole.Atlas, 100_000, 0, 16000, 3000, 3000,
                        magic: true, interval: 3.84);
                Add("B_normale_fairy_004", WonderlandMonsterRole.ChestGuard, 50_000, 0, 16000, 3000, 3000,
                    required: true, magic: true);
                break;
        }
        if (result.Count != geometry.SpawnPositions.Length)
            throw new InvalidOperationException("Wonderland roster does not match its validated placement slots.");
        return result.ToImmutable();
    }
}
