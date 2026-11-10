using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// One hound-egg tier the Advance Troop Captain accepts.
/// </summary>
/// <remarks>
/// A captured egg carries the pet aptitude it will hatch as: the hatching path
/// reads <c>egg.Quality</c> straight into <c>PetContent.TryGetAptitude</c>, and
/// the capture path rolls that quality when the egg is created
/// (<c>RollCapturedEggQualityAsync</c>). The donation award therefore follows
/// that aptitude, which is also what the activity's own briefing means by
/// "卵的品质越高奖励越丰厚".
/// </remarks>
internal readonly record struct LelantineFarmEggTier(
    int ItemId,
    string DisplayName);

/// <summary>
/// The farm's donation and faction-point rules, kept in one place so the
/// dialogue, the store and the tests read the same numbers.
/// </summary>
internal static class LelantineFarmPointsPolicy
{
    /// <summary>
    /// The faction the activity's own per-faction totals are kept for. They are
    /// the two camps the character row already carries
    /// (<c>GameDefaults.SpartaCamp</c> / <c>AthensCamp</c>).
    /// </summary>
    public const byte SpartaFaction = 0;

    /// <summary>The Athenian faction index.</summary>
    public const byte AthensFaction = 1;

    /// <summary>
    /// The hound egg. <c>10159</c> is the only egg item the farm's donation
    /// branch names.
    /// </summary>
    public const int HoundEggItemId = 10159;

    /// <summary>
    /// The egg tiers the farm accepts, in the order the donation page lists
    /// them.
    /// </summary>
    public static readonly LelantineFarmEggTier[] EggTiers =
    [
        new(HoundEggItemId, "忠犬卵")
    ];

    /// <summary>
    /// FINALISE-PETS (the admin 2026-11-05): the three Loyal Dog natures the farm scores, named as the English
    /// client names them (<c>Localization/en_us/Settings/Sys/ItemColor.xml</c>): aptitude <c>3</c> =
    /// "Cowardly", <c>5</c> = "Rational", <c>10</c> = "Brave". A captured farm dog rolls one of these three
    /// (<see cref="LelantineFarmDogPolicy.RollNature"/>), so every captured egg is donatable.
    /// Replaces the earlier 1/5/8 rungs (1/10/100 points); live held no 1 or 8 eggs on 2026-11-05.
    /// </summary>
    public const short CowardlyEggAptitude = 3;

    /// <summary>The Rational egg, client aptitude <c>5</c>.</summary>
    public const short RationalEggAptitude = 5;

    /// <summary>The Brave egg, client aptitude <c>10</c>.</summary>
    public const short BraveEggAptitude = 10;

    /// <summary>What one donated Cowardly egg is worth (the admin 2026-11-05).</summary>
    public const int CowardlyEggPoints = 10;

    /// <summary>What one donated Rational egg is worth (the admin 2026-11-05).</summary>
    public const int RationalEggPoints = 30;

    /// <summary>What one donated Brave egg is worth (the admin 2026-11-05).</summary>
    public const int BraveEggPoints = 150;

    /// <summary>
    /// GENERATED: pet experience paid to the summoned pet per donated point (Cowardly 10,000, Rational
    /// 30,000, Brave 150,000 per egg). The guide and the captain's text ("a bunch of experience", "points
    /// and pet experience") give no number. No summoned pet = the points are booked and the pet
    /// experience is forfeited, as the Awarder's own warning (<c>NF_L0_FRAM374</c>) does for rewards.
    /// </summary>
    public const int PetExperiencePerPoint = 1_000;

    /// <summary>
    /// The award one egg of the given pet aptitude is worth when donated. Only the three natures above
    /// score; any other quality is refused rather than booked at an invented value.
    /// </summary>
    public static bool TryResolveEggDonationPoints(short aptitude, out int points)
    {
        points = aptitude switch
        {
            CowardlyEggAptitude => CowardlyEggPoints,
            RationalEggAptitude => RationalEggPoints,
            BraveEggAptitude => BraveEggPoints,
            _ => 0
        };
        return points != 0;
    }

    /// <summary>
    /// Resolves one egg item to its tier.
    /// </summary>
    public static bool TryResolveTier(int itemId, out LelantineFarmEggTier tier)
    {
        foreach (var candidate in EggTiers)
        {
            if (candidate.ItemId == itemId)
            {
                tier = candidate;
                return true;
            }
        }

        tier = default;
        return false;
    }

    /// <summary>
    /// The faction index a camp maps to. A camp outside the two known values is
    /// not an activity participant.
    /// </summary>
    public static bool TryResolveFaction(byte camp, out byte faction)
    {
        faction = camp switch
        {
            SpartaFaction => SpartaFaction,
            AthensFaction => AthensFaction,
            _ => byte.MaxValue
        };
        return faction != byte.MaxValue;
    }

    /// <summary>
    /// The largest single donation the client script's own input box accepts.
    /// Its refusal line is "每次输入值必须在1~99"
    /// (<c>NF_L0_FRAM453</c>), which is the range this server enforces.
    /// </summary>
    public const int MaximumDonationQuantity = 99;

    /// <summary>
    /// Turns a proposed donation count into an accepted one. The client script
    /// words its own refusal as "每次输入值必须在1~99"
    /// (<c>NF_L0_FRAM453</c>), which is the range this server enforces.
    /// </summary>
    public static bool IsAcceptedQuantity(int quantity) =>
        quantity is >= 1 and <= MaximumDonationQuantity;

    /// <summary>
    /// Whether the item is one of the eggs the donation branch accepts.
    /// </summary>
    public static bool IsDonationEgg(int itemId) =>
        TryResolveTier(itemId, out _);

    /// <summary>
    /// The tuck-net handout. Both numbers come from the activity's own text:
    /// 40 wooden nets on the first claim (<c>NF_L0_FRAM204</c>) and 5 on every
    /// top-up (<c>NF_L0_FRAM203</c>).
    /// </summary>
    public const int InitialNetCount = 40;

    /// <summary>The top-up handed out once the first 40 are gone.</summary>
    public const int TopUpNetCount = 5;

    /// <summary>
    /// The wooden tuck net: 木质网兜 / <c>Wooden Net</c> in the client's
    /// <c>EquipName.dat</c>, published as
    /// <c>&lt;Pet10080 ID="10080" Type="consume item" ... Overlap="99"
    /// Use="1" Skill="4730"&gt;</c>. The client's own capture skill 4730 is the
    /// pet-capture skill the net casts.
    /// </summary>
    public const int WoodenNetItemId = 10080;

    /// <summary>
    /// What one credited farm kill is worth, by the monster's own rank.
    /// </summary>
    /// <remarks>
    /// The activity's briefing line publishes these three numbers itself
    /// (<c>NF_L0_FRAM462</c>, and its Spartan twin <c>NF_L0_FRAM062</c>):
    /// "击杀不低于自身10级的普通怪物，玩家可以得到1分" (a normal monster is 1
    /// point), "击杀不低于自身10级的精英怪物，玩家可以得到10分" (an elite is
    /// 10), and "击杀后可以得到1000分" for the level-130 hound boss (1000).
    /// </remarks>
    public const int NormalKillPoints = 1;

    /// <summary>An elite farm monster's award.</summary>
    public const int EliteKillPoints = 10;

    /// <summary>The hound boss' award.</summary>
    public const int BossKillPoints = 1000;

    /// <summary>
    /// Resolves the award for one farm monster from its published rank. The
    /// elite and boss flags win over the rank text because the farm's own rows
    /// set both (<c>IsElite</c> true with rank <c>elite</c> for the rabid dogs,
    /// <c>IsBoss</c> true with rank <c>boss</c> for Cerberus).
    /// </summary>
    public static int ResolveKillPoints(
        string rank,
        bool isElite,
        bool isBoss)
    {
        if (isBoss || string.Equals(rank, "boss", StringComparison.Ordinal))
        {
            return BossKillPoints;
        }

        return isElite || string.Equals(rank, "elite", StringComparison.Ordinal)
            ? EliteKillPoints
            : NormalKillPoints;
    }

    /// <summary>
    /// How far below the attacker a normal farm monster may be and still score.
    /// </summary>
    /// <remarks>
    /// The activity's own rules state the gap for normal monsters only: "击杀不低于
    /// 自身10级的普通怪物，玩家可以得到1分". The elite and boss rules state no gap
    /// at all ("击杀不低于自身10级的精英怪物" carries the same wording but the boss
    /// line is unconditional), so this gate applies to the normal award alone.
    /// </remarks>
    public const int NormalMonsterMaximumLowerLevelGap = 10;

    /// <summary>
    /// Whether one farm kill is worth its award to this attacker.
    /// </summary>
    /// <param name="playerLevel">The attacking character's level.</param>
    /// <param name="monsterLevel">
    /// The monster's own level, which the runtime carries as its tier.
    /// </param>
    /// <param name="isElite">Whether the monster is an elite.</param>
    /// <param name="isBoss">Whether the monster is a boss.</param>
    public static bool IsKillEligible(
        int playerLevel,
        int monsterLevel,
        bool isElite,
        bool isBoss)
    {
        if (isElite || isBoss)
        {
            return true;
        }

        if (monsterLevel < 1)
        {
            return false;
        }

        return (long)Math.Max(1, playerLevel) - monsterLevel <=
            NormalMonsterMaximumLowerLevelGap;
    }
}
