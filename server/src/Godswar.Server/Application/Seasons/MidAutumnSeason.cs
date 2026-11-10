using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Seasons;

/// <summary>
/// W3 seasons: the Mid-autumn event (the admin F-29 "Mooncakes + Lunar Priest (as a Mid-autumn event)", F-26, F-134).
/// Everything here runs only while the "mid_autumn" season is open (public.seasonal_events, editor Events page) and
/// the hot switch season.mid_autumn is 1; the mooncakes' own EXP boosts are switch-gated only, so a mooncake made
/// during the season still works after it.
/// <list type="bullet">
/// <item>Lunar Priest (Athens_032 / Sparta_026, function 103, client NpcFunMoon0.lua): takes 1 / 3 / 5 / 10
/// Delicate Mooncakes (3920, the item every NF_L0_MOONF text names) for character EXP, Talent Points, pet EXP or
/// Zodiac energy. The client has the menu and the result lines but not the amounts: every amount is GENERATED.</item>
/// <item>Mooncake Portions 3800-3805 drop from any monster while the season is open (GENERATED rate); six of a kind
/// compound into the mooncake 3806-3811 (BijouForge MaterialType 6 NextID, ForgeCompoundPlanner).</item>
/// <item>A Delicate Mooncake drop (GENERATED rate), so the priest has cakes outside the voucher / gift packs.</item>
/// </list>
/// </summary>
internal static class MidAutumnSeason
{
    public const string Key = "mid_autumn";

    public const string SwitchKey = "season.mid_autumn"; // W3 seasons mid-autumn
    public const string PortionPercentKey = "season.mid_autumn_portion_percent"; // GENERATED
    public const string DelicatePercentKey = "season.mid_autumn_delicate_percent"; // GENERATED
    public const string ExperiencePerLevelKey = "season.mid_autumn_cake_exp_per_level"; // GENERATED
    public const string TalentPointsKey = "season.mid_autumn_cake_talent"; // GENERATED
    public const string PetExperiencePerLevelKey = "season.mid_autumn_cake_pet_exp_per_level"; // GENERATED
    public const string ZodiacEnergyKey = "season.mid_autumn_cake_zodiac"; // GENERATED

    public const uint DelicateMooncake = 3920;
    public const uint FirstPortion = 3800, LastPortion = 3805;

    public const int LunarPriestFunction = 103;

    // NpcFunMoon0.lua page 1: BUTTON1 EXP, BUTTON2 Talent Points, BUTTON3 Pet EXP, BUTTON4 Zodiac energy.
    public enum Reward { None = 0, Experience = 1, TalentPoints = 2, PetExperience = 3, ZodiacEnergy = 4 }

    // Result page (Index 3): NF_L0_S009 choose, MOON901 not enough cakes, MOON910 success, MOON902 pet not out.
    public const int ReplyChoose = 900, ReplyNotEnough = 901, ReplySuccess = 910, ReplyNoPet = 902;

    public static bool SwitchOn => ServerRates.Current.Get(SwitchKey) >= 1m;

    public static bool IsOpen(DateTimeOffset at) => SwitchOn && SeasonalEvents.IsOpen(Key, at);

    public static bool IsLunarPriest(string npcKey) => npcKey is "Athens_032" or "Sparta_026";

    /// <summary>Page 2 for a page-1 choice: its text + "Exchange 1" entry (100/110/120/130), then 3 / 5 / 10.</summary>
    public static int[] PageTwo(Reward reward) => [100 + 10 * ((int)reward - 1), 101, 102, 103];

    public static Reward RewardOf(int pageOneEntry) => pageOneEntry is >= 1 and <= 4 ? (Reward)pageOneEntry : Reward.None;

    /// <summary>Page 2 entry -> cakes: 100/110/120/130 = 1, 101 = 3, 102 = 5, 103 = 10. Also the reward the
    /// "Exchange 1" entry belongs to (the 101-103 entries are shared by all four pages).</summary>
    public static (int Cakes, Reward FromEntry) CakesOf(int pageTwoEntry) => pageTwoEntry switch
    {
        100 => (1, Reward.Experience),
        110 => (1, Reward.TalentPoints),
        120 => (1, Reward.PetExperience),
        130 => (1, Reward.ZodiacEnergy),
        101 => (3, Reward.None),
        102 => (5, Reward.None),
        103 => (10, Reward.None),
        _ => (0, Reward.None)
    };

    /// <summary>"The more Delicate Mooncakes you exchange at one time, the more rewards" (NF_L0_MOONF0100): 5 pay
    /// as 6 and 10 as 15 (GENERATED).</summary>
    public static int RewardUnits(int cakes) => cakes switch
    {
        5 => 6,
        10 => 15,
        _ => cakes
    };

    /// <summary>The reward for <paramref name="cakes"/> cakes at <paramref name="level"/> (rates read live).</summary>
    public static long Amount(Reward reward, int cakes, int level)
    {
        var units = (long)RewardUnits(cakes);
        var perCake = reward switch
        {
            Reward.Experience => (long)Rate(ExperiencePerLevelKey) * Math.Max(1, level),
            Reward.TalentPoints => Rate(TalentPointsKey),
            Reward.PetExperience => (long)Rate(PetExperiencePerLevelKey) * Math.Max(1, level),
            Reward.ZodiacEnergy => Rate(ZodiacEnergyKey),
            _ => 0
        };
        return Math.Min(100_000_000L, units * perCake);
    }

    private static int Rate(string key) => Math.Max(0, (int)ServerRates.Current.Get(key));

    /// <summary>
    /// Season drops from any monster while the season is open: one Mooncake Portion of a random kind (3800-3805,
    /// equal weights) at <paramref name="portionPercent"/>, a Delicate Mooncake at <paramref name="delicatePercent"/>
    /// (both GENERATED). Rolled from the death id's last four bytes, so the Thanksgiving roll (bytes 0-11) is
    /// independent.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> Drops(Guid deathEventId, DateTimeOffset at, int portionPercent,
        int delicatePercent)
    {
        if (deathEventId == Guid.Empty || !IsOpen(at)) yield break;
        foreach (var drop in Roll(deathEventId, portionPercent, delicatePercent)) yield return drop;
    }

    /// <summary>The roll alone (no season or switch check), for the checks.</summary>
    internal static IEnumerable<(uint ItemId, int Index)> Roll(Guid deathEventId, int portionPercent, int delicatePercent)
    {
        Span<byte> bytes = stackalloc byte[16];
        deathEventId.TryWriteBytes(bytes);
        var word = BitConverter.ToUInt32(bytes[12..16]);
        var mixed = unchecked((word ^ BitConverter.ToUInt32(bytes[0..4])) * 2_654_435_761u);
        var list = new List<(uint, int)>(2);
        if (word % 10_000 < Math.Clamp(portionPercent, 0, 100) * 100)
            list.Add((FirstPortion + word / 10_000 % 6, 0));
        if (mixed % 10_000 < Math.Clamp(delicatePercent, 0, 100) * 100)
            list.Add((DelicateMooncake, 1));
        return list;
    }

    public const string Category = "Seasonal events (on/off and dates: Events & notices page)";

    private static ServerRateDefinition Absolute(string key, string label, string unit, decimal value, decimal max,
        int order, string text) =>
        new(key, Category, label, unit, ServerRateKind.Absolute, value, 0m, max, 1m, order,
            "Application/Seasons/MidAutumnSeason.cs", text);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Absolute(SwitchKey, "Mid-autumn: Lunar Priest, mooncake drops and mooncake EXP", "0/1", 1m, 1m, 10,
            "1 = while the Mid-autumn season is open the Lunar Priest takes Delicate Mooncakes and monsters drop " +
            "Mooncake Portions; the Icy / Fruit / Tea / Crystal Mooncakes give their EXP bonus and the Bean / Red " +
            "Mooncakes their EXP whenever used. 0 = none of it (the mooncakes do nothing, as before)."),
        Absolute(PortionPercentKey, "Mid-autumn: Mooncake Portion drop chance", "%", 5m, 100m, 11,
            "While Mid-autumn is open, each monster killed anywhere drops one Mooncake Portion (random of the six) " +
            "this often (GENERATED). Six of a kind compound into a Mooncake (forge Gems tab)."),
        Absolute(DelicatePercentKey, "Mid-autumn: Delicate Mooncake drop chance", "%", 1m, 100m, 12,
            "Chance of a Delicate Mooncake (the Lunar Priest's item) from the same monsters (GENERATED)."),
        Absolute(ExperiencePerLevelKey, "Mid-autumn: Lunar Priest EXP per cake per level", "EXP", 1_000m, 1_000_000m, 13,
            "Character EXP for one Delicate Mooncake = this x character level (GENERATED). 5 cakes pay as 6, 10 as 15. " +
            "The events EXP rate applies on top."),
        Absolute(TalentPointsKey, "Mid-autumn: Lunar Priest Talent Points per cake", "TP", 10m, 10_000m, 14,
            "Talent Points for one Delicate Mooncake (GENERATED). 5 cakes pay as 6, 10 as 15."),
        Absolute(PetExperiencePerLevelKey, "Mid-autumn: Lunar Priest pet EXP per cake per level", "EXP", 2_000m,
            1_000_000m, 15,
            "Pet EXP for one Delicate Mooncake = this x character level, to the summoned pet (GENERATED). No pet out " +
            "= the priest refuses and keeps nothing."),
        Absolute(ZodiacEnergyKey, "Mid-autumn: Lunar Priest Zodiac energy per cake", "energy", 20m, 10_000m, 16,
            "Zodiac energy for one Delicate Mooncake, up to the storage limit of the character's Zodiac level " +
            "(GENERATED). A full store refuses the exchange and keeps the cakes.")
    ];
}
