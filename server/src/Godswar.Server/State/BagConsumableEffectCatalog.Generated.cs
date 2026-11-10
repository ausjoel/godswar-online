using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// What using one bag consumable does once its item skill has been resolved.
/// </summary>
internal enum BagConsumableEffectKind : byte
{
    RestoreHitPoints = 1,
    RestoreManaPoints = 2,
    GrantSilver = 3,
    GrantExperience = 4,
    GrantTimedExperienceBoost = 5
}

/// <summary>
/// One transcribed <c>Magic.ini</c> <c>Power2</c> amount keyed by the item
/// skill that carries it, plus the optional <c>Status.ini</c> status block a
/// <c>ScriptID=StatusMagic</c> skill grants.
/// </summary>
internal readonly record struct BagConsumableEffect(
    int SkillId,
    BagConsumableEffectKind Kind,
    int Amount,
    int StatusId = 0,
    int StatusKind = 0,
    int StatusDurationSeconds = 0,
    int MaxPercentBasisPoints = 0)
{
    // POTIONS (a01/a23, Polish patch): MaxPercentBasisPoints > 0 makes a
    // restoration heal that share of the calculated maximum (2500 = 25%),
    // never more than Amount. 0 = the stock flat Amount.
    public bool IsValid =>
        SkillId > 0 &&
        Enum.IsDefined(Kind) &&
        Amount > 0 &&
        MaxPercentBasisPoints is >= 0 and <= 10_000 &&
        (MaxPercentBasisPoints == 0 ||
            Kind is BagConsumableEffectKind.RestoreHitPoints or
                BagConsumableEffectKind.RestoreManaPoints) &&
        (Kind != BagConsumableEffectKind.GrantTimedExperienceBoost ||
            StatusId > 0 && StatusKind > 0 && StatusDurationSeconds > 0);
}

/// <summary>
/// Stock bag-consumable effects transcribed from the reviewed client files.
/// The two source files are byte-identical to the installed client's copies.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator is not the numeric skill range: it is <c>Magic.ini</c>'s
/// <c>ScriptID</c>. <c>AddHP*</c> is a hit-point restoration, <c>AddMP*</c> a
/// mana restoration, <c>AddMoney*</c> a silver grant, <c>AddEXP*</c> a flat
/// fighter-experience grant, and <c>StatusMagic</c> a timed status whose
/// numbers live only in <c>Status.ini</c> (Magic.ini leaves <c>Power1</c>,
/// <c>Power2</c> and <c>Power3</c> at 0 for those four skills). Every entry
/// below is one reviewed client entry; nothing is inferred from a range.
/// </para>
/// <para>
/// Skills such as 3107, 3126, 4603, 4610-4617, 4640-4645, 4720-4721 and
/// 4800-4804 are deliberately absent: their amounts are zero, they are a
/// different mechanic (status application for a status we have no numbers
/// for), or they have no reviewed client entry at all. An item whose skill is
/// absent here keeps the pre-existing behaviour of being ignored. The eleven
/// <c>StatusMagic</c> boosts that are transcribed — 4807-4809 for the fighter
/// channel and 4752-4756 for the pet channel — resolve here, but the pet
/// potions are also carried by <see cref="PetExperienceBoostPolicy"/>, which
/// owns their tier-replacement rule.
/// </para>
/// </remarks>
internal static class BagConsumableEffectCatalog
{
    // Magic.ini    08AEC7E0453C24ECD3C8C697AFAFA85F6DD812E83C91EC2F145B2A7A73F7C0AF
    // Status.ini   3011C349B2DBEF42C73C19E0453F67B6642B566747F0BBC9042EF59888186C97

    /// <summary>
    /// Transcribed Magic.ini sections. <c>Power2</c> is the amount and
    /// <c>Power1</c>/<c>Power3</c> are zero in every one of these sections.
    /// </summary>
    private static readonly BagConsumableEffect[] Transcribed =
    [
        // ScriptID=AddHP14..AddHP19, AddHP25, AddHP56. CoolingTime=1.
        new(3100, BagConsumableEffectKind.RestoreHitPoints, 200),
        new(3101, BagConsumableEffectKind.RestoreHitPoints, 500),
        new(3102, BagConsumableEffectKind.RestoreHitPoints, 1_000),
        new(3103, BagConsumableEffectKind.RestoreHitPoints, 1_800),
        new(3104, BagConsumableEffectKind.RestoreHitPoints, 3_000),
        new(3105, BagConsumableEffectKind.RestoreHitPoints, 7_000),
        new(3106, BagConsumableEffectKind.RestoreHitPoints, 25_000_000),
        new(3108, BagConsumableEffectKind.RestoreHitPoints, 1_500),

        // POTIONS (a01/a08/a23, Polish patch): the Olympian potions, items
        // 4017-4020 (HP) and 4044-4047 (MP), Development/drops/
        // NEW-POTIONS-SPEC.md. Tiers I-III climb by 3^(1/3) from Magic Healing
        // (7,000) and Mana Essence (2,800) to 3x; tier IV (Nectar of Life /
        // Nectar of Mind) restores 25% of the calculated maximum, at most the
        // tier III amount. CoolingTime=1, like every instant potion.
        new(3109, BagConsumableEffectKind.RestoreHitPoints, 10_100),
        new(3110, BagConsumableEffectKind.RestoreHitPoints, 14_600),
        new(3111, BagConsumableEffectKind.RestoreHitPoints, 21_000),
        new(3112, BagConsumableEffectKind.RestoreHitPoints, 21_000, MaxPercentBasisPoints: 2_500),
        new(3129, BagConsumableEffectKind.RestoreManaPoints, 4_000),
        new(3130, BagConsumableEffectKind.RestoreManaPoints, 5_800),
        new(3131, BagConsumableEffectKind.RestoreManaPoints, 8_400),
        new(3132, BagConsumableEffectKind.RestoreManaPoints, 8_400, MaxPercentBasisPoints: 2_500),

        // Chocolate 4007, Easter Bread 4008. Power2 and item text. CoolingTime=1.
        new(5134, BagConsumableEffectKind.RestoreHitPoints, 2_500),
        new(5139, BagConsumableEffectKind.RestoreHitPoints, 3_000),

        // ScriptID=AddMP1..AddMP6. CoolingTime=1.
        // Perfect Rose 4035, Easter Lamb 4036, Mana Holy Water 4042, Mana Essence 4043. Power2 and item text.
        new(5135, BagConsumableEffectKind.RestoreManaPoints, 400),
        new(5140, BagConsumableEffectKind.RestoreManaPoints, 600),
        new(4866, BagConsumableEffectKind.RestoreManaPoints, 1_600),
        new(4867, BagConsumableEffectKind.RestoreManaPoints, 2_800),
        new(3120, BagConsumableEffectKind.RestoreManaPoints, 60),
        new(3121, BagConsumableEffectKind.RestoreManaPoints, 120),
        new(3122, BagConsumableEffectKind.RestoreManaPoints, 200),
        new(3123, BagConsumableEffectKind.RestoreManaPoints, 500),
        new(3124, BagConsumableEffectKind.RestoreManaPoints, 600),
        new(3125, BagConsumableEffectKind.RestoreManaPoints, 1_000),

        // Candy 9001, Chocolate 9002. Item text; Magic.ini Power2 is 0.
        new(3107, BagConsumableEffectKind.RestoreHitPoints, 2_000),
        new(3126, BagConsumableEffectKind.RestoreManaPoints, 500),

        // ScriptID=AddMoney1..AddMoney3. CoolingTime=2.
        new(4600, BagConsumableEffectKind.GrantSilver, 10_000),
        new(4601, BagConsumableEffectKind.GrantSilver, 100_000),
        new(4602, BagConsumableEffectKind.GrantSilver, 500_000),

        // ScriptID=AddEXP1..AddEXP4. CoolingTime=2.
        new(4620, BagConsumableEffectKind.GrantExperience, 2_000),
        new(4621, BagConsumableEffectKind.GrantExperience, 5_000),
        new(4622, BagConsumableEffectKind.GrantExperience, 10_000),
        new(4623, BagConsumableEffectKind.GrantExperience, 30_000),

        // ScriptID=StatusMagic. Magic.ini carries no value for these; the
        // numbers below are Status.ini's Values/Time for the granted status.
        //
        // 4800 and 4807-4809 are the character-experience grants. They are
        // transcribed on the family's own kind because that is the channel the
        // server applies them on: the 60-minute and the eight-hour grants share
        // it so one tier rule sees both, and it keeps them from competing with
        // the mooncake and Passion Rose consumables on kind 14.
        new(
            4800,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 504,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 900),
        new(
            4807,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 505,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 3_600),
        new(
            4808,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 506,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 3_600),
        new(
            4809,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 507,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 3_600),
        new(
            4752,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 513,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 3_600),
        new(
            4753,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 514,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 3_600),
        new(
            4754,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 515,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 3_600),
        new(
            4755,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 516,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 28_800),
        new(
            4756,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 517,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 28_800),
        new(
            4765,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 591,
            StatusKind: ExperienceBoostKinds.Pet,
            StatusDurationSeconds: 28_800),
        new(
            4747,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 508,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 28_800),
        new(
            4759,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 585,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 28_800),
        new(
            4760,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 586,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 28_800),
        new(
            4764,
            BagConsumableEffectKind.GrantTimedExperienceBoost,
            1,
            StatusId: 590,
            StatusKind: ExperienceBoostKinds.PersistentExperiencePotion,
            StatusDurationSeconds: 28_800)
    ];

    /// <summary>
    /// Status.ini <c>Values</c> expressed as basis points. Status 505 is 0.5,
    /// 506 is 1, 507 is 3 and 513 is 0.5; the <c>ExperienceBoostState</c>
    /// model works in basis points, so 50% is 5000. The 514-517 pet-experience
    /// tiers are 1, 3, 1 and 3, and their durations are the 60-minute and
    /// 8-hour variants the item descriptions name. The enduring
    /// experience-potion tiers are 0.5, 1, 3 and 4 for statuses 585, 508, 586
    /// and 590.
    /// </summary>
    private static readonly FrozenDictionary<int, int> StatusBonusBasisPoints =
        new Dictionary<int, int>
        {
            [505] = 5_000,
            [506] = 10_000,
            [507] = 30_000,
            [504] = 2_500,
            [508] = 10_000,
            [513] = 5_000,            [514] = 10_000,
            [515] = 30_000,
            [516] = 10_000,
            [517] = 30_000,
            [591] = 5_000,
            [585] = 5_000,
            [586] = 30_000,
            [590] = 40_000
        }.ToFrozenDictionary();

    /// <summary>
    /// The effect families this server can apply. <c>GrantExperience</c> is
    /// transcribed and pinned by the protocol checks but deliberately not
    /// activated: the durable bag-item executor has no fighter experience and
    /// level-up path, and re-deriving one would duplicate
    /// <c>PostgresGameStore.ApplyMonsterKillRewardAsync</c>'s level curve. An
    /// exp stone therefore keeps the pre-existing behaviour of being ignored
    /// until a shared progression executor exists.
    /// </summary>
    private static readonly FrozenSet<BagConsumableEffectKind> Implemented =
        new[]
        {
            BagConsumableEffectKind.RestoreHitPoints,
            BagConsumableEffectKind.RestoreManaPoints,
            BagConsumableEffectKind.GrantSilver,
            BagConsumableEffectKind.GrantTimedExperienceBoost
        }.ToFrozenSet();

    private static readonly FrozenDictionary<int, BagConsumableEffect> BySkill =
        Transcribed.ToFrozenDictionary(
            static effect => effect.SkillId,
            static effect => effect);

    public static IReadOnlyCollection<BagConsumableEffect> All => Transcribed;

    /// <summary>
    /// Whether the bag path may apply these effects yet.
    /// </summary>
    /// <remarks>
    /// Off until the durable effect writes have been reviewed end to end. The
    /// transcribed table, the capture-pinned frames and the protocol checks all
    /// stay in place; this constant is the only switch the two call sites read,
    /// and while it is off a right-clicked potion keeps its previous behaviour
    /// (classified, then ignored) instead of writing character state.
    /// </remarks>
    public const bool ActivationEnabled = false;

    /// <summary>
    /// Whether this server applies the effect family yet.
    /// </summary>
    public static bool IsImplemented(BagConsumableEffectKind kind) =>
        Implemented.Contains(kind);

    /// <summary>
    /// Resolves the reviewed effect of one item skill. Both the skill and the
    /// item's own <c>Use=1</c> classification stay database-owned; this table
    /// only supplies the transcribed client numbers.
    /// </summary>
    public static bool TryResolve(
        int skillId,
        out BagConsumableEffect effect) =>
        BySkill.TryGetValue(skillId, out effect);

    /// <summary>
    /// The <c>Status.ini</c> bonus of a granted timed status in basis points.
    /// </summary>
    public static bool TryResolveTimedBoostBonus(
        int statusId,
        out int bonusBasisPoints) =>
        StatusBonusBasisPoints.TryGetValue(statusId, out bonusBasisPoints);
}
