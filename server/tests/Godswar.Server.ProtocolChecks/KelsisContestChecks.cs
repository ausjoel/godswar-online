using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// KELSIS (F-15, 2026-11-09): Delighted Kelsis' lowest-stat dog contest. The reply numbers are NpcFunFarm.lua's
// (function 47) and NpcFunWar.lua's (Awarder 207/208/219). server/docs/kelsis-contest/README.md.
internal static class KelsisContestChecks
{
    public const string RulesCheckName = "KELSIS contest replies, stat encoding and lowest-only rule follow NpcFunFarm.lua";
    public const string WiringCheckName = "KELSIS contest is switched off by default and leaves the dialogue baseline alone";

    public static Task RunRulesAsync()
    {
        // The stat Kelsis asks for: 1..7, fixed per round, and every stat comes up over a year of rounds.
        var saturday = new DateTimeOffset(2026, 11, 7, 12, 45, 0, TimeSpan.Zero);
        var seen = new HashSet<int>();
        for (var week = 0; week < 52; week++)
        {
            var stat = KelsisContestPolicy.StatForRound(saturday.AddDays(7 * week));
            Check.True(stat is >= 1 and <= 7, "the round stat is one of the seven the script names");
            Check.Equal(stat, KelsisContestPolicy.StatForRound(saturday.AddDays(7 * week)), "the round stat is stable");
            seen.Add(stat);
        }

        Check.Equal(7, seen.Count, "all seven stats come up across 52 rounds");
        Check.Equal(0, (int)KelsisContestPolicy.StatCode(1), "Rank reads character_pets.rank (spell stat 0)");
        Check.Equal(6, (int)KelsisContestPolicy.StatCode(7), "Luck reads stat_code 6");

        // "Pet stat:" — the script tests SubID % 100 == 9 and draws (SubID - 9) / 10000 with %.2f.
        foreach (var value in new[] { 0m, 0.01m, 1.23m, 2.5m, 13.999m, 150.07m })
        {
            var encoded = KelsisContestPolicy.EncodeStatValue(value);
            Check.Equal(9, encoded % 100, $"{value} is drawn by the x09 branch");
            Check.Equal(decimal.Round(value, 2, MidpointRounding.AwayFromZero),
                KelsisContestPolicy.DecodeStatValue(encoded), $"{value} draws as itself to two decimals");
            Check.True(encoded % 1000 is not (11 or 12 or 13 or 14 or 15 or 16 or 23 or 24),
                $"{value} cannot be mistaken for a score line");
        }

        Check.Equal(12309, KelsisContestPolicy.EncodeStatValue(1.23m), "1.23 is sent as 12309");

        // Lowest-only (the admin F-15): strictly lower than the round's best; the first dog is always taken.
        Check.True(KelsisContestPolicy.Accepts(1.5m, null), "the first dog of a round is taken");
        Check.True(KelsisContestPolicy.Accepts(1.49m, 1.5m), "a lower dog is taken");
        Check.True(!KelsisContestPolicy.Accepts(1.5m, 1.5m), "a tie keeps the earlier dog");
        Check.True(!KelsisContestPolicy.Accepts(1.51m, 1.5m), "a higher dog is refused");

        // Page 1 / 2 / 3 words, each one a number that exists in its Index branch of NpcFunFarm.lua.
        Check.True(KelsisContestPolicy.OpeningMenu.SequenceEqual([5, 6]), "page 1 is FRAM205 (5) and FRAM206 (6)");
        Check.True(KelsisContestPolicy.RequestReply(3).SequenceEqual([10003]), "Strength request is 10003");
        Check.True(KelsisContestPolicy.SubmitPageReply(2, false, null, null).SequenceEqual([20002, 17, 21]),
            "nobody yet: stat line, 17, submit button");
        Check.True(KelsisContestPolicy.SubmitPageReply(2, true, true, 1.2m).SequenceEqual([20002, 18, 12009, 21]),
            "own dog best: 18 and its value");
        Check.True(KelsisContestPolicy.SubmitPageReply(2, true, false, 1.3m).SequenceEqual([20002, 19, 13009, 21]),
            "own dog beaten: 19");
        Check.True(KelsisContestPolicy.SubmitPageReply(7, true, null, 0.8m).SequenceEqual([20007, 8009, 21]),
            "others gave dogs, this player has none: summoned dog's value, no standing line");
        var pageTwo = new HashSet<int> { 17, 18, 19, 21 };
        Check.True(KelsisContestPolicy.SubmitPageReply(1, true, false, 99.99m)
                .All(n => pageTwo.Contains(n) || n is >= 20001 and <= 20007 || n % 100 == 9),
            "every page-2 number is drawn by an Index 2 branch");
        Check.True(new[] { KelsisContestPolicy.Submitted, KelsisContestPolicy.NotEligible, KelsisContestPolicy.BagFull,
                KelsisContestPolicy.NotSummoned, KelsisContestPolicy.NotADog }.SequenceEqual([300, 400, 500, 600, 700]),
            "page-3 results are the script's 300-700");
        Check.True(KelsisContestPolicy.AwarderWon == 208 && KelsisContestPolicy.AwarderLost == 219 &&
            KelsisContestPolicy.PrizeBindingGold == 2300, "Awarder lines 208/219 and the 2,300 B-Gold prize");

        Check.True(KelsisContestPolicy.Classify([]) == KelsisClick.PageRequest, "no path = page request");
        Check.True(KelsisContestPolicy.Classify([5]) == KelsisClick.Ask, "[5] = ask");
        Check.True(KelsisContestPolicy.Classify([6]) == KelsisClick.Give, "[6] = give page");
        Check.True(KelsisContestPolicy.Classify([6, 21]) == KelsisClick.Submit, "[6,21] = submit");
        Check.True(KelsisContestPolicy.Classify([21]) == KelsisClick.Unknown &&
            KelsisContestPolicy.Classify([5, 21]) == KelsisClick.Unknown, "a submit not under the give page is ignored");
        return Task.CompletedTask;
    }

    public static Task RunWiringAsync()
    {
        var definition = ServerRateCatalog.Get(Finalise2RateKeys.KelsisContest);
        Check.Equal(0m, definition.DefaultValue, "npc.kelsis_contest defaults to 0 (it takes the player's dog)");
        Check.True(definition.Kind == ServerRateKind.Absolute && definition.MaximumValue == 1m, "a 0/1 switch");

        // Kelsis opens like the captured Advance Troop Captain: 10067 {npc, 0x200, 47, key}.
        var captain = PacketBuilder.NpcDialogOpenAck(5615u, [47], "Lelantine_Farm_003");
        var kelsis = PacketBuilder.NpcDialogOpenAck(5619u, [KelsisContestPolicy.FunctionNumber], KelsisContestPolicy.NpcKey);
        Check.Equal(BinaryPrimitives.ReadUInt32LittleEndian(captain.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(kelsis.AsSpan(8)), "same flags word as the captain");
        Check.Equal(BinaryPrimitives.ReadUInt32LittleEndian(captain.AsSpan(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(kelsis.AsSpan(12)), "same function word (47) as the captain");

        var source = ServerSource();
        string Read(string relative) => File.ReadAllText(Path.Combine(source, relative));
        var routing = Read("Game/ScriptedNpcDialogueCatalog.Routing.cs");
        Check.True(routing.Contains("KelsisContestPolicy.NpcKey when KelsisContestOn", StringComparison.Ordinal),
            "Kelsis is routed through the scripted table only while the switch is on");
        Check.True(!Read("Infrastructure/WorldContent/NpcDialogueBaselineV24.cs").Contains("KelsisContest",
            StringComparison.Ordinal), "the hash-pinned dialogue baseline is untouched");
        var handler = Read("Game/GameClientHandler.KelsisContest.cs");
        var snapshot = handler.IndexOf("'pending', jsonb_build_object(", StringComparison.Ordinal);
        var discard = handler.IndexOf("PetPresenceOperation.Delete", StringComparison.Ordinal);
        Check.True(snapshot > 0 && discard > snapshot, "the pet rows are snapshotted before the dog is discarded");
        Check.True(handler.Contains("[kelsis] words", StringComparison.Ordinal) &&
            handler.Contains("EventsNpcWords(packet)", StringComparison.Ordinal), "every request's words are logged");
        Check.True(Read("Game/GameClientHandler.BattlefieldAwarder.cs").Contains(
            "ClaimKelsisPrizeAtAwarderAsync", StringComparison.Ordinal), "Awarder entry 73 answers the prize");
        var schema = Godswar.Server.Infrastructure.EventsNpc.PostgresEventsNpcStore.SchemaSql;
        Check.True(schema.Contains("CREATE TABLE IF NOT EXISTS public.farm_kelsis_submissions", StringComparison.Ordinal) &&
            schema.Contains("pet_snapshot jsonb NOT NULL", StringComparison.Ordinal),
            "first-use DDL creates the reversible record table");
        Check.True(!schema.Contains("schema_migrations", StringComparison.Ordinal), "no registered migration");
        return Task.CompletedTask;
    }

    private static string ServerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory.FullName, "src", "Godswar.Server"),
                         Path.Combine(directory.FullName, "server", "src", "Godswar.Server")
                     })
            {
                if (Directory.Exists(Path.Combine(candidate, "Game")))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("server source not found");
    }
}
