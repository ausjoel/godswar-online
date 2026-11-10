using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>Native attack presentation and explicitly inferred effective ratings.</summary>
internal static class WonderlandCapturedAttackPolicy
{
    // September13 capture reference target (10166 seq60559): these are fixed
    // calibration inputs, never the current target's stats. Native damage does
    // not reveal the external server's exact PA/MA, multipliers or formula.
    private const int ReferencePhysicalDefense = 5379;
    private const int ReferenceMagicDefense = 2920;
    private const int ReferenceAbsorption = 4791;

    public static uint PrimarySkillId(string templateKey) => Evidence(templateKey).Skill;

    // Native Magic.ini range metadata supports party fanout; the solo capture
    // cannot establish simultaneous hits against multiple player targets.
    public static float NativeAreaRadius(string templateKey) => PrimarySkillId(templateKey) switch
    {
        2004 or 2782 or 2803 or 2805 => 5f,
        2021 or 2022 => 6f,
        2178 => 4f,
        2179 or 2192 => 10f,
        _ => 0f
    };

    public static float NativeAreaRadius(WonderlandSpawnPolicy spawn,
        MonsterRuntimeSnapshot snapshot, DateTimeOffset now) =>
        ResolvePrimarySkill(spawn, snapshot, now) == PrimarySkillId(spawn.TemplateKey)
            ? NativeAreaRadius(spawn.TemplateKey) : 0f;

    public static uint ResolvePrimarySkill(WonderlandSpawnPolicy spawn,
        MonsterRuntimeSnapshot snapshot, DateTimeOffset now) =>
        ResolvePrimarySkill(spawn, snapshot.ControlsAt(now));

    public static uint ResolvePrimarySkill(WonderlandSpawnPolicy spawn, HostileStatusControlFlags controls)
    {
        var evidence = LiveEvidence(spawn);
        if (IsSuppressed(evidence, controls)) return 2000u;
        return evidence.Skill;
    }

    public static MonsterCombatProfile ApplyProfile(MonsterCombatProfile original,
        WonderlandSpawnPolicy spawn, HostileStatusControlFlags controls)
    {
        // The user explicitly preserves island3 boss skills and damage balance.
        if (spawn.Role == WonderlandMonsterRole.FlameRooster) return original;
        var evidence = LiveEvidence(spawn);
        if (evidence.Damage is not { } observed) return original;
        var suppressed = IsSuppressed(evidence, controls);
        var magical = !suppressed && evidence.Magical;
        var originalAttack = magical ? original.MagicAttack : original.PhysicalAttack;
        // Some old authored templates populated only the opposite channel.
        // Do not turn that missing rating into a permanent one-damage attack.
        if (originalAttack == 0) originalAttack = Math.Max(original.PhysicalAttack, original.MagicAttack);
        var mitigation = evidence.ReferenceMitigation != 0 && !suppressed ? evidence.ReferenceMitigation :
            (magical ? ReferenceMagicDefense : ReferencePhysicalDefense) + ReferenceAbsorption;
        int effective;
        if (suppressed && !evidence.FallbackDamage.HasValue)
        {
            // No silenced hit was captured for this role. Retain its authored
            // physical fallback, bounded by a physical normal hit if known.
            effective = evidence.Magical ? original.PhysicalAttack :
                Math.Min(original.PhysicalAttack, InferRating(observed, mitigation, originalAttack));
        }
        else
        {
            var damage = suppressed ? evidence.FallbackDamage!.Value : observed;
            effective = InferRating(damage, mitigation, originalAttack);
        }
        return original with
        {
            AttackKind = magical ? MonsterAttackDamageKind.Magical : MonsterAttackDamageKind.Physical,
            PhysicalAttack = magical ? original.PhysicalAttack : effective,
            MagicAttack = magical ? effective : original.MagicAttack
        };
    }

    private static int InferRating(int damage, int mitigation, int originalAttack)
    {
        var rating = checked(damage + mitigation);
        // A one-point hit is floor-censored: only an upper bound is known.
        return damage == 1 ? Math.Min(originalAttack, rating) : rating;
    }

    // Both buff birds use the requested single-target Fireball at moderate
    // range. Preserve original capture evidence separately from live behavior.
    private static AttackEvidence LiveEvidence(WonderlandSpawnPolicy spawn)
    {
        var evidence = Evidence(spawn.TemplateKey);
        return spawn.Role is WonderlandMonsterRole.Petbird or WonderlandMonsterRole.PutridBird
            ? evidence with { Skill = PrimarySkillId("B_normalC_AthensTower_001"), Magical = true }
            : evidence;
    }

    private static bool IsSuppressed(AttackEvidence evidence, HostileStatusControlFlags controls) =>
        evidence.Skill is not (0 or 2000) &&
        (controls & (evidence.Magical ? HostileStatusControlFlags.NonMagicUsing :
            HostileStatusControlFlags.NonTechniqueUsing)) != 0;

    private static AttackEvidence Evidence(string key) => key switch
    {
        "B_boss_xerxer_001" => new(2805, false, 3953),
        "B_normalC_AthensTower_001" => new(2015, true, 1),
        "B_normale_robber_003" => new(2000, false, 1),
        "B_normalg_flamingo_001" => new(2179, true, 24953),
        "B_normalg_famale_001" => new(2000, false, 3380, ReferenceMitigation: 4566 + 3995),
        "B_bosse_dryad_001" => new(2022, true, 6753),
        "B_bosse_gadsguard_007" => new(2803, false, 6753),
        "B_normale_sprider_007" or "B_normale_cyclops_004" or
            "B_normalf_wraith_002" or "B_normalg_wraith_003" or
            "B_normalg_wraith_005" => new(2000, false, 1),
        "B_bosse_flamingo_001" => new(2000, false),
        "B_normale_stymphalianbird_002" or "B_normale_stymphalianbird_005" => new(2782, true, 1),
        "B_bosse_male_001" => new(2178, true, 14453),
        "B_normale_female_001" => new(2076, true, 1),
        "B_normalD_wraith_002" => new(2000, false, 1, ReferenceMitigation: 4566 + 3995),
        "B_bosse_greecewarrior_001" => new(2004, false, 17365, ReferenceMitigation: 4566 + 3995),
        "B_bosse_greecewarrior_002" => new(2004, false, 15993, 4813),
        "B_normale_mage006" or "B_normale_mage014" => new(2211, true, 1),
        "B_normale_mage007" or "B_normale_mage017" => new(2211, true, 1, ReferenceMitigation: 2630 + 3995),
        "B_bosse_dragon_014" => new(2022, true, 15153, 4213),
        "B_normale_stub_003" => new(0, false),
        "B_bosse_dracoladon_003" => new(2021, true, 15153, 4213),
        "B_normale_stymphalianbird_003" or "B_normale_wraith_001" => new(2000, false, 1),
        "B_bosse_bull_001" => new(2803, false, 12353, 2213),
        "B_bosse_pan_002" => new(2178, true, 15153, 4213),
        "B_bossf_dracoladon_003" => new(2021, true, 15853, 4713),
        "B_bosse_kingofscorpion_01" => new(2192, false, 22153, 9213),
        "B_normalf_wraith_001" => new(2179, true, 25653),
        "B_normale_fairy_004" => new(2011, true, 1),
        // Unobserved attacks keep generic presentation and their prior ratings.
        _ => new(2000, false)
    };

    private readonly record struct AttackEvidence(uint Skill, bool Magical,
        int? Damage = null, int? FallbackDamage = null, int ReferenceMitigation = 0);
}
