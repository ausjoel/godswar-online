namespace Godswar.Server.Game;

/// <summary>
/// Scripted NPC dialogues beyond the capital set, transcribed from the client's
/// own <c>NpcFun</c> scripts under the same rules: numbers only from a script's own
/// <c>SubID</c> branch, one group per step, nothing sent for a branch whose text key
/// the shipped client does not define, and no answer where the only basis for
/// choosing one is its wording.
/// </summary>
/// <remarks>
/// Six candidate NPCs were examined and rejected outright, which is a spec outcome
/// rather than unfinished work:
///
///   - <c>Sparta_141</c>/<c>Athens_141</c> Faction Changer,
///     <c>NpcFunMigration.lua</c> (<c>NPC_FLAG_SYS_MIGRATION = 112</c>): all nine
///     <c>Migration1</c>-<c>Migration9</c> keys are undefined anywhere in the shipped
///     <c>Localization/zh_cn</c> tree, so every branch calls <c>SetText(nil)</c> and
///     opens an empty window. <c>105</c> and <c>106</c> also share <c>25,135</c>.
///   - <c>Sparta_052</c>/<c>Athens_052</c> "[Exchange]Mentor" against
///     <c>NpcFunActivity.lua</c> (<c>NPC_FLAG_ACTIVITY = 7</c>): that script draws
///     <c>NF_L0_97</c>-<c>NF_L0_99</c>, which is the guild-quest page already used by
///     2.10 ("请选择发布给公会成员任务的种类"), not an item exchange. The 2.12
///     name-to-wording test fails, so the NPC keeps its current path.
///   - <c>Sparta_110</c>/<c>Athens_110</c> Pet Master is quest 303/1303's giver in
///     <c>StarterQuestChain</c>, so the quest gate in
///     <c>GameClientHandler.NpcDialogOpen.cs</c> owns it before any scripted dialogue
///     is consulted - the same exclusion as <c>_054</c>. Its
///     <c>NpcFunPetMaster.lua</c> keys <c>PL0001</c>-<c>PL0014</c> are also undefined
///     in <c>zh_cn</c>.
///   - <c>Sparta_090</c>/<c>Athens_090</c> Pan's Envoy, <c>NpcFunPan.lua</c>
///     (<c>NPC_FLAG_SYS_PAN = 10</c>): the script has no button and no
///     <c>SetPosition</c> at all - all four branches are <c>EndMessage</c> results,
///     so there is no menu to open, and any single reply closes the window. Choosing
///     which of them the original server sent is a wording read 3.5 forbids.
///   - (Superseded 2026-11-09: Easter Envoy 128 is now function 99 NpcFunRevive.lua and the Easter Bunny
///     Envoy 117 function 51; GameClientHandler.EasterAnniversary.cs answers both while Easter is open.)
///   - <c>Sparta_128</c>/<c>Athens_128</c> Easter Envoy, <c>NpcFunRelive.lua</c>
///     (<c>NPC_FLAG_SYS_RELIVE = 51</c): all twenty-two <c>NF_L0_RL_*</c> keys are
///     absent from <c>zh_cn/UI/Base/LuaText.lua</c> and exist only in the
///     <c>en_us</c> copy, and two branches concatenate a nil, which raises a Lua
///     error. <c>902</c>-<c>907</c> are also drawn under both Index 2 and Index 3.
///   - <c>Sparta_133</c>/<c>Athens_133</c> B-Gold Trader, <c>NpcFunMoney.lua</c>
///     (<c>NPC_FLAG_SYS_MONEY = 100</c>) and <c>Sparta_119</c>/<c>Athens_119</c> IGG
///     Anniversary Envoy, <c>NpcFunIgg.lua</c> (<c>NPC_FLAG_SYS_IGG = 104</c>): the
///     first has no button at all and defines none of its four <c>MONEYCHANGE*</c>
///     keys in <c>zh_cn</c>; the second has exactly one button whose two keys
///     <c>igg100t</c>/<c>igg100b</c> are likewise undefined in <c>zh_cn</c>, and its
///     confirmation travels by message box, which has no packet here (2.7).
///
/// <c>NPC_FLAG_SYS_FIRE = 89</c> (<c>NpcFunFire.lua</c>, 燃放新春爆竹) was checked as
/// a possible home for "Spring Festival Envoy" and rejected: it has no button branch
/// either, and its wording is the 年兽 and firecrackers, not the snow-demon drops the
/// NPC describes. It is bound to <c>NPC_FLAG_SYS_NewYear = 48</c> instead.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The Halloween envoy of both capitals, who hands out the costume, the wand and
    /// the pumpkin-candy exchange.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunHallo.lua</c> (function
    /// <c>NPC_FLAG_SYS_HALLO = 113</c>). The binding is the client's own: the NPC is
    /// "Halloween Envoy" and says it gives a Halloween suit, a Halloween wand and
    /// pumpkin candy, and page one is <c>Hallo1</c> 每人限领一件万圣节时装 over
    /// <c>Hallo2</c> 领时装, <c>Hallo3</c> 领魔棒 and <c>Hallo4</c> 用南瓜糖换奖励.
    ///
    /// The heading leads because it is the page's only text branch, and the three
    /// entries set their own positions at <c>25,135</c>/<c>155</c>/<c>175</c>, so one
    /// reply carries all three without overlap.
    ///
    /// No entry is answered. Page 2 offers seven result numbers for these clicks
    /// (<c>100</c>/<c>101</c> costume, <c>102</c>/<c>104</c> wand, <c>108</c> full
    /// bag, <c>201</c> daily exchange cap, plus the <c>5</c>-<c>8</c> candy prices)
    /// and every one of them asserts something about what this player has already
    /// claimed, which is not recorded here; choosing among them is a text-semantic
    /// read that 3.5 forbids. Two further branches cannot be sent at all: <c>103</c>
    /// redraws <c>Hallo101</c>, so its wording contradicts its own meaning, and the
    /// <c>SubID % 1000 == 1</c> remaining-exchanges line prints <c>Hallo202</c> and
    /// <c>Hallo203</c>, which are undefined in the shipped client.
    ///
    /// Superseded 2026-11-09 (W3 seasons halloween): the 1.4.4 en_us LuaText defines Hallo202/203 ("a maximum of 8
    /// times every day"), and while Halloween is open GameClientHandler.HalloweenMayDay.cs answers every entry from
    /// the server's own records (suit per season year, wand per day, 8 candy exchanges per day). This dialogue only
    /// answers while the season is closed, when the envoy is not on the map.
    /// </remarks>
    private static readonly ScriptedNpcDialogue HalloweenEnvoyDialogue = new(
        FunctionNumber: 113,
        OpeningMenu: [1, 2, 3, 4],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The festival envoy of both capitals, who explains the current festival and
    /// hands out its gift.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunGift.lua</c> (function
    /// <c>NPC_FLAG_SYS_GIFT = 28</c>, 节日礼物). The client names this NPC "Festival
    /// Envoy", and page one carries <c>GIFT1</c> 2011年元旦补领礼物 with
    /// <c>GIFT2</c> 领取本次礼物, <c>GIFT3</c> 本次节日介绍, <c>GIFT4</c> 下次节日时间
    /// and <c>GIFT5</c> 周末经验加成.
    ///
    /// The script gives page one no <c>SetPosition</c> at all, so the entries fall
    /// onto the window's own slot ladder (2.11) and the send order is the visual
    /// order: <c>2</c> first because it is the only branch that also writes the
    /// heading, then <c>3</c>, <c>4</c>, <c>5</c>.
    ///
    /// <c>1</c> is deliberately left off the menu: its label is <c>NF_L0_GIFTX</c>,
    /// which is not defined anywhere in the shipped client, so drawing it yields a
    /// blank button.
    ///
    /// Nothing is answered. Every page-2 reply is bound to a calendar day or to a
    /// claim this server does not record - <c>200</c>-<c>214</c> select a festival by
    /// date, <c>300</c>-<c>314</c> the next festival, and the weekend bonus needs
    /// <c>501</c>/<c>511</c>-<c>513</c> against a real claim. The
    /// <c>SubID % 1000 == 1</c>/<c>== 2</c> experience and talent lines and the
    /// <c>== 3</c>/<c>== 4</c> blueprint and scroll families are computed numbers whose
    /// index arithmetic the script itself gets wrong, and three of their keys
    /// (<c>GIFT505</c>, <c>YD109</c>, <c>SP109</c>) are undefined. None may be sent.
    /// </remarks>
    private static readonly ScriptedNpcDialogue FestivalEnvoyDialogue = new(
        FunctionNumber: 28,
        OpeningMenu: [2, 3, 4, 5],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The mount feeder of both capitals, who raises, lowers and re-skins a mount
    /// and forges its gear.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunHorseFeeder.lua</c> (function
    /// <c>NPC_FLAG_SYS_HorseFeeder = 21</c>, 骑宠饲养员), matching the client name
    /// "Mount Feeder". Page one is <c>NF_HF_T1</c> over six buttons at
    /// <c>25,200/220/240</c> and <c>300,200/220/240</c>: 升级坐骑, 升级相关说明,
    /// 坐骑降级, 外观更换, 兑换坐骑令牌 and 装备打造.
    ///
    /// This is the one dialogue here whose next step the script encodes itself: its
    /// message callback names the previous entry, with <c>PreSubID == 102</c>
    /// selecting <c>NF_HF_TH201</c> and <c>PreSubID == 103</c> selecting
    /// <c>NF_HF_TH203</c>. Those two are therefore settled by the client rather than
    /// inferred. The other four are matched to their form or information page by
    /// wording alone (升级坐骑 to <c>200</c> 请放入你想升级的坐骑 and so on), which is
    /// inference the script does not encode.
    ///
    /// <c>106</c> is left off the menu: its label <c>NF_HF_B106</c> and its form
    /// heading <c>NF_HF_T206</c> are undefined in the shipped client, so both draw
    /// empty. <c>988</c>/<c>989</c> are undefined too, and every Index-3 result
    /// (<c>301</c>-<c>317</c>, <c>990</c>-<c>999</c>) reports a completed upgrade,
    /// downgrade, re-skin or exchange, which this server does not perform. The six
    /// page-two forms take an item and a submission whose <c>+20</c> value has not
    /// been observed, so they stay unanswered like 2.14 and 2.16.
    ///
    /// The September 28 2026 capture (session c202c633, npc 5113 = Sparta_116 at
    /// local 01:58:55) settles the opening page instead of leaving it inferred:
    /// the reference advertised <c>[100, 101, 102, 103, 104, 105, 106, 107]</c>,
    /// which is that list verbatim. <c>106</c> is therefore sent after all - the
    /// client's own script does define the 绑定坐骑装备 button for it - and
    /// <c>107</c> rides along with no branch anywhere in the script and with
    /// <c>NF_HF_B107</c> undefined, so it draws nothing. Neither one was clicked
    /// in the capture, so neither owns an answer and both are left unanswered.
    /// </remarks>
    private static readonly ScriptedNpcDialogue MountFeederDialogue = new(
        FunctionNumber: 21,
        OpeningMenu: [100, 101, 102, 103, 104, 105, 106, 107],
        Steps: new Dictionary<int, int[]>
        {
            // Script-encoded through the message callback's own PreSubID test.
            [102] = [201],
            [103] = [203],

            // Matched by wording only; the script links no Index to another.
            [100] = [200],
            [101] = [202],
            [104] = [204],
            [105] = [205]
        });

    /// <summary>
    /// The random quest manager, who hands out the quest bag and eats the
    /// experience pill it rewards.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunQuest.lua</c> (function
    /// <c>NPC_FLAG_SYS_QUEST = 52</c>, 随机任务), matching the client name "Random
    /// Quest Manager". Page one is <c>NF_L0_QT0101</c> 70级以上玩家才可以领取任务袋
    /// over <c>101</c> and the entry <c>NF_L0_QT102</c> 吃经验之丹 at 25,120.
    ///
    /// <c>101</c> is not offered. Its button label is <c>NF_L0_QT101</c>, while the
    /// shipped client defines only the zero-padded <c>NF_L0_QT0101</c>, so the entry
    /// draws as an empty button - the <c>NF_L0_GIFTX</c> case again, and a client-side
    /// defect rather than a choice here. That also means the heading never appears,
    /// because the heading rides on the same branch.
    ///
    /// <c>102</c> answers with its quantity form <c>202</c> 输入你要吃的经验之丹数量
    /// (1~99), which is a wording match. The form's submission is not observed, so
    /// the three Index-3 lines behind it (<c>310</c> invalid number, <c>320</c> eaten,
    /// <c>330</c> not enough pills) cannot be reached, and the four page-two claim
    /// results (<c>220</c> level, <c>230</c> already holding a bag, <c>240</c> full
    /// bag, <c>250</c> bag granted) all restate the same offer, so settling which one
    /// a click earns needs a capture. <c>210</c> is the script's own mis-click line.
    ///
    /// The September 28 2026 capture (session c202c633, npc 5115 = Sparta_118 at
    /// local 01:58:44) settles both: the reference advertised <c>[101, 102]</c>,
    /// so <c>101</c> 领取任务袋 is a real button after all (the earlier reading
    /// that it draws an empty label came from <c>NF_L0_QT101</c> versus the
    /// zero-padded <c>NF_L0_QT0101</c>, and the client defines the latter), and
    /// clicking it answered <c>250</c> - the script's own "you have the quest bag"
    /// line. Both numbers are on the opening page, so the reply is one page.
    /// The bag itself is not granted here: this server owns no quest-bag economy,
    /// and the capture only shows the line the reference drew.
    /// </remarks>
    private static readonly ScriptedNpcDialogue RandomQuestManagerDialogue = new(
        FunctionNumber: 52,
        OpeningMenu: [101, 102],
        Steps: new Dictionary<int, int[]>
        {
            [101] = [250],
            [102] = [202]
        });

    /// <summary>
    /// The spring festival envoy of both capitals, who takes the goods the snow
    /// demons drop and exchanges them for rewards.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunNewYear.lua</c>, function
    /// <c>NPC_FLAG_SYS_NewYear = 48</c> - <see langword="not"/>
    /// <c>NPC_FLAG_SYS_FIRE = 89</c>. Both constants carry the comment 春节 in
    /// <c>NpcFun.lua</c>, which is what makes them easy to confuse. The client
    /// description settles it: this NPC says the Snow Demons "will drop a lot of cool
    /// stuff when killed that you can then exchange with me for rewards", and
    /// <c>NF_NY_T10</c> reads 在近郊出现的雪怪被杀死后会掉落腊肉、年糕…用这些东西都可以在我这兑换奖励.
    /// <c>NpcFunFire.lua</c> is instead about 燃放新春爆竹 and the 年兽, and mentions no
    /// snow demon, so it belongs to a different endpoint.
    ///
    /// Page one is <c>NF_NY_T10</c> over six entries whose positions the script
    /// computes as 25, 135 + (id - 100) * 20, so they cannot overlap and the send
    /// order is the visual order.
    ///
    /// Nothing is answered. Page two holds six groups of two (<c>200</c>-<c>202</c>,
    /// <c>203</c>-<c>204</c>, <c>205</c>-<c>206</c>, <c>207</c>-<c>208</c>,
    /// <c>209</c>-<c>210</c>, <c>211</c>-<c>212</c>) each under its own heading
    /// <c>NF_NY_T20</c>-<c>T26</c>, and the script never says which group belongs to
    /// which of the six goods - unlike the mount feeder above, this script encodes no
    /// <c>PreSubID</c>. Every later number also reports a completed exchange of a
    /// drop this server does not track, so one capture settles both the pairing and
    /// the reachable results.
    /// </remarks>
    private static readonly ScriptedNpcDialogue SpringFestivalEnvoyDialogue = new(
        FunctionNumber: 48,
        OpeningMenu: [100, 101, 102, 103, 104, 105],
        Steps: new Dictionary<int, int[]>());
}
