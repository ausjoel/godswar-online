using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.ProtocolChecks;

/// <summary>WARRIOR scrolls (F-146 b): scrolls 4838-4851 arm a hidden "kill 1 Magma Scorpion" task.</summary>
internal static class WarriorTrainingScrollChecks
{
    public const string CheckName =
        "WARRIOR scrolls (F-146 b): 4838-4851 arm a hidden Magma Scorpion task, GENERATED EXP by band";

    public static Task RunAsync()
    {
        int[] bands = [60, 70, 80, 90, 100, 110, 120];
        int[] experience = [50_000, 70_000, 100_000, 140_000, 180_000, 230_000, 300_000];
        for (var item = WarriorTrainingScrollPolicy.FirstItemId; item <= WarriorTrainingScrollPolicy.LastItemId; item++)
        {
            var offset = item - WarriorTrainingScrollPolicy.FirstItemId;
            Check.True(WarriorTrainingScrollPolicy.TryResolve(item, out var scroll), $"{item} is a Warriors' scroll");
            Check.True(scroll.Camp == (offset < 7 ? 1 : 0) && scroll.BandLow == bands[offset % 7] &&
                       scroll.BandHigh == (offset % 7 == 6 ? 130 : bands[offset % 7] + 9) &&
                       scroll.Experience == experience[offset % 7],
                $"{item}: camp {scroll.Camp}, band {scroll.BandLow}-{scroll.BandHigh}, {scroll.Experience} EXP " +
                "(client Camp field: 4838-4844 Athens, 4845-4851 Sparta)");
        }
        Check.True(!WarriorTrainingScrollPolicy.TryResolve(4837, out _) && !WarriorTrainingScrollPolicy.TryResolve(4852, out _),
            "4837 and 4852 are not Warriors' scrolls");
        // The client's Quest field points 4838-4843 / 4845-4850 at the Parnassus quests; the new path runs first.
        Check.True(QuestRuntimeRequirements.ScrollItems.TryGetValue(4838u, out var parnassus) && parnassus == 1566u,
            "4838 still names quest 1566 in the client data (why the Warriors' path must run before the quest scroll path)");
        Check.True(Enumerable.Range(4678, 100).All(static id => !WarriorTrainingScrollPolicy.IsScroll(id)),
            "no overlap with the Random Quest Scrolls 4678-4777");

        string[] lines =
        [
            WarriorTrainingScrollPolicy.ArmedLine,
            WarriorTrainingScrollPolicy.RewardLine(new(4844, 1, 120, 130, 300_000)),
            WarriorTrainingScrollPolicy.AlreadyArmedLine,
            WarriorTrainingScrollPolicy.WrongCampLine,
            WarriorTrainingScrollPolicy.CompletedLine(300_000)
        ];
        Check.True(lines.All(static line => line.Length <= 62 && line.All(static c => c is >= ' ' and <= '~' and not '|' and not '#')),
            "every info line is plain ASCII and fits one 63-byte note field");

        var schema = PostgresEventsNpcStore.SchemaSql;
        var table = schema.IndexOf("CREATE TABLE IF NOT EXISTS public.warrior_scroll_tasks (", StringComparison.Ordinal);
        var nextTable = schema.IndexOf("CREATE TABLE IF NOT EXISTS", table + 10, StringComparison.Ordinal);
        Check.True(table >= 0 && nextTable > table &&
                   System.Text.RegularExpressions.Regex.IsMatch(schema[table..nextTable], @"paid_at timestamptz NULL\s*\);"),
            "runtime DDL: warrior_scroll_tasks is created and closed with its own ');' before the next table");
        Check.True(schema.Contains("CREATE UNIQUE INDEX IF NOT EXISTS warrior_scroll_tasks_open", StringComparison.Ordinal) &&
                   schema.Contains("WHERE completed_at IS NULL;", StringComparison.Ordinal),
            "runtime DDL: one open task per character (partial unique index)");
        var tables = schema.Split("CREATE TABLE IF NOT EXISTS").Length - 1;
        var closes = System.Text.RegularExpressions.Regex.Matches(schema, @"^\s*\);\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        Check.Equal(tables, closes, "every CREATE TABLE in the events schema has its own closing ');'");
        return Task.CompletedTask;
    }
}
