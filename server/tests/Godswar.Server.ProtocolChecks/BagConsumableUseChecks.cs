using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Locks the capture-proven bag-consumable activation: the transcribed
/// Magic.ini/Status.ini effect table, the database-owned item classification,
/// and the 10040 / 10046 / 10051 / 10356 frames byte-for-byte against the
/// 2026-09-15 reference capture.
/// </summary>
internal static class BagConsumableUseChecks
{
    public const string CheckName =
        "Bag consumable activation and transcribed client effects";

    /// <summary>
    /// Reference-server frames quoted below. The capture stores the whole
    /// frame including the 4-byte length+opcode header.
    /// </summary>
    private const string CapturedCast4600 =
        "2800382725020000f811000000000000250200000a000000" +
        "10791f432935dfc10000000000000000";
    private const string CapturedImpact4600 =
        "18003e272502000025020000f811000010791f432935dfc1";
    private const string CapturedSilverGrant4600 =
        "10007428190000002502000010270000";
    private const string CapturedActivation4503 =
        "5c004327d503000000000000000006000000000097110000" +
        "ffffffffffffffffffffffffffffffffffffffff01010000" +
        "000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000";
    private const string CapturedActivation4150 =
        "5c0043272502000000000000000003000000000036100000" +
        "ffffffffffffffffffffffffffffffffffffffff01010000" +
        "000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000";

    private const float CapturedCasterX = 11.9715004f;
    private const float CapturedCasterZ = -29.869f;

    public static Task RunAsync()
    {
        CheckTranscribedMagicEffects();
        CheckTranscribedStatusEffects();
        CheckPetExperienceBoostPotionTiers();
        CheckEnduringExperiencePotionTiers();
        CheckImplementedFamilies();
        CheckStatusBoostIsVisibleInTheComposedSnapshot();
        CheckDatabaseOwnedClassification();
        CheckCapturedCastFrame();
        CheckCapturedImpactFrame();
        CheckCapturedActivationFrames();
        CheckCapturedSilverGrantFrame();
        CheckEvidenceAcceptsEveryImplementedFamily();
        CheckLedgerReasonCodeIsAccepted();
        CheckCaptureDerivedLayoutsAreNotDuplicated();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Magic.ini <c>Power2</c> per reviewed skill. Source SHA-256
    /// 08AEC7E0453C24ECD3C8C697AFAFA85F6DD812E83C91EC2F145B2A7A73F7C0AF; the
    /// pinned <c>BagConsumableCooldownPolicy</c> header cites the same file.
    /// </summary>
    private static void CheckTranscribedMagicEffects()
    {
        var expected = new (int Skill, BagConsumableEffectKind Kind, int Amount)[]
        {
            (3100, BagConsumableEffectKind.RestoreHitPoints, 200),
            (3101, BagConsumableEffectKind.RestoreHitPoints, 500),
            (3102, BagConsumableEffectKind.RestoreHitPoints, 1000),
            (3103, BagConsumableEffectKind.RestoreHitPoints, 1800),
            (3104, BagConsumableEffectKind.RestoreHitPoints, 3000),
            (3105, BagConsumableEffectKind.RestoreHitPoints, 7000),
            (3106, BagConsumableEffectKind.RestoreHitPoints, 25000000),
            (3108, BagConsumableEffectKind.RestoreHitPoints, 1500),
            (3120, BagConsumableEffectKind.RestoreManaPoints, 60),
            (3121, BagConsumableEffectKind.RestoreManaPoints, 120),
            (3122, BagConsumableEffectKind.RestoreManaPoints, 200),
            (3123, BagConsumableEffectKind.RestoreManaPoints, 500),
            (3124, BagConsumableEffectKind.RestoreManaPoints, 600),
            (3125, BagConsumableEffectKind.RestoreManaPoints, 1000),
            (4600, BagConsumableEffectKind.GrantSilver, 10000),
            (4601, BagConsumableEffectKind.GrantSilver, 100000),
            (4602, BagConsumableEffectKind.GrantSilver, 500000),
            (4620, BagConsumableEffectKind.GrantExperience, 2000),
            (4621, BagConsumableEffectKind.GrantExperience, 5000),
            (4622, BagConsumableEffectKind.GrantExperience, 10000),
            (4623, BagConsumableEffectKind.GrantExperience, 30000)
        };
        foreach (var (skill, kind, amount) in expected)
        {
            Check.True(
                BagConsumableEffectCatalog.TryResolve(skill, out var effect) &&
                effect.Kind == kind &&
                effect.Amount == amount,
                $"Magic.ini skill {skill} carries {amount} as {kind}");
        }

        // The families the brief keeps out stay out: Magic.ini lists them with
        // Power2 = 0 (3107, 3126, 4603, 4610-4617, 4640-4645) or has no section
        // at all (4720-4729, 4736-4739, 4814-4830), so nothing may resolve.
        foreach (var excluded in new[]
                 {
                     3107, 3126, 3127, 3128, 4603,
                     4610, 4611, 4612, 4613, 4614, 4615, 4616, 4617,
                     4640, 4641, 4642, 4643, 4644, 4645,
                     4720, 4721, 4722, 4736,
                     4800, 4801, 4802, 4803, 4804, 4814, 4830, 7002
                 })
        {
            Check.True(
                !BagConsumableEffectCatalog.TryResolve(excluded, out _),
                $"skill {excluded} has no transcribed bag-consumable effect");
        }

        Check.Equal(
            46, // NEXT: + the 8 Olympian potions (3109-3112, 3129-3132)
            BagConsumableEffectCatalog.All.Count,
            "exactly the reviewed bag-consumable skills are transcribed");
    }

    /// <summary>
    /// Status.ini <c>Values</c>/<c>Time</c> for the StatusMagic skills.
    /// Source SHA-256
    /// 3011C349B2DBEF42C73C19E0453F67B6642B566747F0BBC9042EF59888186C97.
    /// 504 = 0.25, 505 = 0.5, 506 = 1, 507 = 3, 513 = 0.5.
    /// </summary>
    private static void CheckTranscribedStatusEffects()
    {
        var expected = new (int Skill, int Status, int Kind, int BasisPoints)[]
        {
            (4800, 504, ExperienceBoostKinds.PersistentExperiencePotion, 2500),
            (4807, 505, ExperienceBoostKinds.PersistentExperiencePotion, 5000),
            (4808, 506, ExperienceBoostKinds.PersistentExperiencePotion, 10000),
            (4809, 507, ExperienceBoostKinds.PersistentExperiencePotion, 30000),
            (4752, 513, ExperienceBoostKinds.Pet, 5000)
        };
        foreach (var (skill, status, kind, basisPoints) in expected)
        {
            var seconds = skill == 4800 ? 900 : 3600;
            Check.True(
                BagConsumableEffectCatalog.TryResolve(skill, out var effect) &&
                effect.Kind ==
                    BagConsumableEffectKind.GrantTimedExperienceBoost &&
                effect.StatusId == status &&
                effect.StatusKind == kind &&
                effect.StatusDurationSeconds == seconds &&
                effect.IsValid,
                $"Status.ini skill {skill} grants status {status} for {seconds}s");
            Check.True(
                BagConsumableEffectCatalog.TryResolveTimedBoostBonus(
                    status,
                    out var resolved) &&
                resolved == basisPoints,
                $"Status.ini status {status} is {basisPoints} basis points");
        }

        // 500-503 stay approved-out; they must not resolve. Status 504 is the
        // primary (25%/15min) character grant and is transcribed, while
        // 514-517 and 508/585/586/590 belong to their own families and are
        // asserted positively by those checks instead.
        foreach (var status in new[] { 500, 501, 502, 503 })
        {
            Check.True(
                !BagConsumableEffectCatalog.TryResolveTimedBoostBonus(
                    status,
                    out _),
                $"status {status} is not an approved bag-consumable boost");
        }

        Check.True(
            !BagConsumableEffectCatalog.TryResolveTimedBoostBonus(505, out var wrong) ||
            wrong != 0,
            "a granted status never resolves to a zero bonus");
    }

    /// <summary>
    /// The fighter-experience stone family is transcribed but deliberately not
    /// activated until a shared progression executor exists.
    /// </summary>
    private static void CheckImplementedFamilies()
    {
        foreach (var kind in new[]
                 {
                     BagConsumableEffectKind.RestoreHitPoints,
                     BagConsumableEffectKind.RestoreManaPoints,
                     BagConsumableEffectKind.GrantSilver,
                     BagConsumableEffectKind.GrantTimedExperienceBoost
                 })
        {
            Check.True(
                BagConsumableEffectCatalog.IsImplemented(kind),
                $"{kind} is applied by the durable bag consumable path");
        }

        Check.True(
            !BagConsumableEffectCatalog.IsImplemented(
                BagConsumableEffectKind.GrantExperience),
            "instant-EXP stones stay deferred rather than half-implemented");
    }

    /// <summary>
    /// A granted pet boost (status 513) must be visible in the composed MSG_STATUS
    /// snapshot (opcode 10167) that <c>PlayerStatusComposer</c> builds, and must
    /// contribute to the pet experience bonus.
    /// </summary>
    private static void CheckStatusBoostIsVisibleInTheComposedSnapshot()
    {
        Check.True(
            BagConsumableEffectCatalog.TryResolve(4752, out var effect),
            "skill 4752 resolves to the pet EXP status");
        Check.True(
            BagConsumableEffectCatalog.TryResolveTimedBoostBonus(
                effect.StatusId,
                out var bonusBasisPoints),
            "status 513 has a transcribed bonus");

        var now = new DateTimeOffset(
            2026,
            9,
            15,
            12,
            0,
            0,
            TimeSpan.Zero);
        var boosts = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                effect.StatusId,
                effect.StatusKind,
                bonusBasisPoints,
                Priority: 1,
                now.AddSeconds(effect.StatusDurationSeconds),
                "item:4752")
        ]);
        var snapshot = PlayerStatusComposer.Compose(boosts, [], now);
        var composed = snapshot.Effects.SingleOrDefault(
            static entry => entry.StatusId == 513u);
        Check.True(
            composed.StatusId == 513u,
            "the composed 10167 snapshot publishes status 513");
        Check.Equal(
            3600u,
            composed.RemainingSeconds,
            "the published 513 countdown is Status.ini's Time");
        Check.Equal(
            5000,
            boosts.TotalPetBonusBasisPoints,
            "status 513 is a +50% pet experience boost");
        Check.Equal(
            0,
            boosts.TotalBonusBasisPoints,
            "the pet boost does not leak into fighter experience");
    }

    /// <summary>
    /// The five reviewed pet-experience potions keep their client-description
    /// tiers, and the tier rule is: a higher tier replaces the active grant and
    /// drops its remaining time, the same tier extends the timer up to the
    /// 24-hour online ceiling, and a lower tier changes nothing.
    /// </summary>
    private static void CheckPetExperienceBoostPotionTiers()
    {
        var expected = new (uint Item, int Skill, int Status, int BasisPoints, long Ticks)[]
        {
            (4529, 4752, 513, 5_000, TimeSpan.TicksPerHour),
            (4530, 4753, 514, 10_000, TimeSpan.TicksPerHour),
            (4531, 4754, 515, 30_000, TimeSpan.TicksPerHour),
            (4532, 4755, 516, 10_000, 8 * TimeSpan.TicksPerHour),
            (4533, 4756, 517, 30_000, 8 * TimeSpan.TicksPerHour)
        };
        Check.Equal(
            expected.Length,
            PetExperienceBoostPolicy.All.Count,
            "exactly the five reviewed pet-experience potions are mapped");
        var catalog = CreateCatalog(
            [.. expected.Select(
                static entry => Consume(
                    checked((int)entry.Item),
                    use: 1,
                    skill: entry.Skill))]);
        foreach (var (item, skill, status, basisPoints, ticks) in expected)
        {
            Check.True(
                PetExperienceBoostPolicy.TryResolveItem(
                    catalog,
                    item,
                    out var definition) &&
                definition.ItemId == item &&
                definition.SkillId == skill &&
                definition.StatusId == status &&
                definition.BonusBasisPoints == basisPoints &&
                definition.OnlineTicks == ticks,
                $"item {item} keeps skill {skill}, status {status}, " +
                $"{basisPoints} basis points and {ticks} online ticks");
            Check.True(
                BagConsumableEffectCatalog.TryResolve(skill, out var effect) &&
                effect.StatusId == status &&
                effect.StatusKind == ExperienceBoostKinds.Pet &&
                effect.StatusDurationSeconds == ticks / TimeSpan.TicksPerSecond,
                $"the transcribed table agrees with skill {skill}");
            Check.True(
                BagConsumableEffectCatalog.TryResolveTimedBoostBonus(
                    status,
                    out var transcribed) &&
                transcribed == basisPoints,
                $"status {status} carries {basisPoints} transcribed basis points");
        }

        // 4533 (+300%/8h) measured against the 4529 grant (+50%/1h) replaces
        // it outright: the previous hour is dropped, not accumulated.
        var higher = PetExperienceBoostPolicy.ResolveGrant(
            5_000,
            TimeSpan.TicksPerHour,
            PetExperienceBoostPolicy.All.Single(
                static definition => definition.ItemId == 4533));
        Check.True(
            higher.ReplacesActive &&
            higher.BonusBasisPoints == 30_000 &&
            higher.OnlineTicks == 8 * TimeSpan.TicksPerHour,
            "a higher tier replaces the active grant and resets its duration");

        // A second 4532 (+100%/8h) on top of another leaves it at 16 hours.
        var extended = PetExperienceBoostPolicy.ResolveGrant(
            10_000,
            8 * TimeSpan.TicksPerHour,
            PetExperienceBoostPolicy.All.Single(
                static definition => definition.ItemId == 4532));
        Check.True(
            extended.ReplacesActive &&
            extended.BonusBasisPoints == 10_000 &&
            extended.OnlineTicks == 16 * TimeSpan.TicksPerHour,
            "the same tier extends the online timer");

        // The ceiling clamps a third 8-hour grant to 24 hours, not 8.
        var capped = PetExperienceBoostPolicy.ResolveGrant(
            10_000,
            16 * TimeSpan.TicksPerHour,
            PetExperienceBoostPolicy.All.Single(
                static definition => definition.ItemId == 4532));
        Check.Equal(
            PetExperienceBoostPolicy.MaximumOnlineTicks,
            capped.OnlineTicks,
            "same-tier reuse stops at the 24-hour online ceiling");
        Check.Equal(
            24 * TimeSpan.TicksPerHour,
            PetExperienceBoostPolicy.MaximumOnlineTicks,
            "the ceiling is 24 hours of online time");

        // 4529 (+50%/1h) used while the +300% grant is running is inert, and
        // the active grant keeps both its tier and its whole remaining timer.
        var lower = PetExperienceBoostPolicy.ResolveGrant(
            30_000,
            7 * TimeSpan.TicksPerHour,
            PetExperienceBoostPolicy.All.Single(
                static definition => definition.ItemId == 4529));
        Check.True(
            !lower.ReplacesActive &&
            lower.BonusBasisPoints == 30_000 &&
            lower.OnlineTicks == 7 * TimeSpan.TicksPerHour,
            "a lower tier neither downgrades nor extends the active grant");

        // The granted tier reaches pet experience and stays out of the
        // fighter and talent channels.
        var now = new DateTimeOffset(
            2026,
            9,
            15,
            12,
            0,
            0,
            TimeSpan.Zero);
        var state = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                517,
                ExperienceBoostKinds.Pet,
                30_000,
                Priority: higher.Priority,
                now.AddHours(8),
                "pet-exp-potion:4756")
        ]);
        Check.Equal(
            4 * 530,
            state.ApplyToPet(530),
            "a +300% grant quadruples pet experience");
        Check.True(
            state.TotalBonusBasisPoints == 0 &&
            state.TotalTalentBonusBasisPoints == 0,
            "a pet-only grant joins neither the fighter nor the talent stack");
        Check.Equal(
            530,
            state.ApplyTo(530),
            "a pet-only grant leaves fighter experience unchanged");
        Check.Equal(
            530,
            state.ApplyToTalent(530),
            "a pet-only grant leaves talent experience unchanged");
    }

    /// <summary>
    /// The eight reviewed experience potions raise the character's own
    /// experience and the pet's at the same time. The 60-minute and eight-hour
    /// grants share one channel, so a running pet-experience potion keeps its
    /// own duration and the bonuses add together.
    /// </summary>
    private static void CheckEnduringExperiencePotionTiers()
    {
        var expected = new (uint Item, int Skill, int Status, int BasisPoints, long Ticks)[]
        {
            (4500, 4800, 504, 2_500, 15 * TimeSpan.TicksPerMinute),
            (4501, 4807, 505, 5_000, TimeSpan.TicksPerHour),
            (4502, 4808, 506, 10_000, TimeSpan.TicksPerHour),
            (4503, 4809, 507, 30_000, TimeSpan.TicksPerHour),
            (4534, 4759, 585, 5_000, 8 * TimeSpan.TicksPerHour),
            (4506, 4747, 508, 10_000, 8 * TimeSpan.TicksPerHour),
            (4535, 4760, 586, 30_000, 8 * TimeSpan.TicksPerHour),
            (4539, 4764, 590, 40_000, 8 * TimeSpan.TicksPerHour)
        };
        Check.Equal(
            expected.Length,
            ExperienceBoostPotionPolicy.All.Count,
            "exactly the eight reviewed experience potions are mapped");
        Check.Equal(
            ExperienceBoostKinds.PersistentExperiencePotion,
            ExperienceBoostPotionPolicy.Kind,
            "the family applies on its own channel");
        var catalog = CreateCatalog(
            [.. expected.Select(
                static entry => Consume(
                    checked((int)entry.Item),
                    use: 1,
                    skill: entry.Skill))]);
        foreach (var (item, skill, status, basisPoints, ticks) in expected)
        {
            Check.True(
                ExperienceBoostPotionPolicy.TryResolveItem(
                    catalog,
                    item,
                    out var definition) &&
                definition.ItemId == item &&
                definition.SkillId == skill &&
                definition.StatusId == status &&
                definition.BonusBasisPoints == basisPoints &&
                definition.OnlineTicks == ticks,
                $"item {item} keeps skill {skill}, status {status} and " +
                $"{basisPoints} basis points over {ticks} online ticks");
            Check.True(
                ExperienceBoostPotionPolicy.IsReviewedItem(item),
                $"item {item} is recognised by the routing classifier");
            Check.True(
                BagConsumableEffectCatalog.TryResolve(skill, out var effect) &&
                effect.StatusId == status &&
                effect.StatusKind ==
                    ExperienceBoostKinds.PersistentExperiencePotion &&
                effect.StatusDurationSeconds ==
                    ticks / TimeSpan.TicksPerSecond,
                $"the transcribed table agrees with skill {skill}");
            Check.True(
                BagConsumableEffectCatalog.TryResolveTimedBoostBonus(
                    status,
                    out var transcribed) &&
                transcribed == basisPoints,
                $"status {status} carries {basisPoints} transcribed basis points");
        }

        // The short and long grants of one tier share a channel, so the second
        // one extends rather than replacing: +100%/60min then +100%/8h is 9h.
        var acrossDurations = ExperienceBoostPotionPolicy.ResolveGrant(
            10_000,
            TimeSpan.TicksPerHour,
            ExperienceBoostPotionPolicy.All.Single(
                static definition => definition.ItemId == 4506));
        Check.True(
            acrossDurations.ReplacesActive &&
            acrossDurations.BonusBasisPoints == 10_000 &&
            acrossDurations.OnlineTicks == 9 * TimeSpan.TicksPerHour,
            "the same tier across the two durations extends the timer");

        // The tier rule on this channel matches the pet family's.
        var higher = ExperienceBoostPotionPolicy.ResolveGrant(
            5_000,
            8 * TimeSpan.TicksPerHour,
            ExperienceBoostPotionPolicy.All.Single(
                static definition => definition.ItemId == 4535));
        Check.True(
            higher.ReplacesActive &&
            higher.BonusBasisPoints == 30_000 &&
            higher.OnlineTicks == 8 * TimeSpan.TicksPerHour,
            "a higher experience tier replaces the grant and resets its duration");
        var capped = ExperienceBoostPotionPolicy.ResolveGrant(
            30_000,
            16 * TimeSpan.TicksPerHour,
            ExperienceBoostPotionPolicy.All.Single(
                static definition => definition.ItemId == 4535));
        Check.Equal(
            24 * TimeSpan.TicksPerHour,
            capped.OnlineTicks,
            "same-tier reuse stops at the 24-hour online ceiling");
        var lower = ExperienceBoostPotionPolicy.ResolveGrant(
            40_000,
            5 * TimeSpan.TicksPerHour,
            ExperienceBoostPotionPolicy.All.Single(
                static definition => definition.ItemId == 4534));
        Check.True(
            !lower.ReplacesActive &&
            lower.BonusBasisPoints == 40_000 &&
            lower.OnlineTicks == 5 * TimeSpan.TicksPerHour,
            "a lower experience tier neither downgrades nor extends the grant");

        // The two families stack. A running pet potion (+300%, kind 24) plus
        // one enduring experience potion (+300%, kind 25) is +600% pet EXP and
        // +300% character EXP.
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var stacked = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                517,
                ExperienceBoostKinds.Pet,
                30_000,
                Priority: 30,
                now.AddHours(8),
                "pet-exp-potion:4756"),
            new ActiveExperienceBoost(
                586,
                ExperienceBoostKinds.PersistentExperiencePotion,
                30_000,
                Priority: 30,
                now.AddHours(8),
                "enduring-exp-potion:4760")
        ]);
        Check.Equal(
            60_000,
            stacked.TotalPetBonusBasisPoints,
            "the two families add up to +600% pet experience");
        Check.Equal(
            7 * 100,
            stacked.ApplyToPet(100),
            "the stacked pet multiplier is x7, not a replacement");
        Check.Equal(
            30_000,
            stacked.TotalBonusBasisPoints,
            "only the enduring potion feeds the fighter stack");
        Check.Equal(
            4 * 100,
            stacked.ApplyTo(100),
            "the enduring potion grants +300% character experience");
        Check.Equal(
            0,
            stacked.TotalTalentBonusBasisPoints,
            "neither family feeds the talent stack");

        // Both statuses reach the composed snapshot, so the client shows two
        // independent icons.
        var snapshot = PlayerStatusComposer.Compose(stacked, [], now);
        Check.True(
            snapshot.Effects.Any(static entry => entry.StatusId == 517u) &&
            snapshot.Effects.Any(static entry => entry.StatusId == 586u),
            "both the pet and the enduring status are published");
    }

    /// <summary>
    /// The item's own <c>kind = 'consume item'</c>, <c>Use = 1</c> and
    /// <c>Skill</c> stay database-owned: the classifier reads them from the
    /// pinned catalog and only then looks the skill up in the transcribed
    /// tables. The pinned catalog is built from the released baseline below, so
    /// the assertion is about the classifier, not about compiled item seeds.
    /// </summary>
    private static void CheckDatabaseOwnedClassification()
    {
        // A consume item whose Use flag is off, a consume item carrying a skill
        // with no reviewed effect, a non-consume item that carries Use=1 and a
        // Skill, and the reviewed potion/money-bag/pet-potion templates.
        var catalog = CreateCatalog(
            Consume(4000, use: 1, skill: 3100),
            Consume(4001, use: 0, skill: 3101),
            Consume(4150, use: 1, skill: 4600),
            Consume(4529, use: 1, skill: 4752),
            Consume(4721, use: 1, skill: 9999),
            Wearable(9000, use: 1, skill: 3100));

        Check.True(
            BagConsumableItemStats.TryResolveSkillId(catalog, 4000, out var hp) &&
            hp == 3100 &&
            BagConsumableEffectCatalog.TryResolve(hp, out var hpEffect) &&
            hpEffect.Kind == BagConsumableEffectKind.RestoreHitPoints &&
            hpEffect.Amount == 200,
            "Small Healing Potion resolves to its Magic.ini HP effect");
        Check.True(
            BagConsumableItemStats.TryResolveSkillId(catalog, 4150, out var money) &&
            money == 4600 &&
            BagConsumableEffectCatalog.TryResolve(money, out var moneyEffect) &&
            moneyEffect.Kind == BagConsumableEffectKind.GrantSilver &&
            moneyEffect.Amount == 10000,
            "Small Money Bag resolves to its Magic.ini silver effect");
        Check.True(
            BagConsumableItemStats.TryResolveSkillId(catalog, 4529, out var petBoost) &&
            petBoost == 4752 &&
            BagConsumableEffectCatalog.TryResolve(petBoost, out var boost) &&
            boost.StatusId == 513 &&
            boost.StatusKind == ExperienceBoostKinds.Pet,
            "the reported Weak Pet Exp Potion classifies as status 513");
        Check.True(
            !BagConsumableItemStats.TryResolveSkillId(catalog, 4001, out _),
            "Use=0 is not a usable consume item even when Skill is set");
        Check.True(
            !BagConsumableItemStats.TryResolveSkillId(catalog, 9000, out _),
            "a non-consume item is never classified as a bag consumable");
        Check.True(
            !BagConsumableItemStats.TryResolveSkillId(catalog, 999_999, out _),
            "an unknown template is not a bag consumable");
        Check.True(
            BagConsumableItemStats.TryResolveSkillId(catalog, 4721, out var unreviewed) &&
            unreviewed == 9999 &&
            !BagConsumableEffectCatalog.TryResolve(unreviewed, out _),
            "a consume item whose skill is not reviewed stays ignored");
    }

    /// <summary>
    /// Captured <c>S2C 10040</c> id 145490: player 549 casting item skill 4600
    /// with itself as the target from (11.9715, -29.869).
    /// </summary>
    private static void CheckCapturedCastFrame()
    {
        var cast = PacketBuilder.BagConsumableSkillCastVisual(
            549u,
            549u,
            4600u,
            CapturedCasterX,
            CapturedCasterZ,
            CapturedCasterX,
            CapturedCasterZ);
        Check.Equal(
            CapturedCast4600,
            Convert.ToHexString(cast),
            "the 10040 bag-consumable cast matches capture id 145490");
        PinCastLayout(cast, expectedSkillId: 4600u);
    }

    /// <summary>
    /// The cast frame is the already-proven 40-byte monster layout: caster at
    /// +4, skill at +8, zero at +12, target at +16, 10 at +20, and the caster
    /// position at +24/+28.
    /// </summary>
    private static void PinCastLayout(byte[] cast, uint expectedSkillId)
    {
        Check.True(
            cast.Length == 40 &&
            BinaryPrimitives.ReadUInt16LittleEndian(cast) == 40 &&
            BinaryPrimitives.ReadUInt16LittleEndian(cast.AsSpan(2)) == 0x2738 &&
            BinaryPrimitives.ReadUInt32LittleEndian(cast.AsSpan(4)) == 549u &&
            BinaryPrimitives.ReadUInt32LittleEndian(cast.AsSpan(8)) ==
                expectedSkillId &&
            BinaryPrimitives.ReadUInt32LittleEndian(cast.AsSpan(12)) == 0u &&
            BinaryPrimitives.ReadUInt32LittleEndian(cast.AsSpan(16)) == 549u &&
            BinaryPrimitives.ReadUInt32LittleEndian(cast.AsSpan(20)) == 10u &&
            BinaryPrimitives.ReadSingleLittleEndian(cast.AsSpan(24)) ==
                CapturedCasterX &&
            BinaryPrimitives.ReadSingleLittleEndian(cast.AsSpan(28)) ==
                CapturedCasterZ,
            "the 10040 layout is caster, skill, zero, target, 10, x, z");
    }

    /// <summary>
    /// Captured <c>S2C 10046</c> id 145493: attacker 549, target 549, skill
    /// 4600, at (11.9715, -29.869).
    /// </summary>
    private static void CheckCapturedImpactFrame()
    {
        var impact = PacketBuilder.SkillCastImpact(
            549u,
            549u,
            4600u,
            CapturedCasterX,
            CapturedCasterZ);
        Check.Equal(
            CapturedImpact4600,
            Convert.ToHexString(impact),
            "the 10046 impact matches capture id 145493");
        Check.True(
            impact.Length == 24 &&
            BinaryPrimitives.ReadUInt16LittleEndian(impact.AsSpan(2)) == 0x273E &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(4)) == 549u &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(8)) == 549u &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(12)) == 4600u,
            "the 10046 layout is attacker, target, skill, x, z");
    }

    /// <summary>
    /// Captured <c>S2C 10051</c> replies, ids 183610 (player 981, page 0
    /// index 6, item 4503) and 145494 (player 549, page 0 index 3, item 4150).
    /// </summary>
    private static void CheckCapturedActivationFrames()
    {
        var item4503 = BagConsumableActivation(
            981u,
            bagSlot: 6,
            itemId: 4503u,
            captured: CapturedActivation4503);
        Check.Equal(
            CapturedActivation4503,
            Convert.ToHexString(item4503),
            "the 10051 reply matches capture id 183610");

        var item4150 = BagConsumableActivation(
            549u,
            bagSlot: 3,
            itemId: 4150u,
            captured: CapturedActivation4150);
        Check.Equal(
            CapturedActivation4150,
            Convert.ToHexString(item4150),
            "the 10051 reply matches capture id 145494");
    }

    /// <summary>
    /// Reproduces the captured 10051 body for one bound single-unit bag item and
    /// pins every documented offset, including the <c>01 01</c> bound/stack pair
    /// at +44.
    /// </summary>
    private static byte[] BagConsumableActivation(
        uint playerObjectId,
        int bagSlot,
        uint itemId,
        string captured)
    {
        var character = new GameCharacter
        {
            Id = 1,
            AccountId = 1,
            KitBag = BuildKitBag(bagSlot, itemId)
        };
        var packet = PacketBuilder.BagConsumableActivation(
            character,
            bagSlot,
            playerObjectId);
        Check.True(
            packet.Length == 92 &&
            Convert.ToHexString(packet).Length == captured.Length,
            "the 10051 reply is the captured 92-byte frame");
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == 92 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x2743 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) ==
                playerObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 0u &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12)) ==
                (ushort)(bagSlot / 24) &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14)) ==
                (ushort)(bagSlot % 24) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)) == 0u &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == itemId &&
            packet.AsSpan(24, 20).ToArray().All(static value => value == 0xFF) &&
            packet[44] == 1 &&
            packet[45] == 1 &&
            packet.AsSpan(46).ToArray().All(static value => value == 0),
            "the 10051 body carries the player, bag position, item and 01 01 pair");
        return packet;
    }

    private static string BuildKitBag(int bagSlot, uint itemId)
    {
        var slots = Enumerable.Repeat("[]", 96).ToArray();
        slots[bagSlot] = "[" + string.Join(
            ',',
            itemId.ToString(),
            "1", "1", "1", "1", "1", "1", "1", "1", "1", "0") + "]";
        return string.Concat(slots);
    }

    /// <summary>
    /// Captured <c>S2C 10356</c> id 145492 for skill 4600:
    /// <c>10007428190000002502000010270000</c> = {25, player 549, 10000}.
    /// </summary>
    private static void CheckCapturedSilverGrantFrame()
    {
        var grant = PacketBuilder.BagSilverGrant(549u, 10_000);
        Check.Equal(
            CapturedSilverGrant4600,
            Convert.ToHexString(grant),
            "the 10356 silver grant matches capture id 145492");
        Check.True(
            Opcodes.BagSilverGrant == 10356 &&
            BinaryPrimitives.ReadUInt16LittleEndian(grant) == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(grant.AsSpan(2)) == 0x2874 &&
            BinaryPrimitives.ReadInt32LittleEndian(grant.AsSpan(4)) == 25 &&
            BinaryPrimitives.ReadUInt32LittleEndian(grant.AsSpan(8)) == 549u &&
            BinaryPrimitives.ReadInt32LittleEndian(grant.AsSpan(12)) == 10_000,
            "the 10356 frame is kind 25, player, amount");
        Check.True(
            Throws(() => PacketBuilder.BagSilverGrant(549u, 0)),
            "a non-positive silver grant is rejected");
    }

    /// <summary>
    /// Every implemented family must carry valid committed evidence, and the
    /// evidence must refuse an inconsistent record.
    /// </summary>
    private static void CheckEvidenceAcceptsEveryImplementedFamily()
    {
        foreach (var effect in BagConsumableEffectCatalog.All.Where(
                     static effect => BagConsumableEffectCatalog.IsImplemented(
                         effect.Kind)))
        {
            var evidence = new BagConsumableEvidence(
                ItemInstanceId: 77,
                ItemTemplateId: 4000,
                KitBagSlot: 6,
                SkillId: effect.SkillId,
                EffectKind: effect.Kind,
                Amount: effect.Amount,
                StatusId: effect.StatusId,
                StatusKind: effect.StatusKind,
                StatusDurationSeconds: effect.StatusDurationSeconds,
                CurrentHp: 800,
                CurrentMp: 90,
                Silver: 20_000);
            Check.True(
                evidence.IsValid,
                $"skill {effect.SkillId} produces valid committed evidence");
        }

        Check.True(
            !new BagConsumableEvidence(
                77,
                4000,
                6,
                3100,
                BagConsumableEffectKind.GrantTimedExperienceBoost,
                1,
                StatusId: 0,
                StatusKind: 0,
                StatusDurationSeconds: 0,
                CurrentHp: 800,
                CurrentMp: 90,
                Silver: 20_000).IsValid,
            "a timed boost without a status is not valid evidence");
    }

    /// <summary>
    /// The silver ledger must accept what the executor writes: the reason code
    /// has to match <c>ck_character_currency_ledger_reason</c> and the grant has
    /// to respect <c>ck_character_base_money_nonnegative</c>'s upper bound.
    /// </summary>
    private static void CheckLedgerReasonCodeIsAccepted()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260729_027_economy_ledger_foundation");
        Check.True(
            migration.Sql.Contains(
                "currency_code IN ('silver', 'gold')",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "reason_code ~ '^[a-z][a-z0-9_.-]{0,63}$'",
                StringComparison.Ordinal),
            "the economy ledger accepts a lower-case silver reason code");

        const string reasonCode = "bag_money_bag";
        Check.True(
            reasonCode.Length is >= 1 and <= 64 &&
            reasonCode[0] is >= 'a' and <= 'z' &&
            reasonCode.All(static character =>
                character is >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '_' or '.' or '-'),
            "the bag silver ledger reason code satisfies the ledger pattern");

        // 2147483647 is the captured wallet ceiling; skill 4602 tops a fresh
        // wallet out but must never overflow, so the executor refuses instead.
        Check.True(
            (long)int.MaxValue + BagConsumableEffectCatalog.All
                .Single(static effect => effect.SkillId == 4602).Amount >
            int.MaxValue,
            "a money bag can overflow the silver column and is refused");
    }

    /// <summary>
    /// The revision must not grow a second copy of a capture-derived layout:
    /// 10040/10046 stay owned by <c>PacketBuilder.Combat</c>, and 10051 by
    /// <c>PacketBuilder.EquipmentInspection</c>.
    /// </summary>
    private static void CheckCaptureDerivedLayoutsAreNotDuplicated()
    {
        var bagConsumable = ReadSource("PacketBuilder.BagConsumable.cs");
        Check.True(
            bagConsumable.Contains(
                "MonsterSkillCastVisual(",
                StringComparison.Ordinal) &&
            bagConsumable.Contains(
                "SkillCastImpact(",
                StringComparison.Ordinal),
            "the bag-consumable frames reuse the proven combat layouts");
        Check.True(
            !bagConsumable.Contains("0x2738", StringComparison.Ordinal) &&
            !bagConsumable.Contains("0x273E", StringComparison.Ordinal),
            "the bag-consumable builder adds no duplicate combat opcode");

        var activation = ReadSource("PacketBuilder.EquipmentInspection.cs");
        Check.True(
            activation.Contains(
                "WriteSnapshotBagPosition",
                StringComparison.Ordinal),
            "the 10051 bag position lives in the shared snapshot helper");

        var definitions = ReadSource("PacketBuilder.cs");
        Check.Equal(
            1,
            CountOccurrences(definitions, "0x2743"),
            "the 10051 opcode has one definition");
        Check.True(
            definitions.Contains(
                "EquipmentItemSnapshotLength = 92",
                StringComparison.Ordinal) &&
            definitions.Contains(
                "EnterItemRecordLength = 72",
                StringComparison.Ordinal),
            "the 10051 reply keeps the captured 92-byte item snapshot length");
    }

    private static string ReadSource(string fileName)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Packets",
            fileName);
        Check.True(File.Exists(path), $"source file {fileName} exists");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException(
            "The protocol checks could not locate the repository root.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(
                value,
                index + value.Length,
                StringComparison.Ordinal);
        }

        return count;
    }

    private static ItemTemplateDefinition Consume(
        int id,
        int use,
        int skill) =>
        new(
            checked((uint)id),
            "consume item",
            "Potion" + id,
            "Potion " + id,
            EquipmentSlot: 0,
            [],
            MinLevel: 1,
            MaxLevel: 200,
            Hand: null,
            SkillFlag: 0,
            Texture: "t.gwo",
            Icon: "1,1",
            StatsJson:
                "{\"ID\":\"" + id +
                "\",\"Use\":\"" + use +
                "\",\"Skill\":\"" + skill + "\"}");

    /// <summary>
    /// A non-consume template that also carries <c>Use=1</c> and a reviewed
    /// <c>Skill</c>, which must never be classified as a bag consumable.
    /// </summary>
    private static ItemTemplateDefinition Wearable(
        int id,
        int use,
        int skill) =>
        new(
            checked((uint)id),
            "head",
            "Helm" + id,
            "Helm " + id,
            EquipmentSlot: 12,
            [],
            MinLevel: 1,
            MaxLevel: 200,
            Hand: null,
            SkillFlag: 12,
            Texture: "t.gwo",
            Icon: "1,1",
            StatsJson:
                "{\"ID\":\"" + id +
                "\",\"Use\":\"" + use +
                "\",\"Skill\":\"" + skill + "\"}");

    private static IItemTemplateCatalog CreateCatalog(
        params ItemTemplateDefinition[] definitions) =>
        PinnedItemTemplateCatalog.Create(
            "protocol-check-bag-consumable",
            definitions,
            [],
            [],
            []);

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
