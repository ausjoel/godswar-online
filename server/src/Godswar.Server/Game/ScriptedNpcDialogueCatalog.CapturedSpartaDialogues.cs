namespace Godswar.Server.Game;

/// <summary>
/// The scripted NPC dialogues the September 28 2026 reference capture pinned
/// down, registered against the single NPC each one belongs to.
/// </summary>
/// <remarks>
/// Both windows share a function number with an NPC that already had a
/// transcription, which is why they are separate entries rather than edits:
/// the routing table is keyed by the NPC, so each one advertises only its own
/// opening page and a button of one window can never be answered as the other's.
/// Every number below is the reference server's own - the capture session is
/// <c>c202c633-d9ea-4ea3-8c6b-fdb69e32cc53</c>, and each remark names the frame
/// time it came from.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The summer gift exchange envoy, <c>Sparta_115</c>, who trades a gift
    /// certificate for the summer package and dragon fragments for a magic jade.
    /// </summary>
    /// <remarks>
    /// Captured 2026-09-28 (session c202c633, npc 5112 = <c>Sparta_115</c>, local
    /// 01:58:58). The reference advertised <c>[200, 100, 101, 102]</c> and
    /// answered every click:
    ///
    ///   100 -> 200  <c>Summer_Page2_T1</c> "put the summer gift certificate in"
    ///   101 -> 201  <c>Summer_Page2_T2</c> plus the 兑换魔幻灵玉 button
    ///   102 -> 202  <c>Summer_Page2_T3</c> "put in any dragon fragment"
    ///   201 -> 303  <c>Summer_Page3_T4</c> "你身上并没有一套龙之碎片哦！"
    ///
    /// The client's <c>NpcFunSummerVacation.lua</c> draws page-one buttons for
    /// <c>100</c>/<c>101</c>/<c>102</c> only, so <c>200</c> is a page-two number
    /// the reference advertised on page one and the client draws nothing for it
    /// there; it is kept because the capture sent it. <c>201</c> is the second
    /// level - the client keeps <c>101</c> in <c>+16</c> and puts the chosen
    /// number in <c>+20</c> - and it is the one exchange the reference performed,
    /// answering with its own "no full set" line rather than a grant. The
    /// follow-up submission <c>102</c> sent was <c>0</c>, which no branch draws,
    /// so it is unanswered.
    /// </remarks>
    private static readonly ScriptedNpcDialogue SummerGiftEnvoyDialogue = new(
        FunctionNumber: 66,
        OpeningMenu: [200, 100, 101, 102],
        Steps: new Dictionary<int, int[]>
        {
            [100] = [200],
            [101] = [201],
            [102] = [202],

            // Second level: the 兑换魔幻灵玉 button on the 201 page.
            [201] = [303]
        });

    /// <summary>
    /// The Troy event awarder, <c>Sparta_124</c>, who hands out the event's
    /// guild and personal rewards and exchanges glory for zodiac energy.
    /// </summary>
    /// <remarks>
    /// Captured 2026-09-28 (session c202c633, npc 5121 = <c>Sparta_124</c>, local
    /// 02:02:06). The reference advertised <c>[101, 113]</c> and answered every
    /// click:
    ///
    ///   101 -> [201, 202, 203]  查询积分, 领取公会奖励, 领取个人奖励
    ///   113 -> 215              勇士的荣耀, with the 星座能量兑换 button
    ///   201 -> 311              目前暂无排名！ (no ranking yet)
    ///   215 -> 1099             the zodiac-energy exchange form
    ///
    /// <c>Sparta_125</c> owns the same function number 67 and keeps its own
    /// transcription; the two are registered separately so neither window can
    /// answer the other's buttons. Only the numbers this capture sent are
    /// registered, so a page the reference never opened stays unanswered.
    /// </remarks>
    private static readonly ScriptedNpcDialogue TroyEventAwarderDialogue = new(
        FunctionNumber: 67,
        OpeningMenu: [101, 113],
        Steps: new Dictionary<int, int[]>
        {
            [101] = [201, 202, 203],
            [113] = [215],

            // Second level.
            [201] = [311],
            [215] = [1099]
        });

    /// <summary>
    /// The battlefield awarder, <c>Sparta_073</c>, who hands out the battlefield
    /// and title rewards and answers the battlefield's own statistics queries.
    /// </summary>
    /// <remarks>
    /// Captured 2026-09-28 (session 0f68525b, npc 5070 = <c>Sparta_073</c>, local
    /// 02:54:12-02:56:14). The client's <c>NpcFunWar.lua</c> owns function
    /// <c>NPC_FLAG_SYS_WAR = 3</c>, and the reference advertised exactly that one
    /// number. Every level below is the reference's own reply, read off the
    /// capture; the labels are the client's own:
    ///
    ///   1 品都斯山战场 / 2 尼米尼山谷 / 3 利兰丁农场保卫战 / 4 帕纳萨斯山
    ///     1 -> 173 领取战场奖励, 174 领取称谓奖励, 175 领取个人奖励,
    ///          176 查询战场数据
    ///     2 -> 1967 战场（60-89）
    ///     3 -> 71 阵营奖励, 72 积分第一名, 73 克西丝的宠物
    ///     4 -> 215 不在领取奖励时间段 (a result line, not a page of buttons)
    ///   173/175/1967 -> 1755 "你没参加战场活动不能领取奖励!"
    ///   174 -> 3001 杀敌最多, 3002 得分最高, 3003 最佳治疗
    ///   176 -> 11763 积分前5名, 11764 杀敌积分前5名, 11765 两阵营积分,
    ///          11766 阵营积分
    ///   71 -> 201, 72/73 -> 204
    ///
    /// Two of those groups are answered <em>again</em> one level deeper, which is
    /// why this dialogue needs page-keyed selections: <c>3001</c>-<c>3003</c> and
    /// <c>11763</c>-<c>11766</c> are first the answer to <c>174</c>/<c>176</c> and
    /// then buttons of the page that answer opened. Keyed by the pair, a click on
    /// <c>3001</c> while page <c>174</c> is showing reaches its own result, and a
    /// click on <c>174</c> still reaches the page - the two cannot be confused.
    /// The deeper results are the reference's own numbers verbatim, including the
    /// trailing <c>17</c> and <c>-83</c>; nothing is granted here, because the
    /// capture only shows which line the reference drew for a character that had
    /// not taken part.
    /// </remarks>
    private static readonly ScriptedNpcDialogue BattlefieldAwarderDialogue = new(
        FunctionNumber: 3,
        OpeningMenu: [1, 2, 3, 4],
        Steps: new Dictionary<int, int[]>
        {
            // The four battlefields' own pages.
            [1] = [173, 174, 175, 176],
            [2] = [1967],
            [3] = [71, 72, 73],
            [4] = [215],

            // Claiming without having taken part, and the two enquiry pages.
            [173] = [1755],
            [174] = [3001, 3002, 3003],
            [175] = [1755],
            [176] = [11763, 11764, 11765, 11766],
            [1967] = [1755],

            // The reward groups of the farm defence battle.
            [71] = [201],
            [72] = [204],
            [73] = [204]
        },
        PageSelections: new Dictionary<int, IReadOnlyDictionary<int, int[]>>
        {
            // 领取称谓奖励: the three ranking queries, each answered with the
            // reference's own "you have not taken part" line.
            [174] = new Dictionary<int, int[]>
            {
                [3001] = [1755],
                [3002] = [1755],
                [3003] = [1755]
            },

            // 查询战场数据: the four statistics boards.
            [176] = new Dictionary<int, int[]>
            {
                [11763] = [170001, 114002, 100003, 100004, 100005, 17],
                [11764] = [101011, 100012, 100013, 100014, 100015, 17],
                [11765] = [100011, 100012, 100013, 100014, 100015, -83],
                [11766] = [184022, 405021]
            }
        });
}
