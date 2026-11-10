using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    private static void CheckCapturedFullRunHealth()
    {
        // Literal 10020 prefixes from external-full.log SHA256
        // 81EA111739F43E90319B64B777FE6A0F1D4CFEEB9E2DE34997692AAAD666DF44.
        // Latest complete run60454..95729; two allied faction templates use
        // earlier September13 appearances from the same immutable capture.
        (string Template, string Prefix)[] captured =
        [
            ("B_boss_xerxer_001", "680024271202CF00FA570000C80000000000000000127A0000127A00"), // capture seq 60773
            ("B_bosse_bull_001", "680024271202CF00DA510000C800000000000000404B4C00404B4C00"), // capture seq 86649
            ("B_bosse_dracoladon_003", "680024271202CF00E8510000C8000000000000008096980080969800"), // capture seq 81959
            ("B_bosse_dragon_014", "680024271202CF0004520000C8000000000000008096980080969800"), // capture seq 79142
            ("B_bosse_dryad_001", "680024271202CF00E0530000C80000000000000020AA440020AA4400"), // capture seq 63591
            ("B_bosse_flamingo_001", "680024271202CF0008580000C80000000000000000E1F50500E1F505"), // capture seq 66319
            ("B_bosse_gadsguard_007", "680024271202CF0096540000C80000000000000020AA440020AA4400"), // capture seq 63862
            ("B_bosse_greecewarrior_001", "680024271201CF0020520000C80000000000000020B3810020B38100"), // capture seq 37092
            ("B_bosse_greecewarrior_002", "680024271200CF002E520000C80000000000000020B3810020B38100"), // capture seq 75415
            ("B_bosse_kingofscorpion_01", "680024271202CF00A4540000C80000000000000000127A0000127A00"), // capture seq 92559
            ("B_bosse_male_001", "680024271202CF00B2540000C8000000000000004054890040548900"), // capture seq 71723
            ("B_bosse_pan_002", "680024271202CF00F6510000C800000000000000404B4C00404B4C00"), // capture seq 88436
            ("B_bossf_dracoladon_003", "680024271202CF00D2530000C80000000000000020B3810020B38100"), // capture seq 90280
            ("B_norale_Tower_001", "680024271202CF0062530000C80000000000000080841E0080841E00"), // capture seq 81966
            ("B_normalC_AthensTower_001", "680024271202CF0086510000C80000000000000080841E0080841E00"), // capture seq 60649
            ("B_normalD_wraith_002", "680024271202CF00165800007800000000000000A0860100A0860100"), // capture seq 71717
            ("B_normale_cyclops_004", "680024271202CF00A8530000780000000000000020A1070020A10700"), // capture seq 63555
            ("B_normale_fairy_004", "680024271202CF0012520000C80000000000000050C3000050C30000"), // capture seq 95548
            ("B_normale_female_001", "680024271202CF006C5400007800000000000000F0490200F0490200"), // capture seq 71473
            ("B_normale_mage006", "680024271200CF00585200007800000000000000A0860100A0860100"), // capture seq 75416
            ("B_normale_mage007", "680024271201CF00745200007800000000000000A0860100A0860100"), // capture seq 37083
            ("B_normale_mage014", "680024271200CF00AC5200007800000000000000A0860100A0860100"), // capture seq 75391
            ("B_normale_mage017", "680024271201CF00BA5200007800000000000000A0860100A0860100"), // capture seq 75398
            ("B_normale_robber_003", "680024271202CF00145500007800000000000000A0860100A0860100"), // capture seq 60743
            ("B_normale_sprider_007", "680024271202CF003E5500007800000000000000A0860100A0860100"), // capture seq 63554
            ("B_normale_stub_003", "680024271202CF0092550000780000000000000040420F0040420F00"), // capture seq 78998
            ("B_normale_stymphalianbird_002", "680024271202CF00025600007800000000000000404B4C00404B4C00"), // capture seq 66251
            ("B_normale_stymphalianbird_003", "680024271202CF00E4520000780000000000000020A1070020A10700"), // capture seq 81960
            ("B_normale_stymphalianbird_005", "680024271202CF00AA5600007800000000000000404B4C00404B4C00"), // capture seq 66246
            ("B_normale_wraith_001", "680024271202CF0070530000780000000000000020A1070020A10700"), // capture seq 81967
            ("B_normalf_wraith_001", "680024271202CF00605700007800000000000000A0860100A0860100"), // capture seq 86634
            ("B_normalf_wraith_002", "680024271202CF00985700007800000000000000A0860100A0860100"), // capture seq 63564
            ("B_normalg_famale_001", "680024271202CF00EE5300007800000000000000400D0300400D0300"), // capture seq 60836
            ("B_normalg_flamingo_001", "680024271202CF00885400007800000000000000A0860100A0860100"), // capture seq 60757
            ("B_normalg_wraith_003", "680024271202CF00B45700007800000000000000A0860100A0860100"), // capture seq 63436
            ("B_normalg_wraith_005", "680024271202CF00D05700007800000000000000A0860100A0860100"), // capture seq 63438
        ];
        foreach (var camp in new byte[] { 0, 1 })
        foreach (var size in Enumerable.Range(1, 5))
        {
            var plan = Enumerable.Range(1, 8)
                .SelectMany(stage => WonderlandMonsterPlan.Create(stage, size, camp)).ToArray();
            Check.True(plan.Select(p => p.TemplateKey).Distinct().Count() == captured.Length,
                "every planned native template has independent captured HP evidence");
            foreach (var (template, prefix) in captured)
            {
                var packet = Convert.FromHexString(prefix);
                var expectedHp = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24));
                var expectedLevel = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
                var matching = plan.Where(p => p.TemplateKey == template).ToArray();
                Check.True(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10020 &&
                    expectedHp > 0 && matching.Length > 0 && matching.All(p =>
                        p.Stats.MaximumHealth == expectedHp * (p.Stage == 5 && p.IsBoss && !p.IsAllied ? 5u : 1u) &&
                        p.Stats.Level == expectedLevel),
                    $"{template} retains captured HP/level with the requested enemy-marshal HP increase for party size {size} and faction {camp}");
            }
            var hoops = plan.Where(p => p.Role == WonderlandMonsterRole.FiringHoop).ToArray();
            Check.True(hoops.Length == 3 && hoops.All(p => p.Stationary &&
                !p.RequiredForProgression && p.AggroRadius == 0 && p.Stats.AttackRange == 0),
                "captured stationary Firing Hoops do not acquire ordinary attack targets");
        }
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandMonsterPlan.Create(1, 0, 0),
            "empty party is still rejected after removing HP scaling");
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandMonsterPlan.Create(1, 6, 0),
            "oversized party is still rejected after removing HP scaling");
    }
}
