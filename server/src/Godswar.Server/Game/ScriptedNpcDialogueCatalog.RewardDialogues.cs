namespace Godswar.Server.Game;

/// <summary>
/// The remaining capital and world-map function NPCs whose client script draws buttons
/// that the shipped client can actually label, transcribed under the same rules as the
/// other catalog files.
/// </summary>
/// <remarks>
/// Checked against the live realm: none of these keys carries a dialogue route, so the
/// scripted branch is reachable for all of them.
///
/// Six further NPCs were examined and are not registered, each for a reason that can be
/// re-checked in the client or the content tables:
///
///   - <c>_073</c> Battlefield Awarder and the holy-water half of <c>WarField_*</c>:
///     their actions live in <c>NpcFunWar.lua</c> under function number 3, and 3 is what
///     the server already advertises for the quest page (2.1 rule 4), so sending it would
///     collide with the quest pipeline rather than open the war menu.
///   - <c>_112</c> Lunar Beast Slayer: <c>NpcFunFire.lua</c> has no button branch at all -
///     every number is an <c>EndMessage</c> result, and the one informative line encodes a
///     realm-wide firecracker count in <c>SubID = 1000n + 1</c>.
///   - <c>_115</c> Mid-autumn Gift Pack Giver: no script or text key in the client names a
///     gift pack, and the moon family it would belong to is the 兑换月饼 set already used by
///     <c>_026</c>. The candidate script <c>NpcFunMoon.lua</c> is itself unreachable, since
///     its dispatch constant <c>NPC_FLAG_SYS_MOON</c> is never defined.
///   - <c>_089</c> Pet Merchant: it invites the player to browse goods, which is the shop
///     path, while function number 31's own heading addresses the pet manager, a different
///     NPC. The names do not match, so 2.12's test fails.
///   - <c>_064</c> King of Newbies, <c>_069</c> Teaching Manager, <c>_111</c> Spring
///     Merrymaker, the <c>Postman</c>, <c>Fisherman</c>, <c>Fellmonger</c>,
///     <c>Cronus Captain</c>, <c>Troy_004</c>/<c>_005</c> and <c>Arena_001</c> NPCs: no
///     <c>NpcFun</c> script owns them. Their text is a static board, a native client
///     window, or a plain description, and <c>_069</c> has no 拜师/收徒/师徒 wording
///     anywhere in the client.
///   - <c>Sparta_Newbie_024</c>-<c>_028</c> Fitness Trainer 1-5: five text templates but no
///     spawn record on any map, so they cannot be clicked.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The title expert's identification page, which names the class of title to check.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunDesidentify.lua</c>, <c>NPC_FLAG_SYS_DESIDENTIFY = 12</c>. The client
    /// description says the player obtains attribute and class titles here, and page one
    /// offers 成长类称谓鉴定 at 25,95 and 属性类称谓鉴定 at 25,115 under the
    /// <c>DES001</c> heading. Nothing is answered: page two reuses <c>1</c>-<c>8</c> for
    /// the individual title families, and a table keyed by the clicked number alone cannot
    /// hold two meanings for one number (2.11's resolution).
    /// </remarks>
    private static readonly ScriptedNpcDialogue TitleIdentificationDialogue = new(
        FunctionNumber: 12,
        OpeningMenu: [1, 2],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The title expert's reward page, which pays out titles and takes the exchange.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunDesaward.lua</c>, <c>NPC_FLAG_SYS_DESAWARD = 13</c>, the other half of the
    /// same NPC: 成长/任务/属性/排行/活动类奖励 and 称谓兑换. This script gives page one
    /// no <c>SetPosition</c> at all, so the six entries fall onto the window's own ladder
    /// and the send order is the visual order (2.11). Nothing is answered: page two's
    /// <c>1000</c>/<c>1200</c>/<c>2500</c> family is unlinked by the script, <c>500</c>
    /// reappears with a different meaning on page three, and <c>100</c> carries
    /// <c>EndMessage</c>, which may not share a reply with buttons (3.6).
    /// </remarks>
    private static readonly ScriptedNpcDialogue TitleRewardDialogue = new(
        FunctionNumber: 13,
        OpeningMenu: [1, 2, 3, 4, 5, 6],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The fitness trainers of the health run, who enter, query and leave the race.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunHealth.lua</c>, <c>NPC_FLAG_SYS_HEALTH = 22</c>. Four entries are drawn
    /// with their own positions at 25,135/155/175/195; <c>1002</c> is never drawn by the
    /// script, so it is not offered, and the <c>1201</c>/<c>1202</c>/<c>1218</c> group is a
    /// different, mutually exclusive first page that cannot be sent alongside it.
    /// Nothing is answered: page two reports race state, and the ranking line is a
    /// computed family (<c>SubID &gt; 1000000</c> with <c>SubID % 100</c> from 1 to 10)
    /// that encodes this server does not track.
    /// </remarks>
    private static readonly ScriptedNpcDialogue FitnessTrainerDialogue = new(
        FunctionNumber: 22,
        OpeningMenu: [1000, 1001, 1003, 1004],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The Trojan war coordinator, who signs the player up and reports the expedition.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunTroy.lua</c>, <c>NPC_FLAG_SYS_TROY_HORT = 67</c>. Four entries are offered
    /// at 25,135/155/175/195; <c>104</c> and <c>113</c> are excluded because they repeat
    /// the 25,135 and 25,155 slots of <c>101</c> and <c>102</c>, so they belong to a
    /// mutually exclusive first page. Nothing is answered: the later steps reach
    /// <c>LuaText["NF_L0_TR" .. SubID]</c> and other computed families, and
    /// <c>207</c>-<c>209</c>/<c>312</c> recur on page two.
    /// </remarks>
    private static readonly ScriptedNpcDialogue TrojanWarCoordinatorDialogue = new(
        FunctionNumber: 67,
        OpeningMenu: [101, 105, 106, 321],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The zeus envoys in the labyrinth, who take the war materials for oracle exchange.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunMaterialBack.lua</c>, <c>NPC_FLAG_SYS_MATERIALBACK = 114</c> - the same
    /// script and number as the war suppliers, reached from a different NPC on the labyrinth
    /// maps. The client has the envoy say the gods will "give you Zeus' Oracle", and the
    /// script's only page-one button is 宙斯神谕兑换 at 65,200. Its heading <c>1001</c> is
    /// text rather than a button, so only the entry is sent. The five oracle prices on page
    /// two are matched to that entry by wording alone, which the script does not encode.
    /// </remarks>
    private static readonly ScriptedNpcDialogue ZeusEnvoyDialogue = new(
        FunctionNumber: 114,
        OpeningMenu: [1000],
        Steps: new Dictionary<int, int[]>
        {
            [1000] = [1010, 1011, 1012, 1013, 1014]
        });

    /// <summary>
    /// The battlefield criers, who read out which battlefields are open.
    /// </summary>
    /// <remarks>
    /// <c>NpcFunWarTxt.lua</c>, <c>NPC_FLAG_SYS_WARTXT = 27</c>, distinct from
    /// <c>NPC_FLAG_SYS_WAR = 3</c> which the server already spends on the quest page. The
    /// client names these NPCs "Battlefield Crier" and the script's heading is 我是希腊的百
    /// 事通 over six battlefields - 品都斯山, 尼米尼, 利兰丁, 跨服, 竞技场, 帕纳萨斯 - each at
    /// its own 25,135-235 position. Nothing is answered: the claiming and holy-water
    /// actions sit in <c>NpcFunWar.lua</c> under function 3, which cannot be advertised
    /// without displacing the quest page.
    /// </remarks>
    private static readonly ScriptedNpcDialogue BattlefieldCrierDialogue = new(
        FunctionNumber: 27,
        OpeningMenu: [1, 2, 3, 4, 5, 6],
        Steps: new Dictionary<int, int[]>());
}
