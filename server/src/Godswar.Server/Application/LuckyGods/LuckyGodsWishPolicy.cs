namespace Godswar.Server.Application.LuckyGods;

/// <summary>
/// The divine wish's own rules: what one correct guess adds to the prize pool, how
/// a streak grows, and how a number is turned into the <c>SubID</c> the client can
/// actually print.
/// </summary>
/// <remarks>
/// Every literal below is either quoted by the client or is a server-side economy
/// choice; the encodings are not. <c>NpcFunLuckyGods.lua</c> reads a number back as
/// <c>(SubID - tail) / 10</c> inside its text, so the value travels in the digits
/// above the tail and no number above <c>int.MaxValue / 10</c> can be shown at all.
/// That ceiling is what caps the streak at seven.
/// </remarks>
internal static class LuckyGodsWishPolicy
{
    /// <summary>
    /// What the first correct guess of a streak wins. A server-side choice; the
    /// client never states an amount, it only prints the number it is sent.
    /// </summary>
    public const int BaseExperience = 210_000;

    /// <summary>
    /// The longest streak the wish keeps counting. The client's own line for it is
    /// <c>NF_L0_L022</c>, "居然连续7次选中了好心情的那个神", which is printed as a
    /// result rather than a menu, so a seventh correct guess ends the round.
    /// </summary>
    public const int MaximumStreak = 7;

    /// <summary>
    /// The prize pool at the end of a full streak.
    /// </summary>
    public const int CeilingExperience = 13_490_000;

    /// <summary>
    /// Wishes a character may make per realm day, from the client's rules line
    /// <c>NF_L0_L001</c>: "每天可以许愿10次".
    /// </summary>
    public const int DailyLimit = 10;

    /// <summary>
    /// The wait between two wishes, from the same line: "每5分钟可以许愿1次".
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The level the wish demands, from <c>NF_L0_L012</c>: "必须达到55级以上".
    /// </summary>
    public const int MinimumLevel = 55;

    /// <summary>
    /// The odds one guess is accepted, in percent. The client only says the gods'
    /// moods change (<c>NF_L0_L001</c>), so this is a server-side choice.
    /// </summary>
    public const int HitChancePercent = 80;

    /// <summary>
    /// Whether the drawn roll accepts the wish. The caller draws with
    /// <c>Random.Shared.Next(100)</c>, so every roll below the chance hits.
    /// </summary>
    public static bool IsHit(int roll) => roll < HitChancePercent;

    /// <summary>
    /// The pool after <code>streak</code> consecutive correct guesses: double the
    /// previous pool, which is the client's own promise in <c>NF_L0_L001</c>
    /// ("每次都选到愿意给你奖励的神都会将当前奖励翻倍"), except that the seventh
    /// correct guess lands on the ceiling instead of the doubled sixth. A streak of
    /// zero is the state after a wrong guess, which pays nothing: the miss costs the
    /// whole pot, so the ladder restarts from an empty pool.
    /// </summary>
    public static int PoolAfter(int streak) =>
        streak switch
        {
            <= 0 => 0,
            >= MaximumStreak => CeilingExperience,
            _ => BaseExperience * (1 << (streak - 1))
        };

    /// <summary>
    /// A correct guess that lets the player keep going. The client prints
    /// <c>NF_L0_L004</c> + the value + <c>NF_L0_L014</c>, "如果马上领取，你将获得 N
    /// 经验值", and draws the button that continues the streak.
    /// </summary>
    public static int ContinuedSubId(int experience) =>
        Encode(experience, tail: 9);

    /// <summary>
    /// The Apollo button as the script draws it past the first page:
    /// <c>SubID % 100 == 4</c> raises <c>NF_L0_L003</c> at <c>25,155</c>.
    /// </summary>
    public const int ApolloButtonSubId = 104;

    /// <summary>
    /// The claim button as the script draws it past the first page:
    /// <c>SubID % 100 == 5</c> raises <c>NF_L0_L007</c> at <c>25,175</c>.
    /// </summary>
    public const int ClaimButtonSubId = 105;

    /// <summary>
    /// The whole answer to one guess. A win has to carry its two sibling buttons in
    /// the same frame: outside the script's first page the tail-9 family only draws
    /// the Poseidon button, so a single number would pop a window with one button
    /// instead of the three the player expects. A loss and the seventh win are both
    /// <c>EndMessage</c> results, and a result may never share a frame with buttons
    /// because the client then treats the entire frame as a result and closes it.
    /// </summary>
    public static int[] ReplyFor(int streak, int pool, int wishesRemaining) =>
        streak switch
        {
            <= 0 => [MissedSubId(wishesRemaining)],
            >= MaximumStreak => [CompletedSubId(pool)],
            _ => [ContinuedSubId(pool), ApolloButtonSubId, ClaimButtonSubId]
        };

    /// <summary>
    /// Whether a number is one of the two god buttons. <c>NF_L0_L002</c> is drawn by
    /// <c>100</c> and by the tail-9 family, <c>NF_L0_L003</c> by <c>101</c> and by
    /// the <c>% 100 == 4</c> family; both echo their own number back on click.
    /// </summary>
    public static bool IsGuessClick(int subId) =>
        subId is 100 or 101 || subId % 10 == 9 || subId % 100 == 4;

    /// <summary>
    /// Whether a number is the claim button: <c>NF_L0_L007</c>, drawn by
    /// <c>1000</c> on the first page and by the <c>% 100 == 5</c> family later.
    /// </summary>
    public static bool IsClaimClick(int subId) =>
        subId is 1000 || subId % 100 == 5;

    /// <summary>
    /// The seventh correct guess. The client prints <c>NF_L0_L022</c> + the value +
    /// <c>NF_L0_L023</c> and closes the window, which is why the prize still has to
    /// be claimed from the pool rather than paid out here.
    /// </summary>
    public static int CompletedSubId(int experience) =>
        Encode(experience, tail: 2);

    /// <summary>
    /// A wrong guess: the streak and the pool are gone, so the honest line is
    /// <c>NF_L0_L008</c> + the value + <c>NF_L0_L015</c>, "你今天还能许愿 N 次".
    /// <c>N</c> is the wishes left today, and zero is a real answer - the client
    /// prints "0 次" and closes.
    /// </summary>
    public static int MissedSubId(int wishesRemaining) =>
        Encode(wishesRemaining, tail: 8);

    /// <summary>
    /// A wish attempted inside the five-minute wait:
    /// <c>NF_L0_L017</c> + the value + <c>NF_L0_L018</c>, "请再过 N 分钟之后再来".
    /// </summary>
    public static int WaitingSubId(int minutesRemaining) =>
        Encode(minutesRemaining, tail: 6);

    /// <summary>
    /// A paid claim. <c>NF_L0_L011</c> + the value + <c>NF_L0_L015</c> reads "这些
    /// 奖励你就拿去吧。你今天还能许愿 N 次", which is the script's own line for
    /// taking the prize and stopping, and it closes the window. The pool is gone by
    /// then, so <c>201</c> ("你暂时还没有任何奖励") would only contradict the
    /// experience the player has just been handed.
    /// </summary>
    public static int ClaimedSubId(int wishesRemaining) =>
        Encode(wishesRemaining, tail: 7);

    private static int Encode(int value, int tail)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return checked(value * 10 + tail);
    }
}
