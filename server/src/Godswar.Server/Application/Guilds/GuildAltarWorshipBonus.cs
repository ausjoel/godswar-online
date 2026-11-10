namespace Godswar.Server.Application.Guilds;

/// <summary>
/// One member's offering balance on one altar, with the level that altar
/// building currently stands at in the guild.
/// </summary>
/// <param name="BuildingType">
/// The altar's building type, which is also the client's own altar number
/// (<c>Consortia.xml</c> ships the ten altars as client ids 10-19).
/// </param>
/// <param name="Points">
/// The offering points that altar still holds for this member, which is what the
/// member's own layer of the bonus is computed from.
/// </param>
/// <param name="AltarLevel">
/// The level the guild has built this altar to, or 0 when the guild has not
/// built it. It picks which of the altar's own base values applies; the offering
/// points decide how much of it is paid.
/// </param>
internal readonly record struct GuildAltarWorshipBalance(
    int BuildingType,
    long Points,
    int AltarLevel);

/// <summary>
/// What the client calls a <c>WorshipImpactType</c>: which attribute an altar
/// level grants.
/// </summary>
/// <remarks>
/// <para>
/// The numbering is the client's, from <c>Consortia.xml</c>'s
/// <c>BuildingImpact</c> block, and it is <em>not</em> contiguous. The names come
/// from pairing each <c>WorshipImpactType</c> with the same altar's two entries
/// in <c>AltarSuspendConfig.lua</c>, whose <c>Effect1</c>/<c>Effect2</c> name
/// their attributes through <c>text.lua</c>'s <c>ASC_L0_*</c>. Both tables give
/// every altar the same pair of attributes with different numbers, and the
/// pairing is what identifies a type: altar 13 is physical attack plus hit,
/// altar 17 is damage absorption plus magic defence, altar 18 is physical defence
/// plus damage absorption, and so on. Type 4 being physical attack is separately
/// confirmed by that altar's level-twelve value of 540.
/// </para>
/// <para>
/// Only the types the ten altars actually use appear here. A type the client
/// never puts on an altar is left out rather than guessed at.
/// </para>
/// </remarks>
internal enum GuildWorshipAttribute
{
    /// <summary>No worship bonus at all.</summary>
    None = -1,

    /// <summary>MP上限.</summary>
    MaxMp = 1,

    /// <summary>HP回复.</summary>
    HpRecovery = 2,

    /// <summary>HP上限.</summary>
    MaxHp = 3,

    /// <summary>物理攻击.</summary>
    PhysicalAttack = 4,

    /// <summary>物理防御.</summary>
    PhysicalDefense = 6,

    /// <summary>魔法防御.</summary>
    MagicDefense = 7,

    /// <summary>魔法攻击.</summary>
    MagicAttack = 8,

    /// <summary>命中.</summary>
    Hit = 9,

    /// <summary>躲避.</summary>
    Dodge = 10,

    /// <summary>伤害吸收.</summary>
    DamageAbsorb = 14
}

/// <summary>
/// Names the attribute each altar grants, the way the client's own altar list
/// names it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by the altar's building type, deliberately, because the
/// <c>WorshipImpactType</c> ids cannot be decoded.</b> Every arithmetic reading of
/// them was tested against the client's own altar window and none fits: the raw id
/// matches no attribute, and neither does id+1 nor id+2 (best fit 2 of 10). The
/// ids are therefore not the client's <c>ASC_L0_*</c> attribute index in any
/// offset, and a table keyed by them can only be guessed at - three successive
/// guesses here were each wrong in play.
/// </para>
/// <para>
/// The altar window writes the attribute next to each altar's name, and that is
/// the authoritative statement of what an altar grants. The altars are fixed
/// content, so one entry per altar is sufficient, and it is checkable against the
/// window instead of against a number.
/// </para>
/// <para>
/// The list the window shows, altar by altar: 阿波罗 MP上限 / 德墨忒耳 HP回复 /
/// 赫耳墨斯 HP上限 / 赫淮斯托斯 物理攻击 / 哈迪斯 命中 / 狄俄尼索斯 物理防御 /
/// 阿芙洛狄忒 魔法防御 / 赫斯提亚 伤害吸收 / 波塞冬 魔法攻击 / 阿尔忒弥斯 闪避.
/// 物理攻击 is separately confirmed by that altar's level-twelve base value of 540,
/// the number the client's own example quotes.
/// </para>
/// <para>
/// The raw <c>WorshipImpactType</c> each altar carries is still checked (see
/// <see cref="ExpectedType"/>) so that a client update which renumbers an altar is
/// refused rather than paid the wrong attribute.
/// </para>
/// <para>
/// Only the attribute the altar window labels is mapped. Each altar also carries a
/// second effect (<c>AltarSuspendConfig.lua</c>'s <c>Effect2</c>) that the client
/// never names, so no second attribute is invented for it.
/// </para>
/// </remarks>
internal static class GuildWorshipAttributes
{
    /// <summary>
    /// The attribute this altar grants, or
    /// <see cref="GuildWorshipAttribute.None"/> when it grants none.
    /// </summary>
    /// <param name="buildingType">The altar's building type (client ids 10-19).</param>
    /// <param name="worshipImpactType">
    /// The type the content tables recorded for this altar, which must agree with
    /// the altar list; a mismatch is refused rather than guessed at.
    /// </param>
    public static GuildWorshipAttribute ForAltar(
        int buildingType,
        int worshipImpactType) =>
        ExpectedType(buildingType) == worshipImpactType
            ? AttributeOf(buildingType)
            : GuildWorshipAttribute.None;

    /// <summary>
    /// The attribute each of the ten altars grants, as the altar window labels it.
    /// </summary>
    private static GuildWorshipAttribute AttributeOf(int buildingType) =>
        buildingType switch
        {
            10 => GuildWorshipAttribute.MaxMp,
            11 => GuildWorshipAttribute.HpRecovery,
            12 => GuildWorshipAttribute.MaxHp,
            13 => GuildWorshipAttribute.PhysicalAttack,
            14 => GuildWorshipAttribute.Hit,
            15 => GuildWorshipAttribute.PhysicalDefense,
            16 => GuildWorshipAttribute.MagicDefense,
            17 => GuildWorshipAttribute.DamageAbsorb,
            18 => GuildWorshipAttribute.MagicAttack,
            19 => GuildWorshipAttribute.Dodge,
            _ => GuildWorshipAttribute.None
        };

    /// <summary>
    /// The <c>WorshipImpactType</c> each altar carries in the content tables, as
    /// read from <c>Consortia.xml</c>. Kept beside the names above so a client
    /// update that renumbers an altar is caught instead of silently paying the
    /// wrong attribute.
    /// </summary>
    private static int ExpectedType(int buildingType) => buildingType switch
    {
        10 => 1,
        11 => 2,
        12 => 0,
        13 => 4,
        14 => 8,
        15 => 5,
        16 => 7,
        17 => 14,
        18 => 6,
        19 => 9,
        _ => -1
    };
}

/// <summary>
/// What an altar adds to a character's attributes: the member's own offering
/// points plus the guild's own altar level.
/// </summary>
internal sealed record GuildAltarAttributeBonus(
    int MaxHp = 0,
    int MaxMp = 0,
    int HpRecovery = 0,
    int MpRecovery = 0,
    int PhysicalAttack = 0,
    int MagicAttack = 0,
    int PhysicalDefense = 0,
    int MagicDefense = 0,
    int Hit = 0,
    int Dodge = 0,
    int DamageAbsorb = 0)
{
    public static readonly GuildAltarAttributeBonus None = new();

    public bool IsEmpty => this == None;
}

/// <summary>
/// The client's own altar content: which attribute each altar level grants, and
/// what its base value is.
/// </summary>
/// <remarks>
/// Both come from <c>Consortia.xml</c>'s <c>BuildingImpact</c> block, seeded into
/// <c>guild_building_levels.worship_impact_type</c> and <c>.worship_impact_value</c>.
/// Declared as an interface so a caller that has to recompute a character's bonus
/// (the hourly drain settlement, for one) can do it without reaching for the
/// database itself.
/// </remarks>
internal interface IGuildAltarContent
{
    /// <summary>
    /// This altar level's raw <c>WorshipImpactType</c> and its base value, or a
    /// type that names no attribute when the level has no content.
    /// </summary>
    (int Type, int Value) ContentOf(int buildingType, int altarLevel);
}

/// <summary>
/// Turns offering points into the attributes an altar grants.
/// </summary>
/// <remarks>
/// <para>
/// <c>NF_L0_GH87</c> is the whole rule: "供奉越多，从祭坛上获取的百分比加成越多，最高不超过
/// 150%。但是消耗增加速度更快。例如50000供奉可以获取50%基础值". An altar grants a
/// percentage <em>of its own base value</em> (<c>Consortia.xml</c>'s
/// <c>WorshipImpactValue</c> for the level the guild has built the altar to), and
/// that percentage runs from nothing to 150%.
/// </para>
/// <para>
/// <b>The percentage is the multiplier on the base value, not an addition to it.</b>
/// A full 1000000 offering pays 150% of the base - so the altar whose base is 540
/// pays 810, not 540 plus half of it again. Twenty steps of 7.5% reach that
/// ceiling, so the client's quoted 50000 is one step.
/// </para>
/// <para>
/// <b>An altar that holds no offering points grants nothing.</b> There is no
/// standing gift for having built the altar or for having offered there in the
/// past: the points an altar currently holds are the only thing that buys its
/// attribute, so a balance drained to zero stops paying and a later offering
/// starts paying again.
/// </para>
/// <para>
/// The percentage at each step is <see cref="PercentPerStep"/>, and the steps are
/// the user's own reading of the client's two figures (50000 to the first step,
/// 1000000 to the ceiling), so re-cutting the curve means changing that one
/// constant.
/// </para>
/// </remarks>
internal static class GuildAltarWorshipBonusPolicy
{
    /// <summary>Offering points that buy one step of the bonus.</summary>
    public const long PointsPerStep = 50_000;

    /// <summary>Offering points at which the bonus reaches its ceiling.</summary>
    public const long MaxPoints = 1_000_000;

    /// <summary>
    /// The ceiling the client names: a full offering pays this percentage of the
    /// altar's base value.
    /// </summary>
    public const double MaxPercent = 150;

    /// <summary>
    /// Percentage of the altar's base value that one step buys: twenty steps
    /// reach the ceiling.
    /// </summary>
    public const double PercentPerStep = MaxPercent / 20.0;

    /// <summary>GUILD: the guide's 1,000 offering points per 1% of the base.</summary>
    public const double GuidePointsPerPercent = 1_000;

    /// <summary>Steps the ceiling is reached in.</summary>
    public const int MaxSteps = (int)(MaxPoints / PointsPerStep);

    /// <summary>
    /// The percentage of the altar's base value a balance currently buys, from 0
    /// for an empty altar to the client's 150% ceiling.
    /// </summary>
    public static double BonusPercent(long points) =>
        BonusPercent(points, GuildRateKeys.On(GuildRateKeys.AltarCurve));

    /// <summary>
    /// GUILD (2026-11-03): <paramref name="guideCurve"/> selects the IGG guide's
    /// curve (igg-acid=67): "1000 point offerings will increase the relevant
    /// ability by 1%", "Altar Bonus: Basic bonus X (God Altar Bonus +
    /// Offering/100000), the upper limit ... is 1.5 times ... the Basic bonus".
    /// That is linear from the first point and reaches the 150% ceiling at
    /// 150,000 points; it also agrees with the client's own NF_L0_GH87 example
    /// ("50000 offering gives 50% of the base"). The God Altar term is 0: the
    /// guide gives no number for it and the client content carries no worship
    /// value for building 30 (UNPROVEN, see the guild README). Every balance gets
    /// at least as much as under the old curve.
    /// </summary>
    internal static double BonusPercent(long points, bool guideCurve)
    {
        if (points <= 0)
        {
            return 0;
        }

        if (guideCurve)
        {
            return Math.Min(points / GuidePointsPerPercent, MaxPercent);
        }

        var steps = Math.Min(points / PointsPerStep, MaxSteps);
        return steps * PercentPerStep;
    }

    /// <summary>
    /// What a balance of this many points buys from an altar whose base value is
    /// <paramref name="impactValue"/>.
    /// </summary>
    /// <remarks>
    /// The percentage is applied to the whole base value before rounding, so a
    /// partial step still pays rather than being lost.
    /// </remarks>
    public static int BonusFor(long points, int impactValue)
    {
        if (points <= 0 || impactValue <= 0)
        {
            return 0;
        }

        return (int)Math.Round(
            impactValue * BonusPercent(points) / 100.0,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Every altar's contribution to one character, summed by attribute.
    /// </summary>
    /// <remarks>
    /// Each altar is its own account, and each pays the percentage its own
    /// offering points have bought against the base value its own built level
    /// carries. An altar whose balance has been drained to zero pays nothing at
    /// all, which is why there is no standing "guild" layer: the level picks which
    /// base value applies, and the points decide whether any of it is paid.
    /// </remarks>
    /// <param name="balances">
    /// One entry per altar the character has offered to.
    /// </param>
    /// <param name="contentOf">
    /// The client's content for one altar at one level: its raw
    /// <c>WorshipImpactType</c> and the base value the percentage is taken from.
    /// It answers a type that names no attribute for an altar level that has no
    /// content, which is what an unbuilt altar is.
    /// </param>
    public static GuildAltarAttributeBonus Resolve(
        IEnumerable<GuildAltarWorshipBalance> balances,
        Func<int, int, (int Type, int Value)> contentOf)
    {
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(contentOf);

        var total = GuildAltarAttributeBonus.None;
        foreach (var balance in balances)
        {
            // An empty altar pays nothing, and neither does one the member has
            // never offered at.
            if (balance.Points <= 0)
            {
                continue;
            }

            // The client's content is keyed by its raw type id, and the altar
            // list is what names the attribute; both have to agree.
            var (type, value) = contentOf(
                balance.BuildingType,
                balance.AltarLevel);
            var attribute = GuildWorshipAttributes.ForAltar(
                balance.BuildingType,
                type);
            if (attribute == GuildWorshipAttribute.None)
            {
                continue;
            }

            total = Accumulate(total, attribute, BonusFor(balance.Points, value));
        }

        return total;
    }

    private static GuildAltarAttributeBonus Accumulate(
        GuildAltarAttributeBonus total,
        GuildWorshipAttribute attribute,
        int amount)
    {
        if (amount <= 0)
        {
            return total;
        }

        return attribute switch
        {
            GuildWorshipAttribute.MaxHp =>
                total with { MaxHp = total.MaxHp + amount },
            GuildWorshipAttribute.MaxMp =>
                total with { MaxMp = total.MaxMp + amount },
            GuildWorshipAttribute.HpRecovery =>
                total with { HpRecovery = total.HpRecovery + amount },
            GuildWorshipAttribute.PhysicalAttack =>
                total with { PhysicalAttack = total.PhysicalAttack + amount },
            GuildWorshipAttribute.MagicAttack =>
                total with { MagicAttack = total.MagicAttack + amount },
            GuildWorshipAttribute.PhysicalDefense =>
                total with { PhysicalDefense = total.PhysicalDefense + amount },
            GuildWorshipAttribute.MagicDefense =>
                total with { MagicDefense = total.MagicDefense + amount },
            GuildWorshipAttribute.Hit =>
                total with { Hit = total.Hit + amount },
            GuildWorshipAttribute.Dodge =>
                total with { Dodge = total.Dodge + amount },
            GuildWorshipAttribute.DamageAbsorb =>
                total with { DamageAbsorb = total.DamageAbsorb + amount },
            _ => total
        };
    }
}
