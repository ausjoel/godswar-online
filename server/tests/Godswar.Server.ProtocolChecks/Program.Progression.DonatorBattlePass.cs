using Godswar.Server.State;
using Godswar.Server.Application.Rewards;
using AppDonatorBenefits =
    Godswar.Server.Application.Progression.DonatorBenefits;
using AppDonatorTier =
    Godswar.Server.Application.Progression.DonatorTier;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckExperienceBoostStackingAsync()
    {
        var now = new DateTimeOffset(
            2026,
            8,
            31,
            12,
            0,
            0,
            TimeSpan.Zero);
        var expiresAt = now.AddHours(1);
        var state = CreateCrossChannelExperienceState(expiresAt);

        Check.Equal(
            66_500,
            state.TotalBonusBasisPoints,
            "donator and Battle Pass bonuses add to fighter EXP");
        Check.Equal(
            612,
            state.ApplyTo(80),
            "base 80 fighter EXP receives the additive 7.65x multiplier");
        Check.Equal(
            43_000,
            state.TotalTalentBonusBasisPoints,
            "Battle Pass and faction join Talent boosts without adding donator EXP");
        Check.Equal(
            1_060,
            state.ApplyToTalent(200),
            "Battle Pass and faction add to boosted Talent EXP");
        Check.Equal(
            63_000,
            state.TotalPetBonusBasisPoints,
            "Battle Pass and faction join the shared pet channels");
        Check.Equal(
            1_460,
            state.ApplyToPet(200),
            "Battle Pass and faction add to boosted pet EXP");
        var statusSnapshot = PlayerStatusComposer.Compose(state, [], now);
        Check.Equal(
            6.65f,
            statusSnapshot.Aggregate.ExperienceBonus,
            "Talent-only boosts do not inflate the fighter EXP wire aggregate");
        Check.Equal(
            0,
            state.ApplyTo(0),
            "zero base reward remains zero");

        var battlePassOnly = CreateBattlePassState(expiresAt);
        Check.Equal(
            210,
            battlePassOnly.ApplyTo(200),
            "Battle Pass boosts fighter EXP by five percent");
        Check.Equal(
            210,
            battlePassOnly.ApplyToTalent(200),
            "Battle Pass boosts Talent EXP by five percent");
        Check.Equal(
            210,
            battlePassOnly.ApplyToPet(200),
            "Battle Pass boosts pet EXP by five percent");

        CheckDonatorChannelIsolation();
        CheckSpecializedChannelIsolation();
        CheckMaximumExperienceMultipliers(expiresAt);
        CheckDonatorTierMappings();
        CheckFocusedExperienceProjection(state, now);
        CheckExperienceBoostExpiryFingerprint(now);

        var finiteDonator = new ActiveExperienceBoost(
            ExperienceStatusIds.TrueDemonLord,
            ExperienceBoostKinds.Donator,
            2_000,
            4,
            expiresAt.AddDays(30),
            "donator:true_demon_lord");
        Check.Equal(
            uint.MaxValue,
            finiteDonator.RemainingSeconds(now),
            "finite donator status remains permanent-looking");
        Check.Equal(
            3_600u,
            battlePassOnly.ActiveBoosts[0].RemainingSeconds(now),
            "Battle Pass status keeps its calendar countdown");
        await CheckJsonFocusedExperienceBoostReadAsync();
    }

    private static void CheckExperienceBoostExpiryFingerprint(
        DateTimeOffset now)
    {
        var finite = CreateBattlePassState(now.AddHours(1));
        var first = PlayerStatusComposer.Compose(finite, [], now);
        var countdownAdvanced = PlayerStatusComposer.Compose(
            finite,
            [],
            now.AddMinutes(1));
        Check.True(
            first.Effects[0].RemainingSeconds !=
                countdownAdvanced.Effects[0].RemainingSeconds &&
            first.Fingerprint == countdownAdvanced.Fingerprint,
            "Battle Pass countdown changes do not churn its fingerprint");

        var renewed = CreateBattlePassState(now.AddHours(2));
        var renewedSnapshot = PlayerStatusComposer.Compose(
            renewed,
            [],
            now.AddMinutes(1));
        Check.True(
            first.Fingerprint != renewedSnapshot.Fingerprint,
            "Battle Pass renewal changes its absolute-expiry fingerprint");

        var permanentBoost = renewed.ActiveBoosts[0] with
        {
            ExpiresAt = null
        };
        var permanent = new ExperienceBoostState([permanentBoost]);
        var permanentSnapshot = PlayerStatusComposer.Compose(
            permanent,
            [],
            now.AddMinutes(1));
        Check.True(
            renewedSnapshot.Fingerprint != permanentSnapshot.Fingerprint,
            "finite-to-permanent Battle Pass changes its fingerprint");

        var finiteAgain = new ExperienceBoostState(
        [
            permanentBoost with
            {
                ExpiresAt = now.AddHours(3)
            }
        ]);
        var finiteAgainSnapshot = PlayerStatusComposer.Compose(
            finiteAgain,
            [],
            now.AddMinutes(1));
        Check.True(
            permanentSnapshot.Fingerprint != finiteAgainSnapshot.Fingerprint,
            "permanent-to-finite Battle Pass changes its fingerprint");
    }

    private static ExperienceBoostState CreateCrossChannelExperienceState(
        DateTimeOffset expiresAt) =>
        new(
        [
            new(ExperienceStatusIds.MaxExperiencePotion,
                ExperienceBoostKinds.Consumable, 30_000, 11, expiresAt,
                "potion"),
            new(ExperienceStatusIds.Weekend,
                ExperienceBoostKinds.Weekend, 20_000, 1, expiresAt,
                "weekend"),
            new(ExperienceStatusIds.TrickOrTreat,
                ExperienceBoostKinds.TrickOrTreat, 1_000, 1, expiresAt,
                "event"),
            new(ExperienceStatusIds.GuildDoubleExperience16Hours,
                ExperienceBoostKinds.Guild, 10_000, 1, expiresAt,
                "guild"),
            new(ExperienceStatusIds.MaxTalentPotion400Percent,
                ExperienceBoostKinds.Talent, 40_000, 10, expiresAt,
                "talent"),
            new(ExperienceStatusIds.OctagramPatron,
                ExperienceBoostKinds.Donator, 2_500, 5, null,
                "donator:octagram_patron"),
            new(ExperienceStatusIds.PremiumBattlePass,
                ExperienceBoostKinds.BattlePass,
                BattlePassBenefits.ExperienceBonusBasisPoints,
                1, expiresAt, "battle-pass:premium"),
            new(ExperienceStatusIds.FactionAreaExperience,
                ExperienceBoostKinds.FactionArea, 2_500, 1, expiresAt,
                "world-boss")
        ]);

    private static ExperienceBoostState CreateBattlePassState(
        DateTimeOffset expiresAt) =>
        new(
        [
            new(
                ExperienceStatusIds.PremiumBattlePass,
                ExperienceBoostKinds.BattlePass,
                BattlePassBenefits.ExperienceBonusBasisPoints,
                1,
                expiresAt,
                "battle-pass:premium")
        ]);

    private static void CheckDonatorChannelIsolation()
    {
        var donatorOnly = new ExperienceBoostState(
        [
            new(
                ExperienceStatusIds.OctagramPatron,
                ExperienceBoostKinds.Donator,
                DonatorBenefits.ExperienceBonusBasisPoints(
                    DonatorTier.OctagramPatron),
                (int)DonatorTier.OctagramPatron,
                null,
                "donator:octagram_patron")
        ]);
        Check.Equal(
            250,
            donatorOnly.ApplyTo(200),
            "Octagram Patron boosts fighter EXP by twenty-five percent");
        Check.Equal(
            200,
            donatorOnly.ApplyToTalent(200),
            "donator EXP does not cross into Talent EXP");
        Check.Equal(
            200,
            donatorOnly.ApplyToPet(200),
            "donator EXP does not cross into pet EXP");
    }

    private static void CheckSpecializedChannelIsolation()
    {
        var specialized = new ExperienceBoostState(
        [
            new(
                1_600,
                ExperienceBoostKinds.Pet,
                2_500,
                1,
                null,
                "pet-only"),
            new(
                1_601,
                ExperienceBoostKinds.GuildTalent,
                10_000,
                1,
                null,
                "guild-talent-only")
        ]);
        Check.Equal(
            200,
            specialized.ApplyTo(200),
            "pet and Guild Talent boosts do not enter fighter EXP");
        Check.Equal(
            400,
            specialized.ApplyToTalent(200),
            "Guild Talent enters only Talent EXP");
        Check.Equal(
            250,
            specialized.ApplyToPet(200),
            "pet-specific boosts enter only pet EXP");
    }

    private static void CheckMaximumExperienceMultipliers(
        DateTimeOffset expiresAt)
    {
        var maximum = new ExperienceBoostState(
        [
            new(ExperienceStatusIds.MaxExperiencePotion,
                ExperienceBoostKinds.Consumable, 30_000, 11, expiresAt,
                "potion"),
            new(ExperienceStatusIds.Weekend,
                ExperienceBoostKinds.Weekend, 20_000, 1, expiresAt,
                "weekend"),
            new(ExperienceStatusIds.TrickOrTreat,
                ExperienceBoostKinds.TrickOrTreat, 1_000, 1, expiresAt,
                "event"),
            new(ExperienceStatusIds.GuildDoubleExperience16Hours,
                ExperienceBoostKinds.Guild, 10_000, 1, expiresAt,
                "guild"),
            new(ExperienceStatusIds.MaxTalentPotion400Percent,
                ExperienceBoostKinds.Talent, 40_000, 10, expiresAt,
                "talent"),
            new(1_601, ExperienceBoostKinds.GuildTalent, 10_000, 1,
                expiresAt, "guild-talent"),
            new(1_602, ExperienceBoostKinds.Pet, 30_000, 6,
                expiresAt, "pet"),
            new(ExperienceStatusIds.OctagramPatron,
                ExperienceBoostKinds.Donator, 2_500, 5, null,
                "donator:octagram_patron"),
            new(ExperienceStatusIds.PremiumBattlePass,
                ExperienceBoostKinds.BattlePass, 500, 1, expiresAt,
                "battle-pass:premium"),
            new(ExperienceStatusIds.FactionAreaExperience,
                ExperienceBoostKinds.FactionArea, 35_000, 1, expiresAt,
                "faction-area")
        ]);
        Check.Equal(99_000, maximum.TotalBonusBasisPoints,
            "maximum Fighter additive stack is x10.9");
        Check.Equal(85_500, maximum.TotalTalentBonusBasisPoints,
            "maximum Talent additive stack is x9.55");
        Check.Equal(125_500, maximum.TotalPetBonusBasisPoints,
            "maximum pet additive stack is x13.55");

        var maximumGlobal = new MonsterRewardPolicySnapshot(
            MaximumLowerLevelGap: 20,
            GlobalExperienceMultiplierBasisPoints: 50_000,
            Revision: 1,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedBy: "maximum-stack-check");
        Check.Equal(
            28_939,
            maximumGlobal.ApplyExperienceMultipliers(
                531,
                maximum.TotalBonusBasisPoints),
            "maximum Fighter EXP truncates once after additive and global multipliers");
        Check.Equal(
            477,
            maximumGlobal.ApplyExperienceMultipliers(
                10,
                maximum.TotalTalentBonusBasisPoints),
            "maximum Talent EXP truncates once after additive and global multipliers");
        Check.Equal(
            35_975,
            maximumGlobal.ApplyExperienceMultipliers(
                531,
                maximum.TotalPetBonusBasisPoints),
            "maximum pet EXP truncates once after additive and global multipliers");
        Check.Equal(
            612,
            MonsterRewardPolicySnapshot.Default.ApplyExperienceMultipliers(
                80,
                additiveBonusBasisPoints: 66_500),
            "a 1x global multiplier preserves the additive result");
        Check.Equal(
            0,
            maximumGlobal.ApplyExperienceMultipliers(
                0,
                maximum.TotalBonusBasisPoints),
            "zero base EXP remains zero with every multiplier active");
        Check.Equal(
            int.MaxValue,
            maximumGlobal.ApplyExperienceMultipliers(
                int.MaxValue,
                int.MaxValue),
            "combined EXP multiplication saturates instead of overflowing");
    }

    private static void CheckDonatorTierMappings()
    {
        var tiers = new[]
        {
            new DonatorTierExpectation(DonatorTier.OctagramPatron, 5,
                "Octagram Patron", 2_500, 1_000, 500,
                ExperienceStatusIds.OctagramPatron),
            new DonatorTierExpectation(DonatorTier.TrueDemonLord, 4,
                "True Demon Lord", 2_000, 800, 400,
                ExperienceStatusIds.TrueDemonLord),
            new DonatorTierExpectation(DonatorTier.DemonLordSeed, 3,
                "Demon Lord Seed", 1_500, 600, 300,
                ExperienceStatusIds.DemonLordSeed),
            new DonatorTierExpectation(DonatorTier.OniPatron, 2,
                "Oni Patron", 1_000, 400, 200,
                ExperienceStatusIds.OniPatron),
            new DonatorTierExpectation(DonatorTier.KijinPatron, 1,
                "Kijin Patron", 500, 200, 100,
                ExperienceStatusIds.KijinPatron)
        };

        foreach (var expected in tiers)
        {
            CheckDonatorTierMapping(expected);
        }

        Check.Equal(0,
            DonatorBenefits.ExperienceBonusBasisPoints(DonatorTier.None),
            "no donator tier has no fighter EXP bonus");
        Check.Equal(0,
            DonatorBenefits.MaximumHitPointsBonusBasisPoints(
                DonatorTier.None),
            "no donator tier has no maximum-HP bonus");
        Check.Equal(0,
            DonatorBenefits.AttackBonusBasisPoints(DonatorTier.None),
            "no donator tier has no attack bonus");
        Check.Equal(0,
            DonatorBenefits.StatusId(DonatorTier.None),
            "no donator tier has no client status");
    }

    private static void CheckDonatorTierMapping(
        DonatorTierExpectation expected)
    {
        Check.Equal(
            expected.Rank,
            (short)expected.Tier,
            $"{expected.DisplayName} rank");
        Check.Equal(
            expected.DisplayName,
            DonatorBenefits.DisplayName(expected.Tier),
            $"{expected.DisplayName} display name");
        Check.Equal(
            expected.Experience,
            DonatorBenefits.ExperienceBonusBasisPoints(expected.Tier),
            $"{expected.DisplayName} fighter EXP bonus");
        Check.Equal(
            expected.MaximumHitPoints,
            DonatorBenefits.MaximumHitPointsBonusBasisPoints(expected.Tier),
            $"{expected.DisplayName} maximum-HP bonus");
        Check.Equal(
            expected.Attack,
            DonatorBenefits.AttackBonusBasisPoints(expected.Tier),
            $"{expected.DisplayName} attack bonus");
        Check.Equal(
            expected.StatusId,
            DonatorBenefits.StatusId(expected.Tier),
            $"{expected.DisplayName} client status");

        var applicationTier = (AppDonatorTier)expected.Rank;
        Check.Equal(
            expected.DisplayName,
            AppDonatorBenefits.DisplayName(applicationTier),
            $"{expected.DisplayName} focused-contract display name");
        Check.Equal(
            expected.Experience,
            AppDonatorBenefits.ExperienceBonusBasisPoints(applicationTier),
            $"{expected.DisplayName} focused-contract fighter EXP bonus");
        Check.Equal(
            expected.MaximumHitPoints,
            AppDonatorBenefits.MaximumHitPointsBonusBasisPoints(
                applicationTier),
            $"{expected.DisplayName} focused-contract maximum-HP bonus");
        Check.Equal(
            expected.Attack,
            AppDonatorBenefits.AttackBonusBasisPoints(applicationTier),
            $"{expected.DisplayName} focused-contract attack bonus");
        Check.Equal(
            expected.StatusId,
            AppDonatorBenefits.StatusId(applicationTier),
            $"{expected.DisplayName} focused-contract client status");
    }

    private static void CheckFocusedExperienceProjection(
        ExperienceBoostState state,
        DateTimeOffset now)
    {
        var focused = FocusedGameplayProjectionCompatibility.ToApplication(
            state,
            now);
        Check.Equal(
            state.TotalBonusBasisPoints,
            focused.TotalBonusBasisPoints,
            "focused projection preserves fighter EXP channels");
        Check.Equal(
            state.TotalTalentBonusBasisPoints,
            focused.TotalTalentBonusBasisPoints,
            "focused projection preserves Talent EXP channels");
        Check.Equal(
            state.TotalPetBonusBasisPoints,
            focused.TotalPetBonusBasisPoints,
            "focused projection preserves pet EXP channels");
        Check.Equal(
            state.ApplyToPet(200),
            focused.ApplyToPet(200),
            "focused projection applies the same pet EXP bonus");
    }

    private sealed record DonatorTierExpectation(
        DonatorTier Tier,
        short Rank,
        string DisplayName,
        int Experience,
        int MaximumHitPoints,
        int Attack,
        int StatusId);
}
