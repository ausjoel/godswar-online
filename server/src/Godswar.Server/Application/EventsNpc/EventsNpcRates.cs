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
            "Runs the Lost Book event. On: the Mysterious Elder gives Sheepskin Scroll 7 each day " +
            "and joins chapters, the 14 Ancient Relics appear and trade scrolls for chapters and " +
            "EXP, and the Wishing Pool takes Lost Books (5 incomplete and 2 complete a day). Off: " +
            "the NPCs only show their menus."),
        new(EventsNpcRateKeys.Marathon, Category, "GodsWar Marathon (health run)", "0/1", ServerRateKind.Absolute,
            0m, 0m, 1m, 1m, 20, "Game/GameClientHandler.EventsNpc.Marathon.cs",
            "Runs the GodsWar Marathon race. On: the Fitness Trainers appear and players of level " +
            "60 or more can race Monday to Saturday, up to 6 races a day (180 B-Gold for the " +
            "first two, 90 after). Off: most trainers are absent and the rest only show their " +
            "menus."),
        new(EventsNpcRateKeys.RandomQuest, Category, "Random Quest Manager (Lv70)", "0/1", ServerRateKind.Absolute,
            0m, 0m, 1m, 1m, 30, "Game/GameClientHandler.EventsNpc.RandomQuest.cs",
            "Runs the Random Quest Manager chain for level 70 and above. On: the manager gives a " +
            "Quest Sack with a random quest scroll; each finished quest pays Exp Pills and the " +
            "next sack, and the 10th pays a Treasure Map. Off: menu only, and sacks keep their " +
            "old contents."),
        new(EventsNpcRateKeys.MarathonCheckAnswer, Category, "Marathon: check the sum", "0/1",
            ServerRateKind.Absolute, 0m, 0m, 1m, 1m, 40, "Game/GameClientHandler.EventsNpc.Marathon.cs",
            "Checks the sum a Marathon trainer asks. On: a wrong answer is refused and the " +
            "manual is kept. Off: any answer is accepted. Tested with the real game.")
    ];
}
