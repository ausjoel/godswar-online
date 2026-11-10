namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The Wishing Pool's second service, <c>NPC_FLAG_SYS_LOSTBOOK = 24</c>. The
    /// client names the entry "Make a wish with the Lost Book" and the reference
    /// server advertised it beside the skill-book wish, so the pool answers three
    /// wish kinds, one per advertised function number: 16 skill book, 24 lost book,
    /// 50 divine.
    /// </summary>
    private const int WishingPoolLostBookPage = 24;

    /// <summary>
    /// The divine wish's own page. Its dialogue and its economy live in
    /// <c>GameClientHandler.LuckyGods.cs</c> and
    /// <c>Application/LuckyGods/LuckyGodsWishPolicy.cs</c>.
    /// </summary>
    private const int WishingPoolLuckyGodsPage = 50;

    /// <summary>
    /// The lost-book page's opening answer, which is the reference server's own:
    /// <c>10070 {5212, 24, 101, 1, 2}</c>.
    /// </summary>
    /// <remarks>
    /// <c>101</c> is the header line of <c>NpcFunLostBook.lua</c> rather than a
    /// button - its branch watches <c>SubID % 2000 == 101</c> and prints the wishes
    /// left as <c>10 - (SubID - 101) / 2000</c>, so a plain <c>101</c> reads "you
    /// still have 10 wishes left". <c>1</c> and <c>2</c> are the page's two
    /// buttons, "*Use Incomplete Lost Book" and "*Use Lost Book".
    /// </remarks>
    private static readonly int[] WishingPoolLostBookMenu = [101, 1, 2];

    /// <summary>
    /// Both lost-book buttons lead to the wish itself, which this server does not
    /// run, so they are answered with the script's own missing-book line
    /// (<c>NF_L0_BK601</c>). It is a result, so the window closes on it.
    /// </summary>
    private static readonly int[] WishingPoolLostBookNoBook = [601];
}
