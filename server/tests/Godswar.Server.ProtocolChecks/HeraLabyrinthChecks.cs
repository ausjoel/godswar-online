using System.Text.RegularExpressions;
using Godswar.Server.Application.Gates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// HERA'S LABYRINTH (the admin F-161): switch, Event Transporter 500/501, rosters, the GENERATED maze, rewards and limits.
internal static class HeraLabyrinthChecks
{
    public const string CheckName = "HERA'S LABYRINTH: gated, maze connected, landings clear of portals, rewards match the client";

    private static readonly string[] Variables =
    [
        HeraLabyrinthProtocol.FeatureVariable, HeraLabyrinthProtocol.WindowVariable, PanInvitationProtocol.EnvoyVariable,
        HeraLabyrinthProtocol.AnnouncementVariable
    ];

    private static readonly int[] CapturedTransporterMenu = [200, 700, 501, 600, 202];

    public static Task RunAsync()
    {
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            CheckSwitchedOff();
            CheckLayout();
            CheckRewards();
            CheckGateAndItems();
            Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.FeatureVariable, "1");
            CheckSwitchedOn();
            CheckWarSupplier();
            CheckAnnouncement();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        return Task.CompletedTask;
    }

    private static void CheckSwitchedOff()
    {
        Check.True(!HeraLabyrinthProtocol.IsEnabled, "Hera's Labyrinth is off by default");
        Check.True(!HeraLabyrinthProtocol.TryResolveEntry("Athens_072", 1, 500, out _), "500 not intercepted while off");
        Check.True(!HeraLabyrinthProtocol.TryResolveEntry("Sparta_072", 1, 501, out _), "501 not intercepted while off");
        Check.True(ReferenceEquals(CapturedTransporterMenu,
            HeraLabyrinthProtocol.WithEntryButton("Athens_072", 1, CapturedTransporterMenu)), "Athens menu unchanged while off");
        IReadOnlyList<NpcSpawnDefinition> empty = [];
        Check.Equal(0, HeraLabyrinthProtocol.AddNpcs(30, empty).Count, "map 30 roster unchanged while off");
        Check.Equal(0, HeraLabyrinthProtocol.AddNpcs(31, empty).Count, "map 31 roster unchanged while off");
        var portal = HeraLabyrinthLayout.Portals[0];
        Check.True(!HeraLabyrinthProtocol.TryResolvePortal(30, portal.X - 3, portal.Z, portal.X + 3, portal.Z, 6f,
            out _, out _, out _, out _), "portals inert while off");
        Check.True(!HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Athens_076", 114, 1010, out _),
            "War Supplier deposit not intercepted while off");
        Check.Equal(0, HeraLabyrinthProtocol.AnnouncementTimes().Count, "no announcement while off");
        var npc = HeraLabyrinthProtocol.Npcs(30)[0];
        Check.True(!HeraLabyrinthProtocol.IsHeraNpc(npc) && !HeraLabyrinthProtocol.IsZeusEnvoy(npc),
            "no actor is claimed while off");
    }

    private static void CheckLayout()
    {
        var portals = HeraLabyrinthLayout.Portals;
        Check.Equal(35, portals.Length, "35 scene_transport_002 portals");
        for (var isle = 1; isle <= 9; isle++)
        {
            var count = portals.Count(p => p.Isle == isle);
            Check.Equal(isle == 5 ? 3 : 4, count, $"isle {isle} portal count (centre 3 + the pocket exit)");
            Check.True(portals.Where(p => p.Isle == isle).All(p => p.Destination is >= 1 and <= 9 && p.Destination != isle),
                $"isle {isle} has no portal to itself");
        }

        var reach = new HashSet<int> { 1 };
        var queue = new Queue<int>([1]);
        while (queue.TryDequeue(out var at))
        {
            foreach (var p in portals.Where(p => p.Isle == at))
            {
                if (reach.Add(p.Destination))
                {
                    queue.Enqueue(p.Destination);
                }
            }
        }

        Check.Equal(9, reach.Count, "every isle reachable from Isle 1 (the pocket exit's destination)");
        foreach (var start in Enumerable.Range(1, 9))
        {
            var seen = new HashSet<int> { start };
            var q = new Queue<int>([start]);
            while (q.TryDequeue(out var at))
            {
                foreach (var p in portals.Where(p => p.Isle == at))
                {
                    if (seen.Add(p.Destination))
                    {
                        q.Enqueue(p.Destination);
                    }
                }
            }

            Check.Equal(9, seen.Count, $"isle {start} reaches every isle (strongly connected)");
        }

        var triggers = portals.Select(static p => (p.X, p.Z)).Append(HeraLabyrinthLayout.ExitToIsleOne).ToArray();
        static double Distance((float X, float Z) a, (float X, float Z) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
        var clearance = HeraLabyrinthLayout.LandingClearance;
        Check.True(clearance > 6f + 2f, "landing clearance exceeds the trigger radius with slack");
        for (var isle = 1; isle <= 9; isle++)
        {
            var landing = HeraLabyrinthLayout.Landings[isle];
            Check.True(triggers.All(t => Distance(t, landing) >= clearance), $"isle {isle} landing is clear of every portal");
        }

        Check.True(triggers.All(t => Distance(t, HeraLabyrinthProtocol.Arrival) >= clearance), "arrival clear of every portal");
        Check.True(Enumerable.Range(1, 5).All(k => triggers.All(t => Distance(t, HeraLabyrinthLayout.Envoys[k]) >= clearance)),
            "envoys clear of every portal");
        Check.True(triggers.All(t => Distance(t, HeraLabyrinthLayout.ZeusEnvoy) >= clearance), "Zeus' Envoy clear of every portal");
        Check.Equal((167f, -157f), HeraLabyrinthLayout.ZeusEnvoy, "Zeus' Envoy at the Quest.xml responder point");
        Check.True(Enumerable.Range(1, 5).All(k => Distance(HeraLabyrinthLayout.Envoys[k], HeraLabyrinthLayout.Landings[k + 1]) <= 10),
            "Envoy k stands on Isle k+1 (five different isles)");
    }

    private static void CheckSwitchedOn()
    {
        Check.True(HeraLabyrinthProtocol.TryResolveEntry("Athens_072", 1, 500, out var athens) && athens == 30, "Athens 500 -> map 30");
        Check.True(HeraLabyrinthProtocol.TryResolveEntry("Sparta_072", 1, 501, out var sparta) && sparta == 31, "Sparta 501 -> map 31");
        Check.True(!HeraLabyrinthProtocol.TryResolveEntry("Athens_072", 1, 501, out _), "Athens does not take 501");
        Check.True(!HeraLabyrinthProtocol.TryResolveEntry("Sparta_072", 2, 501, out _), "wrong dialog index ignored");
        var athensMenu = HeraLabyrinthProtocol.WithEntryButton("Athens_072", 1, CapturedTransporterMenu);
        Check.True(athensMenu.SequenceEqual([200, 700, 500, 600, 202]), "Athens shows its captured 500 in 501's place");
        Check.True(HeraLabyrinthProtocol.WithEntryButton("Sparta_072", 1, CapturedTransporterMenu).SequenceEqual(CapturedTransporterMenu),
            "Sparta keeps the captured menu");

        IReadOnlyList<NpcSpawnDefinition> empty = [];
        var cyclades = HeraLabyrinthProtocol.AddNpcs(30, empty);
        var dodecanese = HeraLabyrinthProtocol.AddNpcs(31, empty);
        Check.Equal(6, cyclades.Count, "map 30 roster");
        Check.Equal(6, dodecanese.Count, "map 31 roster");
        Check.Equal(6, HeraLabyrinthProtocol.AddNpcs(30, cyclades).Count, "re-adding does not duplicate");
        Check.True(cyclades.All(static n => n.NpcKey.StartsWith("Labyrinth_", StringComparison.Ordinal) && n.MapId == 30),
            "map 30 carries the Labyrinth_00x actors (Quest.xml 1275)");
        Check.True(dodecanese.All(static n => n.NpcKey.StartsWith("Labyrint2_", StringComparison.Ordinal) && n.MapId == 31),
            "map 31 carries the Labyrint2_00x actors (Quest.xml 275)");
        var all = cyclades.Concat(dodecanese).ToList();
        Check.Equal(all.Count, all.Select(static n => n.ObjectId).Distinct().Count(), "object ids unique");
        Check.True(all.All(static n => n.ObjectId is >= 7851 and <= 7870), "object ids inside 7851-7870");
        var others = PansLabyrinthProtocol.Npcs()
            .Concat(EventDestinationsProtocol.TreasureLandNpcs()).Concat(EventDestinationsProtocol.SicilyNpcs())
            .Select(static n => n.ObjectId).ToHashSet();
        Check.True(all.All(n => !others.Contains(n.ObjectId)), "no id shared with Pan / Treasure Land / Sicily actors");
        var appearances = NpcTemplateSeeds.Appearances.Select(static a => a.TemplateKey).ToHashSet(StringComparer.Ordinal);
        Check.True(all.All(n => appearances.Contains(n.TemplateKey)), "every actor template is a client NPC.INI section");
        Check.True(HeraLabyrinthProtocol.IsZeusEnvoy(cyclades.Single(static n => n.NpcKey == "Labyrinth_006")), "Labyrinth_006 is Zeus' Envoy");
        Check.True(HeraLabyrinthProtocol.TryGetEnvoy(dodecanese.Single(static n => n.NpcKey == "Labyrint2_005"), out var five) && five == 5,
            "Labyrint2_005 is Envoy 5");
        Check.True(!HeraLabyrinthProtocol.TryGetEnvoy(cyclades.Single(static n => n.NpcKey == "Labyrinth_006"), out _), "006 is not an envoy");
        foreach (var quest in StarterQuestChain.Steps.Where(static s => s.QuestId is 275 or 1275))
        {
            var roster = quest.ResponderMapId == 30 ? cyclades : dodecanese;
            Check.True(roster.Any(n => n.NpcKey == quest.ResponderKey && n.MapId == quest.ResponderMapId),
                $"quest {quest.QuestId} responder {quest.ResponderKey} is placed on map {quest.ResponderMapId}");
        }

        // Portals: crossing one moves to its sign's isle; walking away from a landing never triggers.
        var portal = HeraLabyrinthLayout.Portals.First(static p => p.Isle == 9);
        Check.True(HeraLabyrinthProtocol.TryResolvePortal(31, portal.X - 4, portal.Z, portal.X + 4, portal.Z, 6f,
                out var from, out var to, out var lx, out var lz) &&
            from == 9 && to == portal.Destination && (lx, lz) == HeraLabyrinthLayout.Landings[portal.Destination],
            "a segment over an Isle 9 portal lands on its sign's isle");
        var exit = HeraLabyrinthLayout.ExitToIsleOne;
        Check.True(HeraLabyrinthProtocol.TryResolvePortal(30, exit.X, exit.Z - 3, exit.X, exit.Z + 1, 6f, out var f0, out var t0, out _, out _) &&
            f0 == 0 && t0 == 1, "the pocket exit leads to Isle 1");
        Check.True(!HeraLabyrinthProtocol.TryResolvePortal(32, portal.X - 4, portal.Z, portal.X + 4, portal.Z, 6f, out _, out _, out _, out _),
            "portals act on maps 30 / 31 only");
        var angles = Enumerable.Range(0, 8).Select(static i => i * Math.PI / 4).ToArray();
        for (var isle = 1; isle <= 9; isle++)
        {
            var (x, z) = HeraLabyrinthLayout.Landings[isle];
            Check.True(angles.All(a => !HeraLabyrinthProtocol.TryResolvePortal(30, x, z, x + 1.5f * (float)Math.Cos(a),
                z + 1.5f * (float)Math.Sin(a), 6f, out _, out _, out _, out _)), $"no bounce from the isle {isle} landing");
        }

        var (ax, az) = HeraLabyrinthProtocol.Arrival;
        Check.True(!HeraLabyrinthProtocol.TryResolvePortal(30, ax, az, ax + 1f, az, 6f, out _, out _, out _, out _), "no bounce at the arrival");
    }

    private static void CheckRewards()
    {
        Check.Equal(0, HeraLabyrinthProtocol.Band(50), "Lv50 band 0");
        Check.Equal(9, HeraLabyrinthProtocol.Band(149), "Lv149 band 9");
        Check.Equal(11, HeraLabyrinthProtocol.Band(150), "Lv150 band 11 (the client skips 10)");
        Check.Equal(12, HeraLabyrinthProtocol.Band(200), "Lv200 band 12");
        Check.True(Enumerable.Range(50, 151).All(static l => HeraLabyrinthProtocol.Band(l) != 10), "band 10 (the 'already' line) never paid");
        Check.Equal(2000, HeraLabyrinthProtocol.RewardResult(1, 0), "Envoy 1 band 0 = 2000");
        Check.Equal(2412, HeraLabyrinthProtocol.RewardResult(5, 12), "Envoy 5 band 12 = 2412");
        Check.Equal(1400, HeraLabyrinthProtocol.MissingOracleResult(5), "Envoy 5 missing oracle = 1400");
        Check.Equal(2310, HeraLabyrinthProtocol.AlreadyDoneResult(4), "Envoy 4 already today = 2310");
        Check.Equal((450000, 15), HeraLabyrinthProtocol.Reward(5, 200), "Envoy 5 top reward");
        Check.Equal((1500, 1), HeraLabyrinthProtocol.Reward(1, 50), "Envoy 1 bottom reward");
        Check.Equal(3818u, HeraLabyrinthProtocol.OracleItemId(5), "Oracle 5 = 3818");
        Check.True(Enumerable.Range(1, 5).Select(HeraLabyrinthProtocol.DailyCounter).Distinct().Count() == 5, "one daily counter per envoy");

        // Every reward equals the client's own NF_L0_2N0b line, when the client is on this machine.
        var lua = Path.Combine("/path/to/GodsWar");
        if (!File.Exists(lua))
        {
            return;
        }

        var text = Regex.Replace(File.ReadAllText(lua), @"\|c[0-9A-Fa-f]{8}", string.Empty);
        foreach (var envoy in Enumerable.Range(1, 5))
        {
            foreach (var band in Enumerable.Range(0, 13).Where(static b => b != 10))
            {
                var key = $"NF_L0_{HeraLabyrinthProtocol.RewardResult(envoy, band)}";
                var match = Regex.Match(text, $@"(?m)^{key}\s*=.*?([0-9]+) EXP.*?([0-9]+) (?:TP|Talent Points).*?50 (?:Zodiac|Stored) Energy");
                Check.True(match.Success, $"{key} is a client reward line");
                var level = band <= 9 ? 50 + band * 10 : band == 11 ? 150 : 160;
                Check.Equal((int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)),
                    HeraLabyrinthProtocol.Reward(envoy, level), $"{key} EXP / TP");
            }
        }
    }

    private static void CheckGateAndItems()
    {
        var gate = GateCatalog.Find(GateKeys.HeraLabyrinth);
        Check.True(gate is not null, "gate event.hera_labyrinth exists");
        Check.Equal((50, 200), (HeraLabyrinthProtocol.MinimumLevel, HeraLabyrinthProtocol.MaximumLevel), "Lv50, cap 200 (F-70)");
        Check.True(HeraLabyrinthProtocol.IsOpenAt(new DateTime(2026, 11, 9, 3, 0, 0)), "open all day by default");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.WindowVariable, "never");
        Check.True(!HeraLabyrinthProtocol.IsOpenAt(new DateTime(2026, 11, 9, 3, 0, 0)), "never closes it");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.WindowVariable, null);
        var items = ClientCatalogItemSeeds.All.Select(static i => i.Id).ToHashSet();
        Check.True(Enumerable.Range(3814, 5).All(items.Contains) && items.Contains((int)PanInvitationProtocol.PartThree),
            "Zeus' Oracles 3814-3818 and part 3 (3894) are client items");
        // Every grant is one item: oracles are Overlap 1 and no stack ever exceeds 99.
        Check.True(HeraLabyrinthProtocol.RewardZodiacEnergy == 50, "50 Zodiac Energy per step (client text)");
    }
    // Part 2: the capital War Supplier's deposit page (NpcFunMaterialBack.lua, function 114).
    private static void CheckWarSupplier()
    {
        for (var oracle = 1; oracle <= 5; oracle++)
        {
            Check.True(HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Sparta_076", 114, 1009 + oracle, out var o) && o == oracle,
                $"1{oracle + 9:000} deposits Oracle {oracle}");
        }

        Check.True(!HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Athens_076", 114, 1000, out _), "1000 stays with the dialogue");
        Check.True(!HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Athens_076", 114, 1015, out _), "1015 is not a button");
        Check.True(!HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Athens_076", 2, 1010, out _), "other functions ignored");
        Check.True(!HeraLabyrinthProtocol.TryResolveWarSupplierDeposit("Athens_072", 114, 1010, out _), "other NPCs ignored");
        Check.Equal(9, HeraLabyrinthProtocol.WarSupplierBand(200), "supplier pays band 9 at Lv150+ (no 2N11 / 2N12 on its page)");
        Check.Equal(3, HeraLabyrinthProtocol.WarSupplierBand(85), "supplier band below 150 equals the maze band");

        // Every number the supplier can send after a 1010-1014 click is drawn on the script's page 3 (Index 3).
        var script = "/path/to/GodsWar";
        if (!File.Exists(script))
        {
            return;
        }

        var source = File.ReadAllText(script);
        var page3Start = source.IndexOf("Index == 3", StringComparison.Ordinal);
        Check.True(page3Start > 0, "NpcFunMaterialBack.lua has a page 3");
        var page3 = Regex.Matches(source[page3Start..], @"SubID == (\d+)").Select(static m => int.Parse(m.Groups[1].Value)).ToHashSet();
        var sent = new HashSet<int> { HeraLabyrinthProtocol.WarSupplierEndResult };
        for (var oracle = 1; oracle <= 5; oracle++)
        {
            sent.Add(HeraLabyrinthProtocol.WarSupplierMissingResult(oracle));
            sent.Add(HeraLabyrinthProtocol.AlreadyDoneResult(oracle));
            foreach (var level in Enumerable.Range(50, 151))
            {
                sent.Add(HeraLabyrinthProtocol.RewardResult(oracle, HeraLabyrinthProtocol.WarSupplierBand(level)));
            }
        }

        Check.True(sent.All(page3.Contains), "every War Supplier reply is a page-3 SubID of NpcFunMaterialBack.lua");
    }

    // Part 2: the daily EveryDay.Dat line, edge-triggered on realm-local time.
    private static void CheckAnnouncement()
    {
        Check.True(HeraLabyrinthProtocol.AnnouncementText.Length <= Godswar.Server.Packets.PacketBuilder.CenteredAnnouncementMaximumTextLength &&
            HeraLabyrinthProtocol.AnnouncementText.All(static c => c is >= ' ' and <= '~'), "announcement fits the centred notice");
        var times = HeraLabyrinthProtocol.AnnouncementTimes();
        Check.True(times.SequenceEqual([new TimeOnly(19, 59)]), "default announcement 19:59 (GENERATED)");
        var day = new DateTime(2026, 11, 9);
        Check.True(!HeraLabyrinthProtocol.AnnouncementDue(times, null, day.AddHours(20)), "first tick never fires");
        Check.True(HeraLabyrinthProtocol.AnnouncementDue(times, day.AddHours(19).AddMinutes(58.8), day.AddHours(19).AddMinutes(59.1)),
            "fires when 19:59 is crossed");
        Check.True(!HeraLabyrinthProtocol.AnnouncementDue(times, day.AddHours(19).AddMinutes(59.1), day.AddHours(19).AddMinutes(59.4)),
            "fires once");
        Check.True(!HeraLabyrinthProtocol.AnnouncementDue(times, day.AddHours(17), day.AddHours(20)), "a gap over an hour is not replayed");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.AnnouncementVariable, "23:59, 0:00");
        times = HeraLabyrinthProtocol.AnnouncementTimes();
        Check.Equal(2, times.Count, "two configured times");
        Check.True(HeraLabyrinthProtocol.AnnouncementDue(times, day.AddHours(23).AddMinutes(59.5), day.AddDays(1).AddSeconds(10)),
            "midnight crossing fires");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.AnnouncementVariable, "off");
        Check.Equal(0, HeraLabyrinthProtocol.AnnouncementTimes().Count, "off silences it");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.AnnouncementVariable, null);
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.WindowVariable, "never");
        Check.Equal(0, HeraLabyrinthProtocol.AnnouncementTimes().Count, "window never silences it");
        Environment.SetEnvironmentVariable(HeraLabyrinthProtocol.WindowVariable, null);
    }
}
