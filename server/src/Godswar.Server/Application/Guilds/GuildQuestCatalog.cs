namespace Godswar.Server.Application.Guilds;

/// <summary>
/// One guild quest's guild half, transcribed from the client.
/// </summary>
/// <param name="Kind">
/// The Quest Supervisor button that releases it: 1 = "Delivery and Materials
/// Transportation" (NF_L0_98, the Message Delivery / Messenger quests), 2 = "Kill
/// monsters" (NF_L0_99, the Leader's Order / Guild Quest kill quests).
/// </param>
/// <param name="GuildGold">"Guild Gold" - paid into the guild's gold.</param>
/// <param name="GuildFund">"Guild Fund" - paid into the guild's silver.</param>
/// <param name="Contribution">"Contribution" - paid to the member.</param>
internal readonly record struct GuildQuestReward(
    int Kind,
    long GuildGold,
    long GuildFund,
    int Contribution);

/// <summary>
/// GUILD (2026-11-03): the guild quests of the [Guild] Quest Supervisor
/// (Sparta_039 / Athens_038; <c>UIQuestSort</c> 4 in the quest chain) and what
/// their reward text promises the guild.
/// </summary>
/// <remarks>
/// Not invented: every number is the quest's own <c>Append</c> block in the
/// stock client's <c>Localization/en_us/Text/Quest/&lt;id&gt;.dat</c>, read by
/// <c>Development/build/guild/tools/gen_guild_quests.py</c>. The quest chain
/// (<c>StarterQuestChain</c>) already pays the EXP and TP; its <c>Gold</c>
/// column holds the same number as the text's "Guild Gold", which the stock
/// hand-in paid to the player as personal gold. With <c>guild.quest_rules</c>
/// on it goes to the guild instead (the text says Guild Gold). The War Material
/// chain (389-392 / 1389-1392) has no guild reward and is not listed.
/// </remarks>
internal static class GuildQuestCatalog
{
    public const int DeliveryKind = 1;

    public const int KillKind = 2;

    private static readonly Dictionary<uint, GuildQuestReward> Rewards = new()
    {
        [207] = new(1, 0, 200, 200), // [Lv10]Message Delivery
        [208] = new(1, 0, 300, 300), // [Lv25]Message Delivery
        [209] = new(1, 0, 400, 400), // [Lv40]Message Delivery
        [210] = new(2, 30, 1600, 3000), // [Lv21]Leader's Order
        [211] = new(2, 40, 2000, 3500), // [Lv34]Leader's Order
        [212] = new(2, 50, 2400, 6000), // [Lv51]Guild Quest: Dark Wizards
        [223] = new(1, 0, 500, 500), // [Lv55]Message Delivery
        [224] = new(1, 0, 600, 600), // [Lv70]Message Delivery
        [225] = new(1, 0, 800, 800), // [Lv85]Message Delivery
        [226] = new(1, 0, 1000, 1000), // [Lv100]Message Delivery
        [227] = new(2, 60, 3000, 8000), // [Lv64]Guild Quest: Huge Frogs
        [228] = new(2, 80, 3300, 13000), // [Lv81]Leader's Order
        [229] = new(2, 100, 3600, 16000), // [Lv94]Guild Quest: Deigo Pirates
        [230] = new(2, 120, 4200, 18000), // [Lv111]Guild Quest: Harpies
        [369] = new(1, 0, 1500, 1500), // [Lv121]Messenger Service
        [370] = new(2, 160, 5000, 24000), // [Lv121]Orders From Above
        [1207] = new(1, 0, 200, 200), // [Lv10]Message Delivery
        [1208] = new(1, 0, 300, 300), // [Lv25]Message Delivery
        [1209] = new(1, 0, 400, 400), // [Lv40]Message Delivery
        [1210] = new(2, 30, 1600, 3000), // [Lv21]Guild Quest: Wild Oxen
        [1211] = new(2, 40, 2000, 3500), // [Lv34]Leader's Order
        [1212] = new(2, 50, 2400, 6000), // [Lv51]Leader's Order
        [1223] = new(1, 0, 500, 500), // [Lv55]Message Delivery
        [1224] = new(1, 0, 600, 600), // [Lv70]Message Delivery
        [1225] = new(1, 0, 800, 800), // [Lv85]Message Delivery
        [1226] = new(1, 0, 1000, 1000), // [Lv100]Message Delivery
        [1227] = new(2, 60, 3000, 8000), // [Lv64]Leader's Order
        [1228] = new(2, 80, 3300, 13000), // [Lv81]Leader's Order
        [1229] = new(2, 100, 3600, 16000), // [Lv94]Leader's Order
        [1230] = new(2, 120, 4200, 18000), // [Lv111]Guild Quest: Wind Elves
        [1369] = new(1, 0, 1500, 1500), // [Lv121]Messenger Service
        [1370] = new(2, 160, 5000, 24000), // [Lv121]Orders From Above
    };

    public static bool TryFind(uint questId, out GuildQuestReward reward) =>
        Rewards.TryGetValue(questId, out reward);

    public static IReadOnlyCollection<uint> QuestIds => Rewards.Keys;
}
