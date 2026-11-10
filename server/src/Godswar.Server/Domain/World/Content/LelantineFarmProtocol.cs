using System.Globalization;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// One farm NPC the reference client script knows: the npc key its text and
/// appearance rows carry, and the object id this server places it under.
/// </summary>
internal readonly record struct LelantineFarmNpc(
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    float Facing,
    bool Athenian);

/// <summary>
/// The stock Lelantine Farm defence activity ("利兰丁农场保卫战"), recovered
/// from the September 26 2026 reference capture of 21:00-21:10 plus the
/// client's own shipped data. Everything here is reviewed evidence; nothing is
/// guessed.
/// </summary>
/// <remarks>
/// Verified sources, one per fact:
/// <list type="bullet">
/// <item>Activity name: the capture's client script key is
/// <c>Lelantine_Farm_003</c> and its buttons resolve under
/// <c>NF_L0_FRAM###</c> text keys, i.e. 利兰丁农场 (Lelantine), never 米兰丁.</item>
/// <item>Map: <c>MapTemplateSeed.Generated.cs</c> and
/// <c>database/postgres/008_maps.sql</c> publish map id <c>42</c> /
/// <c>Lelantine_Farm</c> / client scene <c>226</c>.</item>
/// <item>Client function number: the capture answers the farm clicks with
/// <c>S2C 10070 {npc, 47, subId}</c>, so the dialog index - and therefore the
/// <c>NPC_FLAG_SYS_FARM</c> branch of the client's <c>NpcFun.lua</c> - is 47.</item>
/// <item>Entry buttons: the capital Battlefield Transporter advertises
/// <c>#1-1-282</c> / <c>#1-1-1282</c> (<c>tools/ClientPacketContracts</c> output
/// <c>artifacts/npc-dialogue-probe-tags/mapping.md</c> line 634) and the
/// captured click for the farm is <c>C2S 10069 {npc, 1, 282}</c>.</item>
/// <item>Arrival anchors: <c>Address.ini</c> of the farm names
/// <c>Athenian Base = -138,150</c> and <c>Spartan Base = 168,-153</c>, which are
/// the same values <c>008_maps.sql</c> publishes for map 42.</item>
/// </list>
/// </remarks>
internal static class LelantineFarmProtocol
{
    /// <summary>Map 42, the farm scene the activity is fought in.</summary>
    public const int MapId = 42;

    /// <summary>Scene key the published map row carries.</summary>
    public const string SceneKey = "Lelantine_Farm";

    /// <summary>
    /// The client function, and therefore the 10067 dialog index, that selects
    /// <c>NpcFunFarm.lua</c>. The capture answers every farm click with
    /// <c>10070 {npc, 47, ...}</c>.
    /// </summary>
    public const int FarmDialogIndex = 47;

    /// <summary>
    /// The dialog index of <c>NpcFunWar.lua</c>, the reward half of the same
    /// window. The farm's first page offers the reward entries at 71/72/73.
    /// </summary>
    public const int RewardDialogIndex = 3;

    /// <summary>
    /// The dialog index of <c>NpcFunTranmit.lua</c>, which the farm's Returning
    /// Helper uses to send a fighter back to their capital.
    /// </summary>
    public const int TeleportDialogIndex = 1;

    /// <summary>
    /// The sub id the capital Battlefield Transporter's "利兰丁农场保卫战"
    /// button sends. Captured as <c>C2S 10069 {npc, 1, 282}</c>; the
    /// single-channel twin is 1282.
    /// </summary>
    public const int EntrySubId = 282;

    /// <summary>
    /// The "only channel 1" twin of <see cref="EntrySubId"/>. The capture's own
    /// page carries both and this server runs one channel, so both are accepted
    /// and behave identically.
    /// </summary>
    public const int EntrySingleChannelSubId = 1282;

    /// <summary>
    /// The donation page's bulk-donation entry, which the captain's window
    /// offers once the donation page is open.
    /// </summary>
    /// <remarks>
    /// Captured <c>S2C 10070 {5615, 47, 111}</c> at <c>21:00:52</c>, immediately
    /// after the player picked the donation entry from the root menu.
    /// </remarks>
    public const int FarmDonateAllPage = 111;

    /// <summary>
    /// The briefing buttons the captain's window offers. Picking one is
    /// answered with the matching briefing paragraph.
    /// </summary>
    /// <remarks>
    /// Captured <c>S2C 10070 {5615, 47, 31, 32, 33, 34}</c> at
    /// <c>21:01:00</c>, then <c>{60}</c>, <c>{62}</c> and <c>{63}</c> as each
    /// button was picked.
    /// </remarks>
    public static readonly int[] CaptainBriefingButtons = [31, 32, 33, 34];

    /// <summary>
    /// The Advance Troop Captain's root menu, exactly as the capture answers it.
    /// </summary>
    /// <remarks>
    /// Captured <c>S2C 10070 {5615, 47, 101, 102, 103, 104}</c> at
    /// <c>21:00:45</c>, repeated for every one of the six times the player
    /// reopened that npc. The four entries are the activity's own root page:
    /// donation, battlefield ranking, battlefield briefing, and faction points.
    /// </remarks>
    public static readonly int[] CaptainMenuSubIds =
    [
        FarmDonatePageSecond,
        FarmRankingPage,
        FarmIntroPage,
        FarmFactionPointPage
    ];

    /// <summary>
    /// The Quartermaster's own npc keys. Its window is a shop rather than a
    /// dialogue.
    /// </summary>
    /// <remarks>
    /// Captured <c>S2C 10067 {5616, 4, 0, "Lelantine_Farm_004"}</c> at
    /// <c>21:01:31</c>: the flags word is <c>4</c>, which is
    /// <c>CapitalNpcServiceProtocol.ShopOpenFlags</c>, and the client follows it
    /// with a <c>10068</c> page request. Every shop npc in the capture opens
    /// that way, so the quartermaster is a shop and never answers dialog
    /// index 47.
    /// </remarks>
    public static readonly string[] QuartermasterNpcKeys =
        ["Lelantine_Farm_004", "Lelantine_Farm_007"];

    /// <summary>
    /// The Quartermaster's returning-helper sibling check, used by the roster
    /// lookup: the net handout sits on the Quartermaster, not on the captain.
    /// </summary>
    public static bool IsQuartermaster(string npcKey) =>
        QuartermasterNpcKeys.Contains(npcKey, StringComparer.Ordinal);

    /// <summary>
    /// The Advance Troop Captain's keys, the only farm npcs that answer the
    /// activity dialog.
    /// </summary>
    public static readonly string[] CaptainNpcKeys =
        ["Lelantine_Farm_003", "Lelantine_Farm_006"];

    /// <summary>Whether the npc answers the activity dialog window.</summary>
    public static bool IsAdvanceTroopCaptain(string npcKey) =>
        CaptainNpcKeys.Contains(npcKey, StringComparer.Ordinal);

    /// <summary>
    /// "领取网兜" - the Quartermaster's tuck-net handout.
    /// </summary>
    public const int FarmNetClaim = 1;

    /// <summary>"你想要什么样的宝宝" - Kelsis describes the puppy she wants.</summary>
    public const int FarmPetRequest = 5;

    /// <summary>"我给你个犬宝宝" - the hound-puppy hand-in button.</summary>
    public const int FarmPetSubmit = 6;

    /// <summary>The page holding Kelsis' hand-in button.</summary>
    public const int FarmPetSubmitPage = 21;

    /// <summary>"捐赠宠物卵" - the donation page of the Advance Troop Captain.</summary>
    public const int FarmDonatePage = 100;

    /// <summary>The donation page's own entry, used by the second faction.</summary>
    public const int FarmDonatePageSecond = 101;

    /// <summary>"战场介绍" - the activity briefing page.</summary>
    public const int FarmIntroPage = 103;

    /// <summary>"查看阵营积分" - the faction-point query page.</summary>
    public const int FarmFactionPointPage = 104;

    /// <summary>"查看战场排名" - the ranking page.</summary>
    public const int FarmRankingPage = 102;

    /// <summary>
    /// The value scale every number this window draws is carried on. The client
    /// script reads each number <c>n</c> it is sent as
    /// <c>(n - selector) / 1000</c> beside the selector's label.
    /// </summary>
    /// <remarks>
    /// Published client script data: <c>NpcFunFarm.lua</c> of the shipped
    /// client (<c>Localization/zh_cn/UI/XML/NpcFun/NpcFunFarm.lua</c>), the
    /// <c>Index == 2</c> branch, which tests
    /// <c>math.mod(SubID, 1000) == 11</c> and draws
    /// <c>NF_L0_FRAM713 .. ((SubID - 11) / 1000)</c>, and does the same for the
    /// selectors below. The same arithmetic is what
    /// <c>artifacts/npc-dialogue-probe-tags/response-alphabet.md</c> records as
    /// the module's 合成算式.
    /// </remarks>
    public const int ScoreValueScale = 1000;

    /// <summary>
    /// "Your Lelantine Points" - the asking character's own farm score, drawn at
    /// the top of the ranking page.
    /// </summary>
    /// <remarks>
    /// Client label <c>NF_L0_FRAM713</c> ("个人积分" / "Your Lelantine Points").
    /// </remarks>
    public const int PersonalScoreSelector = 11;

    /// <summary>
    /// "Current high score" - the highest personal score on the battlefield,
    /// drawn under the personal score.
    /// </summary>
    /// <remarks>
    /// Client label <c>NF_L0_FRAM714</c> ("最高分" / "Current high score").
    /// </remarks>
    public const int HighestScoreSelector = 12;

    /// <summary>
    /// "Your current ranking" - where the asking character's personal score
    /// stands, drawn under the high score.
    /// </summary>
    /// <remarks>
    /// Client label <c>NF_L0_FRAM715</c> ("你当前分数排名：").
    /// </remarks>
    public const int RankingSelector = 13;

    /// <summary>"Sparta's Lelantine Points" - the Spartan camp's total.</summary>
    /// <remarks>Client label <c>NF_L0_FRAM723</c>.</remarks>
    public const int SpartaScoreSelector = 23;

    /// <summary>"Athens' Lelantine Points" - the Athenian camp's total.</summary>
    /// <remarks>Client label <c>NF_L0_FRAM724</c>.</remarks>
    public const int AthensScoreSelector = 24;

    /// <summary>
    /// The battlefield-statistics reply group, captured as
    /// <c>S2C 10070 {5615, 47, 11, 12, 13}</c> at <c>21:00:56</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's own text keys for this group are <c>NF_L0_FRAM781</c>
    /// ("战场数据统计") and <c>NF_L0_FRAM715</c> ("你当前分数排名："), so this is
    /// the personal half of the activity's 即时比分查询: the player's own score
    /// and where it stands.
    /// </para>
    /// <para>
    /// The capture answers it with the three bare selectors, which the client
    /// draws as a score of zero - the reference session had not scored yet. A
    /// running server sends each selector carrying its value through
    /// <see cref="EncodeScore"/>.
    /// </para>
    /// </remarks>
    public static readonly int[] CaptainStatisticsSubIds = [11, 12, 13];

    /// <summary>
    /// The faction-score reply group, captured as
    /// <c>S2C 10070 {5615, 47, 23, 24}</c> at <c>21:01:23</c>.
    /// </summary>
    /// <remarks>
    /// The client's own text keys for this group are <c>NF_L0_FRAM723</c>
    /// ("斯巴达阵营积分：") and <c>NF_L0_FRAM724</c> ("雅典阵营积分　："), so this
    /// reply draws both factions' running totals - the 阵营比分 half of the same
    /// query. It is deliberately a different group from
    /// <see cref="CaptainStatisticsSubIds"/>, which carries the personal score.
    /// </remarks>
    public static readonly int[] CaptainFactionScoreSubIds = [23, 24];

    /// <summary>
    /// Whether a number is one of the score selectors this window draws.
    /// </summary>
    public static bool IsScoreSelector(int selector) =>
        selector is PersonalScoreSelector or HighestScoreSelector
            or RankingSelector or SpartaScoreSelector or AthensScoreSelector;

    /// <summary>
    /// Packs one score onto its selector, which is how the activity window is
    /// given a number to draw.
    /// </summary>
    /// <param name="selector">
    /// Which line the value is drawn on, one of
    /// <see cref="PersonalScoreSelector"/>, <see cref="HighestScoreSelector"/>,
    /// <see cref="RankingSelector"/>, <see cref="SpartaScoreSelector"/> or
    /// <see cref="AthensScoreSelector"/>.
    /// </param>
    /// <param name="value">The score itself, which cannot be negative.</param>
    /// <remarks>
    /// Encoding a selector with a zero value yields the bare selector, which is
    /// exactly what the reference capture sent.
    /// </remarks>
    public static int EncodeScore(int selector, long value)
    {
        if (!IsScoreSelector(selector))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selector),
                selector,
                "Number is not a Lelantine Farm score selector.");
        }

        if (value is < 0 or > int.MaxValue / ScoreValueScale)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return checked((int)(selector + (value * ScoreValueScale)));
    }

    /// <summary>
    /// The value one encoded score number carries, which is the arithmetic the
    /// client draws with.
    /// </summary>
    public static long DecodeScore(int encoded) =>
        encoded < 0
            ? throw new ArgumentOutOfRangeException(nameof(encoded))
            : encoded / ScoreValueScale;

    /// <summary>"请选择你要领取的奖励" - the reward chooser.</summary>
    public const int FarmRewardPage = 71;

    /// <summary>The "捐赠身上所有犬宝宝" bulk donation button.</summary>
    public const int FarmDonateAll = 112;

    /// <summary>
    /// The word the window's own confirm button sends as the first word of its
    /// click path, i.e. the 确定 entry of the page the client is showing.
    /// </summary>
    /// <remarks>
    /// Measured live on 2026-09-26: with the captain's donation page open, the
    /// client submitted the typed donation as a <c>C2S 10069</c> click on NPC
    /// <c>5620</c> under dialog <c>47</c> whose path began with <c>0</c>. This
    /// server read that word as the quantity, so the donation never happened and
    /// nothing was consumed. The pet-manager pages use the same marker for their
    /// own accept entry (<c>PetManagerProtocol</c>'s <c>arguments[0] == 0</c>),
    /// and the number the player typed rides on its own word rather than in the
    /// path.
    /// </remarks>
    public const int WindowConfirmSubId = 0;

    /// <summary>
    /// The kit-bag slot of the egg the player placed in the donation page's item
    /// control, as the confirmation carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A text-plus-item-control page submits through the window's A1 action,
    /// which appends a literal zero in argument 0 and encodes the item control in
    /// argument 6 as <c>bagPage * 100 + pageSlot</c>. That shape is the stock
    /// client's own and is already decoded for the pet manager's appearance page
    /// (<c>PetManagerProtocol.TryResolveAppearanceChangeMutation</c>), so the
    /// page and slot arithmetic is reused from there rather than restated.
    /// </para>
    /// <para>
    /// Measured live on 2026-09-26 in this very page: placing the egg stack that
    /// sits in kit-bag slot 42 arrived as <c>118</c>, slot 41 arrived as
    /// <c>117</c>, i.e. page 1 slot 18 and page 1 slot 17. Without this word the
    /// donation could only take eggs in slot order, which spent the weakest stack
    /// whatever the player had put in the box.
    /// </para>
    /// </remarks>
    public static bool TryResolveSubmittedEggSlot(
        IReadOnlyList<int> arguments,
        out int absoluteBagSlot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        absoluteBagSlot = -1;
        if (arguments.Count <= PetManagerProtocol.AppearanceChangeItemArgumentIndex)
        {
            return false;
        }

        var coordinate = arguments[PetManagerProtocol.AppearanceChangeItemArgumentIndex];
        if (coordinate < 0)
        {
            return false;
        }

        var bagPage = coordinate / 100;
        var pageSlot = coordinate % 100;
        if (bagPage >= PetManagerProtocol.BagPageCount ||
            pageSlot >= PetManagerProtocol.BagSlotsPerPage)
        {
            return false;
        }

        absoluteBagSlot = checked(
            (bagPage * PetManagerProtocol.BagSlotsPerPage) + pageSlot);
        return true;
    }

    /// <summary>
    /// The reply the script draws after a successful donation.
    /// </summary>
    public const int DonationAccepted = 51;

    /// <summary>"你包裹中当前资质宠物卵数量不足".</summary>
    public const int DonationNotEnough = 52;

    /// <summary>"每次输入值必须在1~99".</summary>
    public const int DonationOutOfRange = 53;

    /// <summary>"请在框中放入犬宝宝卵！".</summary>
    public const int DonationNoEgg = 54;

    /// <summary>
    /// How many points one donation has to be worth before the activity
    /// announces it to the map.
    /// </summary>
    /// <remarks>
    /// This threshold is the one this server was given; it is <b>not</b> a
    /// capture-recovered value. Every frame of all twelve reference captures was
    /// walked for it: the note channel is there (916 opcode-10038 notes), but no
    /// captured frame carries any of the activity's message ids, so the
    /// reference session never donated enough in one booking to announce it.
    /// A donation is announced when it is worth <em>more</em> than this, which
    /// is the wording the activity was described with ("单次捐的卵超过100分").
    /// </remarks>
    public const int DonationBroadcastMinimumPoints = 100;

    /// <summary>
    /// The note type the shipped client renders an announcement of this
    /// activity with.
    /// </summary>
    /// <remarks>
    /// Published client script data: <c>SrvMsg.lua</c> of the shipped client
    /// (<c>Localization/zh_cn/UI/XML/SrvMsg.lua</c>) declares
    /// <c>SrvMsg_NOTE_181 = 33</c> and composes that type as
    /// <c>name .. SrvMsg_Lelantine_msg[note[0]] .. note[2] ..
    /// SrvMsg_Lelantine_msg[note[1]]</c>, i.e. "「名字」捐献犬宝宝宠物蛋，
    /// 斯巴达阵营获得<b>分数</b>积分！" - the Chinese never travels on the wire.
    /// </remarks>
    public const int DonationBroadcastNoteType = 33;

    /// <summary>
    /// The note channel that draws the announcement across the middle of the
    /// screen.
    /// </summary>
    /// <remarks>
    /// <c>SrvMsg.lua</c>'s <c>CHANNEL_MIDDLE = 0</c>, which it renders with
    /// <c>GameAPI:AddProclaimMessage_UTF8</c>.
    /// </remarks>
    public const int DonationBroadcastChannel = 0;

    /// <summary>The Spartan half of the announcement: client <c>SM_51070</c>.</summary>
    public const string DonationSpartaNoteId = "51070";

    /// <summary>The Athenian half: client <c>SM_51080</c>.</summary>
    public const string DonationAthensNoteId = "51080";

    /// <summary>The trailing "积分！" the points are drawn before: <c>SM_51090</c>.</summary>
    public const string DonationPointsNoteId = "51090";

    /// <summary>
    /// Whether a donation worth <paramref name="points"/> is announced to the
    /// map.
    /// </summary>
    public static bool IsAnnouncedDonation(long points) =>
        points > DonationBroadcastMinimumPoints;

    /// <summary>
    /// The note a donation announcement carries: the donor's half of the
    /// message, the points half, and the points themselves, in the order the
    /// client reads them.
    /// </summary>
    public static string BuildDonationBroadcastNote(bool athenian, long points)
    {
        if (points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(points));
        }

        return string.Concat(
            athenian ? DonationAthensNoteId : DonationSpartaNoteId,
            "#",
            DonationPointsNoteId,
            "#",
            points.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"你的包裹满了".</summary>
    public const int BagFull = 91;

    /// <summary>"我不是已经给你网兜了么".</summary>
    public const int NetAlreadyClaimed = 92;

    /// <summary>"你这么快就把网兜都用完了" - the five-net top-up.</summary>
    public const int NetTopUp = 93;

    /// <summary>"这里有40个木质网兜" - the first handout.</summary>
    public const int NetGranted = 94;

    /// <summary>
    /// Kelsis' three option buttons, which the script draws as a second page.
    /// </summary>
    public static readonly int[] KelsisMenuSubIds = [FarmPetRequest, FarmPetSubmit];

    /// <summary>The briefing text buttons the farm window offers.</summary>
    public static readonly int[] BriefingSubIds = [31, 32, 33, 34];

    /// <summary>Minimum level the activity admits, from its own refusal text.</summary>
    public const int MinimumLevel = 31;

    /// <summary>Maximum level the activity admits, from its own refusal text.</summary>
    public const int MaximumLevel = 140;

    /// <summary>
    /// The authored farm roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The npc keys, template keys and faction split are published client data
    /// (<c>database/postgres/007_npcs.sql</c> rows 542-549 and the appearance
    /// rows 1681-1688). The two Advance Troop Captains and their Quartermasters
    /// are confirmed by the capture itself: the farm window was answered for
    /// <c>Lelantine_Farm_003</c> (Athens) and <c>Lelantine_Farm_004</c>
    /// (Athens Quartermaster), and their text rows are the雅典/斯巴达 branches of
    /// the same script.
    /// </para>
    /// <para>
    /// The <b>object ids</b> are the reference server's own: the capture answers
    /// clicks on <c>5615</c>, <c>5616</c> and <c>5617</c> by script key, and the
    /// block is contiguous from <c>5615</c>, so the remaining five ids follow
    /// that numbering. One <b>placement</b> is captured as well - the
    /// <c>10020</c> frame at <c>21:03:04</c> puts object <c>5617</c> on map 42 at
    /// <c>(-141, 161)</c> facing <c>2.30</c>. The other coordinates are this
    /// server's authored placement, taken from the farm's own
    /// <c>Address.ini</c>: the two Advance Troop Captains stand on their own
    /// faction's base as that file names those bases, and Kelsis and Ninto sit at
    /// the <c>Kelsis and Ninto</c> point (6,5) the same file names. The published
    /// <c>npc_spawn_definitions</c> has no map 42 rows, so nothing here displaces
    /// an actor that already exists on another map.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The npc object ids the reference capture itself used for the farm roster.
    /// </summary>
    /// <remarks>
    /// These are captured values, not authored ones. The 2026-09-26 capture
    /// shows the client clicking three farm npcs and the reference server
    /// answering by script key:
    /// <list type="bullet">
    /// <item><c>21:00:44</c> click <c>5615</c> answered as
    /// <c>Lelantine_Farm_003</c></item>
    /// <item><c>21:01:31</c> click <c>5616</c> answered as
    /// <c>Lelantine_Farm_004</c></item>
    /// <item><c>21:01:36</c> click <c>5617</c> answered as
    /// <c>Lelantine_Farm_005</c>, and the same session carries its
    /// <c>10020</c> world-object frame at <c>21:03:04</c> naming object
    /// <c>5617</c> on map 42 at <c>(-141, 161)</c> facing <c>2.30</c></item>
    /// </list>
    /// The block is contiguous from 5615, so the remaining five ids follow the
    /// captured numbering. The array is ordered to match
    /// <see cref="Npcs"/>; the ids are therefore not in ascending order.
    /// </remarks>
    public static readonly uint[] AllowedFarmNpcIds =
        [5618u, 5619u, 5615u, 5616u, 5617u, 5620u, 5621u, 5622u];

    public static readonly LelantineFarmNpc[] Npcs =
    [
        new("Lelantine_Farm_001", "Lelantine_Farm_001_Male8", 5618u,
            11f, 5f, 3f, Athenian: true),
        new("Lelantine_Farm_002", "Lelantine_Farm_002_WarField4", 5619u,
            6f, 10f, 0f, Athenian: true),
        new("Lelantine_Farm_003", "Lelantine_Farm_003_WarField3", 5615u,
            -138f, 154f, 4.6f, Athenian: true),
        new("Lelantine_Farm_004", "Lelantine_Farm_004_WarField2", 5616u,
            -132f, 150f, 3.1f, Athenian: true),
        // The one placement the capture actually recorded: the 10020 frame at
        // 21:03:04 puts object 5617 at (-141, 161) facing 2.30 on map 42.
        new("Lelantine_Farm_005", "Lelantine_Farm_005_WarField1", 5617u,
            -141f, 161f, 2.3f, Athenian: true),
        new("Lelantine_Farm_006", "Lelantine_Farm_006_WarField3", 5620u,
            168f, -157f, 1.6f, Athenian: false),
        new("Lelantine_Farm_007", "Lelantine_Farm_007_WarField2", 5621u,
            162f, -153f, 0f, Athenian: false),
        new("Lelantine_Farm_008", "Lelantine_Farm_008_WarField1", 5622u,
            170f, -149f, 0f, Athenian: false)
    ];

    /// <summary>
    /// The farm's Returning Helper teleports home through the stock
    /// <c>NpcFunTranmit.lua</c> dialog index 1. Its single button is the map
    /// return the same script uses for every battlefield's "Returning Helper"
    /// (<c>WarField_001</c> / <c>WarField_006</c>), whose captured sub id is 1.
    /// </summary>
    public const int FarmReturnSubId = 1;

    /// <summary>
    /// The Returning Helper's whole menu: the one capital teleport.
    /// </summary>
    public static readonly int[] FarmReturnMenuSubIds = [FarmReturnSubId];

    /// <summary>
    /// Appearance word the farm actors are drawn with. It is the word the
    /// capture's own Advance Troop Captain was answered for
    /// (<c>0x211</c>, the WarField-class actor word also used by
    /// <c>Parnitha_1_003_WarField1</c>).
    /// </summary>
    public const uint AppearanceType = 0x211u;

    /// <summary>
    /// Where a fighter lands on entry: their own faction's base on the farm, as
    /// the farm's <c>Address.ini</c> names it.
    /// </summary>
    public static (float X, float Z) Arrival(bool athenian) =>
        athenian ? (-138f, 150f) : (168f, -153f);

    /// <summary>
    /// Whether the given npc key belongs to the farm roster.
    /// </summary>
    public static bool IsFarmNpcKey(string npcKey) =>
        npcKey.StartsWith("Lelantine_Farm_", StringComparison.Ordinal);

    /// <summary>
    /// Resolves the farm actor a npc key and object id identify.
    /// </summary>
    public static bool TryResolve(
        string npcKey,
        uint interactionId,
        out LelantineFarmNpc npc)
    {
        foreach (var candidate in Npcs)
        {
            if (candidate.ObjectId == interactionId &&
                string.Equals(candidate.NpcKey, npcKey, StringComparison.Ordinal))
            {
                npc = candidate;
                return true;
            }
        }

        npc = default;
        return false;
    }

    /// <summary>
    /// Whether the endpoint is the farm's Returning Helper, whose whole window
    /// is the capital teleport.
    /// </summary>
    public static bool IsReturningHelper(string npcKey) =>
        npcKey is "Lelantine_Farm_005" or "Lelantine_Farm_008";
}
