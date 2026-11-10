namespace Godswar.Server.Game;

/// <summary>
/// Scripted capital-city dialogues that no earlier record of
/// <c>docs/NPC对话链路技术文本.md</c> section 2 covers. Each is one client
/// <c>NpcFun</c> script owned by one NPC pair, transcribed under the same rules:
/// every number comes from the script's own <c>SubID</c> branch, one reply carries
/// only the group for the current step, and a step whose number cannot be settled
/// from the script is left unanswered instead of guessed.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The wedding priest of both capitals, Cupid's servant, who makes the
    /// congratulatory red packets and sets off the celebration fireworks.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunCongrate.lua</c> (function
    /// <c>NPC_FLAG_SYS_CONGRATE = 42</c>, 婚礼祭司). The binding is the shipped
    /// content's own: both cities name this NPC "Wedding Priest" and have it say
    /// "Cupid's right hand man … bring some red packets and fireworks", and the two
    /// page-one entries are <c>NF_L0_CONG1</c> 制作红包 and <c>NF_L0_CONG2</c>
    /// 燃放庆祝烟花.
    ///
    /// Each answer advances the client one <c>Index</c>, so depth is the page:
    ///
    ///   page 1 (Index 1)  1/2            25,135 / 25,155
    ///   page 2 (Index 2)  11/12          银币 and 金币 packets, 25,135 / 25,155,
    ///                                    under <c>NF_L0_CONG1200</c> 我这里能做红包
    ///                     21/22/23/24    the four fireworks, 25,135/155/185/215,
    ///                                    under <c>NF_L0_CONG1300</c> 我还会帮你放烟火
    ///   page 3 (Index 3)  31/41          the 银币 and 金币 packet forms, each an
    ///                                    item slot plus an amount field
    ///                     2700           当前结婚新人已经不存在
    ///
    /// The two page-2 groups share the 25,135 and 25,155 slots, so they are mutually
    /// exclusive and never sent together. Which group follows which page-one entry is
    /// taken from the page's own heading text, which names the page-one wording -
    /// 红包 in <c>NF_L0_CONG1200</c>, 烟火 in <c>NF_L0_CONG1300</c>. The script does
    /// not encode that pairing, so it is an inference and has not been re-checked
    /// against a capture.
    ///
    /// Deliberately unanswered: the <c>31</c>/<c>41</c> form submissions, because what
    /// the client sends in <c>+20</c> on submit has not been observed, which is the
    /// same open item as the Zeus deposit form in section 4; and the firework entries
    /// stop at the script's own no-couple line, because a wedding in progress is not
    /// something this server records. Never sent at all: <c>2200</c> and <c>2500</c>,
    /// which the script draws under both Index 3 and Index 4 while a table keyed by
    /// the clicked number alone can hold only one meaning for each, and <c>999</c>,
    /// which prints the undeclared global <c>Level_limit</c>.
    /// </remarks>
    private static readonly ScriptedNpcDialogue WeddingPriestDialogue = new(
        FunctionNumber: 42,
        OpeningMenu: [1, 2],
        Steps: new Dictionary<int, int[]>
        {
            [1] = [11, 12],
            [2] = [21, 22, 23, 24],
            [11] = [31],
            [12] = [41],
            [21] = [2700],
            [22] = [2700],
            [23] = [2700],
            [24] = [2700]
        });

    /// <summary>
    /// Cupid, who arranges engagements, ceremony times, the ceremony itself and the
    /// wedding gift.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunMaterial.lua</c> (function
    /// <c>NPC_FLAG_SYS_MATERIAL = 41</c>, 丘比特(结婚)). The client names this NPC
    /// "Cupid" and has it say "If you wish to marry your sweetheart then you can get
    /// engaged to them here", and page one is <c>NF_L0_MRI6</c> 爱情天使就是我…找潘神
    /// 拿条海螺项链 over <c>NF_L0_MRI1</c> 申请订婚, <c>NF_L0_MRI2</c> 申请结婚仪式时间,
    /// <c>NF_L0_MRI3</c> 开始结婚仪式, <c>NF_L0_MRI4</c> 领取结婚礼包 and
    /// <c>NF_L0_MRI5</c> 查询结婚仪式时间.
    ///
    /// The introduction leads, as in 2.5 and 2.10, because it is the page's only text
    /// branch, and the five entries each set their own position from 25,135 to 25,215,
    /// so one reply carries all of them without overlap.
    ///
    /// Nothing is answered, which is a spec decision rather than unfinished routing.
    /// The dialogue is an engagement, a schedule and a two-player confirmation, none of
    /// which this server records, and page 2 offers over twenty result numbers for
    /// these five clicks - among them four 领取结婚礼包 lines and four 申请时间 lines
    /// whose wording is the only thing telling them apart. Picking one by reading its
    /// text is exactly what 3.5 forbids and what 5 lists as this repository's own past
    /// failure, so every click stays unanswered until a capture settles it.
    /// </remarks>
    private static readonly ScriptedNpcDialogue CupidDialogue = new(
        FunctionNumber: 41,
        // 7 = "Buy a Whelk Necklace" (F-130, client 1.4.5 NpcFunMaterial.lua); older clients show nothing for it.
        OpeningMenu: [6, 1, 2, 3, 4, 5, 7],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The class shifter of both capitals, who moves a character between the four
    /// professions and converts their weapon to match.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunWork.lua</c> (function <c>NPC_FLAG_SYS_WORK = 44</c>,
    /// 转职). The client names this NPC "Class Shifter" and states the rule the script
    /// carries: free at level 100 or below, one Class Shift Permit above it.
    ///
    /// Unlike the altar script, this one never reuses a number across pages -
    /// Index 1 is 100-103, Index 2 is 110-114/116-121/123/124/128/129 and Index 3 is
    /// 130/200-203/205-209/211/213/214 - so no step has to stop early for want of a
    /// second meaning:
    ///
    ///   page 1 (Index 1)  100/101/102/103   25,155/175/195/215
    ///   page 2 (Index 2)  110/111/112/113   转职为战士/斗士/法师/祭司,
    ///                                       320,135/155/175/195
    ///                     116/117/118/119   the four 转换为X武器 buttons, at
    ///                                       25,215 / 25,235 / 320,215 / 320,235
    ///                     128                四职业特色, a result
    ///
    /// The two button groups sit in columns that do not touch each other, so each
    /// could be sent whole; only the one matching its own page-one entry is. That
    /// pairing is read from the button texts (申请转职 over the four professions,
    /// 转换职业武器 over the four weapons), not from the script, which cross-references
    /// nothing - it is an inference and has not been re-checked against a capture.
    ///
    /// Deliberately unanswered: <c>102</c> 转换职业防具, because although
    /// <c>NF_L0_WK112</c> 我要转换该件防具 is defined in <c>LuaText.lua</c>, no branch
    /// of the script ever draws it, so the client has no armour-conversion button to
    /// click; the <c>114</c>/<c>120</c>/<c>123</c> permit forms and their submissions,
    /// whose <c>+20</c> value is unmeasured; and every Index 3 result, because reaching
    /// one needs a real class or weapon conversion, which this server does not perform.
    /// </remarks>
    // CLASSCHANGE (2026-11-03): with GODSWAR_CLASS_CHANGE=true or server rate npc.class_change = 1,
    // the four class buttons are answered by TryHandleClassChangeAsync
    // (ScriptedNpcDialogueCatalog.ClassChange.cs), which performs the change. Off, this table is unchanged.
    private static readonly ScriptedNpcDialogue ClassShifterDialogue = new(
        FunctionNumber: 44,
        OpeningMenu: [100, 101, 102, 103],
        Steps: new Dictionary<int, int[]>
        {
            // Captured on 2026-09-28 (session c202c633, npc 5041 = Sparta_044 at
            // local 02:07:21): the reference answered the first entry with
            // [110, 111, 112, 113, 114] and the follow-up submission 112 - the
            // client keeps 100 in +16 and puts the chosen class in +20 - with
            // 202, the script's own "take your equipment off first" line. The
            // extra 114 (the removal instructions) and the 112 arm are the two
            // numbers this capture added.
            [100] = [110, 111, 112, 113, 114],
            [112] = [202],
            [101] = [116, 117, 118, 119],
            [103] = [128]
        });

    /// <summary>
    /// The fortune teller of both capitals, who changes the character's zodiac and
    /// claims the lucky day.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunStar.lua</c> (function
    /// <c>NPC_FLAG_SYS_STAR = 19</c>, 星座幸运日). The client names this NPC "Fortune
    /// Teller" and has it say it can "change your zodiac and grant you extraordinary
    /// luck for one day", and page one is <c>NF_L0_XZ2</c> 免费修改星座,
    /// <c>NF_L0_XZ3</c> 缴纳500金币修改星座, <c>NF_L0_XZ4</c> 免费领取幸运日 and
    /// <c>NF_L0_XZ5</c> 缴纳500金币领取幸运日, at 25,135/160/185/210. Below 1000 the
    /// remaining page-one numbers only print the heading, and 1000 and above are the
    /// 请正确操作哦 result, so the four buttons are the whole opening.
    ///
    /// Nothing is answered, because the script cannot settle the second page:
    ///
    ///   - The twelve signs are drawn twice, as <c>1</c>-<c>12</c> and again as
    ///     <c>13</c>-<c>24</c>, at the same positions with byte-identical text, so
    ///     which band is the free path and which the 500-gold path is not in the
    ///     script at all.
    ///   - <c>1</c> is a page-one button, a page-two sign and a page-three result
    ///     (修改星座成功) at once, so a table keyed by the clicked number alone cannot
    ///     carry it; the same holds for <c>2</c>, <c>3</c> and <c>4</c>.
    ///   - <c>25</c> matches no branch, falling between the script's own
    ///     <c>&lt;= 24</c> and <c>&gt;= 26</c> tests, and <c>26</c> and <c>39</c> share
    ///     25,180, so they are mutually exclusive.
    ///
    /// Page one is still advertised because the Zodiac state itself is authoritative
    /// here (opcode <c>10297</c>, see <c>docs/zodiac-sync-10297.md</c>), and changing
    /// the sign is listed there as a remaining SID rather than as a working path, so no
    /// second step exists to send.
    /// </remarks>
    private static readonly ScriptedNpcDialogue FortuneTellerDialogue = new(
        FunctionNumber: 19,
        OpeningMenu: [1, 2, 3, 4],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// Cupid's shadow, who dissolves an engagement or a marriage.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunDivrce.lua</c> (function
    /// <c>NPC_FLAG_SYS_DIVORCE = 43</c>, 丘比特阴影(解除婚姻)). The client names this
    /// NPC "Cupid's Shadow", matching the flag's own label, and page one is
    /// <c>AWAY1000</c> 丘比特的阴影 over <c>AWAY1</c> 解除订婚 and <c>AWAY2</c> 解除结婚,
    /// at 25,135 / 25,155.
    ///
    /// Page two is two mutually exclusive pairs - <c>11</c>/<c>12</c> (协议 / 强制
    /// 解除订婚) and <c>21</c>/<c>22</c> (协议 / 强制解除结婚) - that reuse the same
    /// 25,135 and 25,155 slots, so per 3.2 only one pair goes out per reply. Which
    /// pair follows which page-one entry is read from the wording, not from the
    /// script, which links no <c>Index</c> to any other.
    ///
    /// Nothing is answered past that. The remaining page-two and page-three numbers
    /// (<c>1100</c> 你并没有订婚, <c>1200</c> 你并没有结婚, <c>1300</c> 结婚未超过 7 天,
    /// and the six Index-3 lines up to 解除成功 and 强制解除需扣 200 声望或 100000 银币)
    /// each assert a marital state, a party composition, a seven-day window or a
    /// reputation charge that this server does not record, and selecting among them is
    /// a text read that 3.5 forbids. The script reuses no number across pages, so
    /// this dialogue stops by evidence rather than by the collision rule.
    /// </remarks>
    private static readonly ScriptedNpcDialogue CupidsShadowDialogue = new(
        FunctionNumber: 43,
        OpeningMenu: [1, 2],
        Steps: new Dictionary<int, int[]>
        {
            [1] = [11, 12],
            [2] = [21, 22]
        });

    /// <summary>
    /// The apothecary, who exchanges gems for experience potions and combines
    /// enduring potions.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunChemist.lua</c> (function
    /// <c>NPC_FLAG_SYS_CHEMIST = 60</c>, 药剂师), which names this NPC "Apothecary"
    /// outright. Page one is <c>CH100</c> 我是药剂师… over <c>CH101</c> 兑换经验药剂
    /// and <c>CH102</c> 合成持久型药剂, at 25,135 / 25,155.
    ///
    /// <c>102</c> answers with <c>202</c>, the combine form, which is a fixed number.
    /// <c>101</c> is left unanswered for a structural reason rather than a missing
    /// decision: its exchange form is not a number at all but a computed one, the
    /// script's <c>SubID % 100 == 11</c> family, whose leading digits encode this
    /// character's remaining exchange counts against denominators the script hardcodes
    /// as 4, 4 and 8. A static click-to-page table cannot carry that, and inventing a
    /// value would print numbers the player has not earned.
    ///
    /// Also not sent: every Index-3 result (<c>300</c>, <c>902</c>-<c>910</c> and the
    /// <c>% 100 == 21</c> success form), because each one reports a completed
    /// exchange, a shortage of crystals, silver or potions, a daily cap or a full bag -
    /// all of which need the exchange itself, which this server does not run. Note
    /// <c>901</c> is never drawn and never defined, and both Index-3 branches hide
    /// <c>ButtonA1</c>/<c>A2</c> and use <c>A3</c> at 420,250.
    /// </remarks>
    private static readonly ScriptedNpcDialogue ApothecaryDialogue = new(
        FunctionNumber: 60,
        OpeningMenu: [101, 102],
        Steps: new Dictionary<int, int[]>
        {
            [102] = [202]
        });

    /// <summary>
    /// The plastic surgeon, who changes a character's sex for a large gold fee.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcDenaturation.lua</c> (function
    /// <c>NPC_FLAG_SYS_DENATURATION = 105</c>, 变性). The client names this NPC
    /// "Plastic Surgeon" and the script's own heading says 你真的要改变性别啊！30000金
    /// 啊！…你将掉线. Page one is that heading <c>Denaturation4</c> plus the single
    /// button <c>Denaturation5</c> 我要改变性别（30000金） at 25,135.
    ///
    /// The file is named without the <c>Fun</c> prefix and still dispatches from
    /// <c>NpcFun.lua</c>, which is why the search for it is easy to miss.
    ///
    /// The button is not answered, and no answer could be right: page two holds
    /// exactly three lines - <c>101</c> 金币不足, <c>102</c> 未离婚或处于订婚状态 and
    /// <c>103</c> 未脱下时装 - none of which the script links to page one, so choosing
    /// is 3.5's forbidden read. Two further facts argue against sending any of them
    /// even once measured: page two carries no <c>EndMessage</c> and no button, so a
    /// reply would leave a bare error line in an open window with nothing to click,
    /// and the script has no success number at all - the change is completed by the
    /// 掉线 the heading describes.
    /// </remarks>
    // 2026-11-03: the remarks above describe the old stub. The confirm button is now answered by
    // TryHandleIdentityChangeAsync (ScriptedNpcDialogueCatalog.IdentityChange.cs), which sends its own page one.
    private static readonly ScriptedNpcDialogue PlasticSurgeonDialogue = new(
        FunctionNumber: 105,
        OpeningMenu: [104, 105],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The mystery merchant, who sells two limited daily goods.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunSecretShop.lua</c> (function
    /// <c>NPC_FLAG_SYS_SECRETSHOP = 110</c>, 神秘商人), which matches the client name
    /// "Mystery Merchant". Page one is the goods list <c>SecretShop_01</c> over
    /// <c>SecretShop_02</c> 重生泉水(限定版) and <c>SecretShop_03</c>
    /// 中级仙宠灵露(限定版).
    ///
    /// Each product is answered with its own purchase line, <c>201</c> and
    /// <c>202</c>, which repeat the product name verbatim. That is still a wording
    /// match rather than something the script encodes, and it is the strongest such
    /// match among the dialogues transcribed here.
    ///
    /// The three page-two failures are not sent: <c>901</c> full bag, <c>902</c>
    /// insufficient gold and <c>903</c> daily quota spent each apply to both products
    /// identically, so the clicked number alone cannot say which is true, and the
    /// goods themselves are not sold here. The 灵露 price escalation the heading
    /// describes, 200 金 rising 100 per purchase, exists only in prose - this script
    /// encodes no number for it, unlike 2.1 and 2.11.
    ///
    /// One coordinate detail: <c>101</c> sits at 25,115, one notch above the window's
    /// first button slot, to clear the five-line heading. Both entries set their own
    /// position, so 2.11's slot-ladder fallback never applies.
    /// </remarks>
    private static readonly ScriptedNpcDialogue MysteryMerchantDialogue = new(
        FunctionNumber: 110,
        OpeningMenu: [100, 101, 102],
        Steps: new Dictionary<int, int[]>
        {
            [101] = [201],
            [102] = [202]
        });
}
