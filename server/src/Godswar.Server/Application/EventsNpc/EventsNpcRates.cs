using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.EventsNpc;

// EVENTSNPC: the three event switches (Lost Book, GodsWar Marathon, Random Quest Manager) and the one
// Marathon option. Every switch defaults to 0, so a binary with this patch behaves exactly like the base
// build until an operator turns an event on in server_rates (hot reload, no restart).
internal static class EventsNpcRateKeys
{
    public const string LostBook = "event.lost_book";
    public const string Marathon = "event.marathon";
    public const string RandomQuest = "event.random_quest";
    public const string MarathonCheckAnswer = "event.marathon_check_answer";
}

internal static class EventsNpcRates
{
    public const string Category = "Event NPCs";

    public static bool LostBookOn => On(EventsNpcRateKeys.LostBook);

    public static bool MarathonOn => On(EventsNpcRateKeys.Marathon);

    public static bool RandomQuestOn => On(EventsNpcRateKeys.RandomQuest);

    public static bool MarathonChecksAnswer => On(EventsNpcRateKeys.MarathonCheckAnswer);

    private static bool On(string key) => ServerRates.Current.Get(key) >= 1m;

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(EventsNpcRateKeys.LostBook, Category, "Lost Book event", "0/1", ServerRateKind.Absolute,
            0m, 0m, 1m, 1m, 10, "Game/GameClientHandler.EventsNpc.LostBook.cs",
            "1 = Mysterious Elder gives Sheepskin Scroll 7 daily and joins chapters, the 14 Ancient Relics " +
            "appear and trade scrolls for chapters + EXP, the Wishing Pool takes Lost Books (5 incomplete + " +
            "2 complete a day, IGG). 0 = menus only, as before."),
        new(EventsNpcRateKeys.Marathon, Category, "GodsWar Marathon (health run)", "0/1", ServerRateKind.Absolute,
            0m, 0m, 1m, 1m, 20, "Game/GameClientHandler.EventsNpc.Marathon.cs",
            "1 = Fitness Trainers 1-5 (suburbs) and 8-10 (capital) appear and the race runs: Lv60+, " +
            "Monday-Saturday, Health Manual 1-17, 180 B-Gold for the first two races a day, 90 after, " +
            "6 races a day (IGG). 0 = trainers 1-5 and 8-10 absent, 6-7 menu only, as before."),
        new(EventsNpcRateKeys.RandomQuest, Category, "Random Quest Manager (Lv70)", "0/1", ServerRateKind.Absolute,
            0m, 0m, 1m, 1m, 30, "Game/GameClientHandler.EventsNpc.RandomQuest.cs",
            "1 = the Random Quest Manager gives a Quest Sack (Lv70+), the sack gives a random faction quest " +
            "scroll, each finished chain quest pays Exp Pills and the next sack, the 10th a Treasure Map; " +
            "the manager also takes Exp Pills. 0 = menu only and the old sack contents, as before."),
        new(EventsNpcRateKeys.MarathonCheckAnswer, Category, "Marathon: check the sum", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 40, "Game/GameClientHandler.EventsNpc.Marathon.cs",
            "1 = a wrong answer to the trainer's sum is refused. 0 = any answer is accepted. Keep 0 until " +
            "the real client's answer field is captured (UNPROVEN, see Development/build/eventsnpc/README.md).")
    ];
}
