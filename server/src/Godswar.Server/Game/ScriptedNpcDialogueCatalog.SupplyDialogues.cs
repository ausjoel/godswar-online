namespace Godswar.Server.Game;

/// <summary>
/// Supply and seasonal NPC dialogues: transcribed from the client's own
/// <c>NpcFun</c> scripts under the rules of
/// <c>docs/NPC对话链路技术文本.md</c> - numbers only from a script's own
/// <c>SubID</c> branch, one group per step, nothing sent for a branch whose label
/// the shipped client cannot draw, and no answer where wording alone is the basis.
/// </summary>
/// <remarks>
/// Each <c>OpeningMenu</c> here lists only numbers whose label resolves to Chinese in
/// the client text table. Where a script's page-one header, or a later page, has no
/// text in either locale, that number is left out rather than drawn blank, and the
/// dialogue stops there until a capture settles the next step.
///
/// Four NPCs were examined and are deliberately not registered, each for a reason that
/// can be re-checked:
///
///   - <c>_131</c> Holy Stone Redeemer: <c>NpcFunHolyPackage.lua</c> draws no button at
///     all, and every one of its <c>HolyPackage1</c>-<c>HolyPackage10</c> keys, plus the
///     <c>NpcFunChains</c> keys that <c>NPC_FLAG_SYS_PINGZHEN = 92</c> reaches, is absent
///     from the client text tables. A result-only script has no menu to open.
///   - <c>_124</c> Event Awarder: <c>NPC_FLAG_ACTIVITY = 7</c> reaches the guild-quest
///     page (<c>NF_L0_97</c>-<c>NF_L0_99</c>) and <c>NPC_FLAG_SYS_DETAIL = 93</c> reaches
///     <c>NpcFunChains</c>, whose text is absent; neither wording matches "claim rewards
///     after the event has closed", so 2.12's binding test fails.
///   - <c>Sparta_025</c> Oriental Cuisine Master: no spawn record on any map, and the
///     Athens key of the same number is <c>[Warehouse]Kyriakou</c>, a different NPC. City
///     numbering cannot be assumed symmetric, so this is checked per <c>scene_key</c>.
///     (Superseded 2026-11-09, W3 seasons: Sparta_025 is published since NpcContentBaselineV9, its Athens twin is
///     Athens_031 (same template Sicily3, captured 5171); both now answer functions 102 NpcFunMooncake.lua and 101
///     NpcFunFood.lua while Oriental Cuisine is open: GameClientHandler.OrientalCarnival.cs.)
///   - <c>_090</c> Pan's Envoy and <c>_133</c> B-Gold Trader: their scripts
///     (<c>NpcFunPan.lua</c>, <c>NpcFunMoney.lua</c>) contain no button branch at all -
///     every branch is an <c>EndMessage</c> result - so there is nothing to advertise.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The war suppliers, who buy back war materials.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunMaterialBack.lua</c>, <c>NPC_FLAG_SYS_MATERIALBACK = 114</c>. The client
    /// has "War Supplier 1" say "You can hand in any war materials to me", and the
    /// script's heading 我们的兑换计划有：宙斯神谕兑换… names the same commodity.
    /// <c>1001</c> is the page heading and <c>1000</c> the one entry at 65,200, so both
    /// go in the opening reply; page two's five prices sit at 65,170-290 and are offered
    /// only after that entry is clicked, which is a wording match, not script encoding.
    /// No cross-<c>Index</c> reuse.
    /// </remarks>
    private static readonly ScriptedNpcDialogue WarSupplierDialogue = new(
        FunctionNumber: 114,
        OpeningMenu: [1001, 1000],
        Steps: new Dictionary<int, int[]>
        {
            [1000] = [1010, 1011, 1012, 1013, 1014]
        });

    /// <summary>
    /// The lunar priest, who takes Delicate Mooncakes (3920) for character experience, talent points,
    /// pet experience and zodiac energy.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunMoon0.lua</c>, <c>NPC_FLAG_SYS_MOON0</c>. <c>NpcFun.lua</c> assigns that
    /// constant twice, 76 at line 74 and 103 at line 104, and Lua keeps the last value,
    /// so the number to advertise is <c>103</c> - sending 76 reaches no dispatch branch
    /// and draws nothing. The same file reassigns <c>NPC_FLAG_SYS_MOONCAKE</c> from 75 to
    /// 102, which is what the 025 exchange would use if that NPC spawned.
    ///
    /// The binding is verbatim: the script's heading lists 人物经验，宠物经验，人物专长，
    /// 星座能量 and the client's own description of this NPC reads "Character Exp, Pet Exp,
    /// Character TP, Energy Zodiac". Four entries at 40,170-230 are self-positioned, so
    /// they fit one reply. The Athens priest is <c>Athens_032</c> (<c>Athens_026</c> is
    /// <c>[Warrior]Demetrius</c>, a different NPC).
    ///
    /// This table answers nothing past page one: page two's <c>101</c>-<c>103</c> are shared by
    /// all four reward pages, which a table keyed by the clicked number cannot hold. W3 seasons
    /// mid-autumn: while the season is open, GameClientHandler.MidAutumn.cs answers pages two and
    /// three from the click path (amounts GENERATED).
    /// </remarks>
    private static readonly ScriptedNpcDialogue LunarPriestDialogue = new(
        FunctionNumber: 103,
        OpeningMenu: [1, 2, 3, 4],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The Easter envoys, who hand out the bunny cage and take eggs.
    /// <para>
    /// Superseded 2026-11-09 (W3 seasons easter): the remarks below describe the old binding of Athens_128 /
    /// Sparta_128 to function 51. The client texts tie 51 (NF_L0_RL_901) to the Easter Bunny Envoy 117 and 99
    /// (REVIVE100T) to this Easter Envoy, so this dialogue is now function 99; see the comment above the field.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>NpcFunRelive.lua</c>, <c>NPC_FLAG_SYS_RELIVE = 51</c> - not
    /// <c>NPC_FLAG_SYS_REVIVE = 99</c>, which reaches a different script, and not
    /// <c>NPC_FLAG_SYS_RABBIT = 95</c>. The three entries 领取兔子笼, 复活节彩蛋 and
    /// 查看我的上交进度 at 25,160/180/200 are the only page-one numbers; the rule page
    /// <c>901</c> has no text in the client and is left out.
    ///
    /// Of page two only <c>201</c> 兑换奖品 and <c>202</c> 领取兔子 have text, and the
    /// script links no <c>Index</c> to another, so both pairings are wording matches and
    /// unmeasured. <c>201</c> is a form whose submission number has not been observed.
    /// Page three repeats <c>902</c>-<c>907</c> with its own meanings, which forces the
    /// chain to stop at page two regardless, and the two
    /// <c>SubID % 1000 == 1</c>/<c>== 2</c> families encode a real submission count that
    /// this server keeps no record of.
    /// </remarks>
    // W3 seasons easter: rebound to function 99 (NpcFunRevive.lua, NPC_FLAG_SYS_REVIVE "主城复活节活动"): its
    // REVIVE100T is this NPC's own description and REVIVE1001-1007 its egg -> Exp Pill exchange. The 51 script
    // above (NF_L0_RL_901 = the Easter Bunny Envoy's description) now belongs to Athens_117 / Sparta_117
    // (EasterBunnyEnvoyDialogue). Pages: 101 -> 201 (Isles hours), 102 -> 202 (egg slot), 103 -> 203 (token slot);
    // GameClientHandler.EasterAnniversary.cs answers them while Easter is open.
    private static readonly ScriptedNpcDialogue EasterEnvoyDialogue = new(
        FunctionNumber: 99,
        OpeningMenu: [101, 102, 103],
        Steps: new Dictionary<int, int[]>
        {
            [101] = [201],
            [102] = [202],
            [103] = [203]
        });

    /// <summary>
    /// The IGG anniversary envoy, who sells the anniversary gold sack.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunIgg.lua</c>, <c>NPC_FLAG_SYS_IGG = 104</c> - not the number 119, which is
    /// <c>NPC_FLAG_SYS_PETMASTER</c>. Only one number in the whole script draws a button,
    /// <c>100</c>, and it moves to 45,150 off the window's own ladder while hiding the
    /// two standard buttons, so the menu is a single entry.
    ///
    /// Superseded (rig 2026-11-09): the envoy is always answered by
    /// <c>GameClientHandler.AnniversaryEnvoyAsync</c> and never reaches this entry, because
    /// replying <c>[100]</c> here drew "You've obtained an Anniversary Gold Sack" with nothing
    /// charged or given. <c>NpcFunIgg_SetMsg</c> (<c>igg101t</c>, <c>HaveMessageBox</c>) is dead
    /// code: the stock <c>Set_MessageBox</c> in <c>NpcFun/NpcFun.lua</c> has no IGG branch, so
    /// each click (sub -1) is the purchase and <c>100</c>/<c>197</c>/<c>198</c>/<c>199</c> are its
    /// results (<c>199</c> borrows <c>NF_L0_MOON200</c>, a key belonging to the mooncake family).
    /// </remarks>
    private static readonly ScriptedNpcDialogue IggAnniversaryEnvoyDialogue = new(
        FunctionNumber: 104,
        OpeningMenu: [100],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The faction changers, who move a character to the other side for gold.
    /// </summary>
    /// <remarks>
    /// <c>NpcMigration.lua</c>, <c>NPC_FLAG_SYS_MIGRATION = 112</c>. The file exists
    /// twice under different names but identical bytes; <c>NpcFun.lua</c> dispatches to
    /// the <c>NpcMigration_</c> functions, so <c>NpcFunMigration.lua</c> is a dead copy.
    /// This is the strongest binding of the set: the NPC asks "Are you certain you want to
    /// switch factions?" and the script's own heading says the change is expensive and
    /// drops the connection immediately.
    ///
    /// One entry only. The script offers <c>105</c> and <c>106</c> at the same 25,135 with
    /// byte-identical labels that differ only in colour (red and blue, i.e. the two
    /// factions), so per 3.2 exactly one may be sent and which one the original server
    /// used is unmeasured; the heading <c>104</c> has no text. Page two's three reasons
    /// and the <c>SubID % 100 == 2</c> cooldown countdown have no text either, so nothing
    /// is answered - and the change itself, which drops the connection, is not something
    /// this server performs.
    /// </remarks>
    // 2026-11-03: the remarks above describe the old stub. The confirm button is now answered by
    // TryHandleIdentityChangeAsync (ScriptedNpcDialogueCatalog.IdentityChange.cs), which sends its own page one.
    private static readonly ScriptedNpcDialogue FactionChangerDialogue = new(
        FunctionNumber: 112,
        OpeningMenu: [105],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The rich man's envoy, who runs the anniversary lottery.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunLotto.lua</c>, <c>NPC_FLAG_SYS_LOTTO = 33</c>. The NPC invites the player
    /// to try their luck and get rich, and the three entries are 我要抽一次, 领取奖品 and
    /// 活动详情. The page-one heading <c>101</c> has no text in the client and is left
    /// out; so is everything from page two on, where <c>Lotto1</c> and
    /// <c>Lotto5</c>-<c>Lotto13</c> are all undefined, and <c>206</c> is reused across two
    /// pages.
    /// </remarks>
    private static readonly ScriptedNpcDialogue RichManEnvoyDialogue = new(
        FunctionNumber: 33,
        OpeningMenu: [102, 103, 104],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The sacred sealer, who sells the legendary creature scrolls.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunLegendary.lua</c>, advertised as <c>NPC_FLAG_SYS_LEGENDARY1 = 34</c>.
    /// <c>NpcFun.lua</c> dispatches both <c>LEGENDARY1</c> and <c>LEGENDARY2 = 35</c> to
    /// this one script, so the two numbers draw the same page and which one the original
    /// server chose is unmeasured - only one is advertised here. The three entries are
    /// 购买拉冬卷轴, 购买独眼巨人卷轴 and 购买美杜莎卷轴 at 25,135/155/175, matching the
    /// client's own "scrolls that have legendary creatures sealed inside … some Gold".
    ///
    /// The heading <c>101</c> (<c>Legendary3</c>) has no text and is left off, and so is
    /// every result: <c>Legendary1</c>, <c>Legendary2</c> and <c>Legendary7</c>-
    /// <c>Legendary11</c> are undefined in the client, which is all page two offers, and
    /// the scrolls are not sold here.
    /// </remarks>
    private static readonly ScriptedNpcDialogue SacredSealerDialogue = new(
        FunctionNumber: 34,
        OpeningMenu: [102, 103, 104],
        Steps: new Dictionary<int, int[]>());
}
