using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;
using R = Godswar.Server.Domain.World.Content.EventDestinationSpawns.Row;

namespace Godswar.Server.ProtocolChecks;

// PAN CHAIN slice 2: the GENERATED Sicily / Treasure Land monster rows (server/tools/pans-labyrinth/gen_destination_spawns.py).
internal static class PanChainSpawnChecks
{
    public const string CheckName =
        "PAN CHAIN slice 2: Sicily / Treasure Land spawns cover every Purify Sicily quest; Cursed Zone part 2 is gated; " +
        "F-159 306/1306 count only the Lv121+ Gela Hunters";

    // Client Quest.xml MinLevel / MaxLevel of the Purify Sicily quests (306/1306: 121-200).
    private static readonly (uint Quest, int Min, int Max)[] SicilyQuests =
    [
        (292, 25, 44), (293, 45, 64), (294, 65, 80), (295, 81, 90), (296, 91, 100), (297, 101, 110), (298, 111, 120), (306, 121, 200),
        (1292, 25, 44), (1293, 45, 64), (1294, 65, 80), (1295, 81, 90), (1296, 91, 100), (1297, 101, 110), (1298, 111, 120), (1306, 121, 200),
    ];

    public static Task RunAsync()
    {
        CheckRows(EventDestinationSpawns.Sicily.Where(static row => row.ObjectId < SicilyHighGroup.FirstObjectId).ToArray(),
            EventDestinationsProtocol.SicilyMapId, 47001, 47199);
        CheckRows(EventDestinationSpawns.Sicily.Where(static row => row.ObjectId >= SicilyHighGroup.FirstObjectId).ToArray(),
            EventDestinationsProtocol.SicilyMapId, SicilyHighGroup.FirstObjectId, SicilyHighGroup.LastObjectId);
        Check.Equal(EventDestinationSpawns.Sicily.Length,
            EventDestinationSpawns.Sicily.Select(static row => row.ObjectId).Distinct().Count(), "all Sicily object ids unique");
        Check.True(EventDestinationSpawns.TreasureLand.All(static row =>
                row.ObjectId < SicilyHighGroup.FirstObjectId || row.ObjectId > SicilyHighGroup.LastObjectId),
            "Treasure Land stays out of the F-159 id range");
        CheckHighGroup();
        CheckRows(EventDestinationSpawns.TreasureLand, EventDestinationsProtocol.TreasureLandMapId, 47201, 47399);
        CheckQuestCoverage();
        Check.Equal(3, EventDestinationSpawns.TreasureLand.Count(static row => row.Rank == "boss"), "three Treasure Land dragons");
        Check.True(EventDestinationSpawns.TreasureLand.Any(static row => row is { Template: "A_bossx_dragon_014", Level: 130 }),
            "Icefield Dragon is the level 130 boss (wiki)");
        Check.True(!EventDestinationSpawns.Sicily.Any(static row => row.Template.StartsWith("C_", StringComparison.Ordinal)),
            "Sicily leaves the Athenian/Spartan soldiers out");
        CheckCursedZoneEnvoy();
        CheckTreasureLandAdmission();
        return Task.CompletedTask;
    }

    // F-159: the GENERATED Lv121+ Gela Hunters and the 306/1306 level floor.
    private static void CheckHighGroup()
    {
        var map = (uint)EventDestinationsProtocol.SicilyMapId;
        var high = EventDestinationSpawns.Sicily.Where(static row => row.ObjectId >= SicilyHighGroup.FirstObjectId).ToArray();
        var low = EventDestinationSpawns.Sicily.Where(static row =>
            row.ObjectId < SicilyHighGroup.FirstObjectId && row.Template == "A_normal_godsguard_010").ToArray();
        Check.Equal(24, high.Length, "F-159: 12 Lv121+ Gela Hunters per camp");
        Check.Equal(24, low.Length, "F-159: the Lv112-120 Gela Hunters are unchanged (12 per camp)");
        Check.True(high.All(static row => row is { Template: "A_normal_godsguard_010", DisplayName: "Gela Hunter", Rank: "normal" } &&
                row.Level is >= 122 and <= 200 && row.Name.Contains("-hunter-high-", StringComparison.Ordinal)),
            "F-159: stronger copies of the Gela Hunter template at Lv122-200");
        // F-163 (the admin: "Spread across Lv122-200"): each camp's 12 are spread evenly, round-half-up(122 + i * 78 / 11).
        const string spread = "122,129,136,143,150,157,165,172,179,186,193,200";
        foreach (var west in new[] { true, false })
        {
            var levels = high.Where(row => row.X < 0 == west).OrderBy(static row => row.ObjectId).Select(static row => row.Level);
            Check.Equal(spread, string.Join(',', levels), $"F-163: {(west ? "Sparta" : "Athens")}'s 12 spread evenly over Lv122-200");
        }
        Check.True(low.All(static row => row.Level is >= 112 and <= 120), "F-159: the 298/1298 group stays at Lv112-120");
        Check.True(high.All(row => row.Health > low.Max(static l => l.Health)), "F-159: every high copy has more HP than any low one");
        Check.Equal(12, high.Count(static row => row.X < 0), "F-159: 12 on Sparta's (west) half");
        Check.True(high.All(row => low.All(l => MathF.Sqrt(((row.X - l.X) * (row.X - l.X)) + ((row.Z - l.Z) * (row.Z - l.Z))) >= 2.5f)),
            "F-159: the high group does not stand on the low one");

        foreach (var (quest, plain) in new (uint, uint)[] { (306, 298), (1306, 1298) })
        {
            var objective = StarterQuestObjectives.For(quest).Single();
            Check.Equal(5167u, objective.MonsterId, $"{quest}: the client quest window still counts QuestMonster 5167");
            foreach (var row in low)
            {
                var named = StarterQuestObjectives.Matches(objective, map, row.DisplayName, row.X, row.Z);
                Check.True(named && !SicilyHighGroup.Admits(quest, map, (uint)row.Level, true),
                    $"{quest}: Lv{row.Level} {row.Name} does NOT count with GODSWAR_SICILY=1");
                Check.True(SicilyHighGroup.Admits(quest, map, (uint)row.Level, false),
                    $"{quest}: with the switch off the old behaviour is kept");
                Check.True(StarterQuestObjectives.Matches(StarterQuestObjectives.For(plain).Single(), map, row.DisplayName, row.X, row.Z) &&
                    SicilyHighGroup.Admits(plain, map, (uint)row.Level, true), $"{plain}: Lv{row.Level} {row.Name} still counts");
            }
            Check.True(high.All(row => StarterQuestObjectives.Matches(objective, map, row.DisplayName, row.X, row.Z) &&
                SicilyHighGroup.Admits(quest, map, (uint)row.Level, true)), $"{quest}: every Lv121+ Gela Hunter counts");
        }

        Check.True(SicilyHighGroup.Admits(306, map, 121, true) && !SicilyHighGroup.Admits(306, map, 120, true),
            "F-159: the floor is Lv121");
        Check.True(SicilyHighGroup.Admits(306, 5, 50, true), "F-159: the floor is map 32 only");
        var saved = Environment.GetEnvironmentVariable(EventDestinationsProtocol.SicilyVariable);
        try
        {
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.SicilyVariable, null);
            Check.True(SicilyHighGroup.Admits(1306, map, 112), "F-159: GODSWAR_SICILY unset leaves 306/1306 as before");
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.SicilyVariable, "1");
            Check.True(!SicilyHighGroup.Admits(1306, map, 112) && SicilyHighGroup.Admits(1306, map, 125),
                "F-159: GODSWAR_SICILY=1 applies the floor (read at kill time)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.SicilyVariable, saved);
        }
    }

    private static void CheckTreasureLandAdmission()
    {
        var saved = Environment.GetEnvironmentVariable(EventDestinationsProtocol.TreasureLandSpawnsVariable);
        try
        {
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.TreasureLandSpawnsVariable, null);
            Check.True(!EventDestinationsProtocol.AdmitsTreasureLandAuthoredSpawn(25), "Treasure Land spawns off by default");
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.TreasureLandSpawnsVariable, "1");
            Check.True(EventDestinationsProtocol.AdmitsTreasureLandAuthoredSpawn(25), "Treasure Land spawns admitted when on");
            Check.True(!EventDestinationsProtocol.AdmitsTreasureLandAuthoredSpawn(32) &&
                !EventDestinationsProtocol.AdmitsTreasureLandAuthoredSpawn(2), "the admission is map 25 only");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.TreasureLandSpawnsVariable, saved);
        }
    }

    // Part 2 (NF_L0_MSER521): own switch, default off; the rule; the page and reply numbers the client script draws.
    private static void CheckCursedZoneEnvoy()
    {
        var saved = Environment.GetEnvironmentVariable(PanInvitationProtocol.CursedEnvoyVariable);
        try
        {
            Environment.SetEnvironmentVariable(PanInvitationProtocol.CursedEnvoyVariable, null);
            Check.True(!PanInvitationProtocol.CursedEnvoyEnabled, "part-2 envoy off by default");
            Environment.SetEnvironmentVariable(PanInvitationProtocol.CursedEnvoyVariable, "1");
            Check.True(PanInvitationProtocol.CursedEnvoyEnabled, "part-2 envoy switch reads 1");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PanInvitationProtocol.CursedEnvoyVariable, saved);
        }

        Check.True(PanInvitationProtocol.CursedZoneOpening.SequenceEqual([500]), "page 1 is [500] (MSER521 + one item slot)");
        Check.True(PanInvitationProtocol.IsCursedZoneEnvoy("Execrativelys3_012"), "the placed map-29 envoy is a part-2 envoy");
        Check.True(!PanInvitationProtocol.IsCursedZoneEnvoy("Execrativelys3_014") &&
            !PanInvitationProtocol.IsCursedZoneEnvoy("Agate_009"), "other NPCs are not part-2 envoys");
        Check.True(!PanInvitationProtocol.QualifiesForPartTwo(1, 0), "Common, no options: refused");
        Check.True(!PanInvitationProtocol.QualifiesForPartTwo(2, 1), "Enhanced, one option: refused");
        Check.True(PanInvitationProtocol.QualifiesForPartTwo(3, 0), "Delicate: accepted");
        Check.True(PanInvitationProtocol.QualifiesForPartTwo(1, 2), "two options: accepted");
        Check.Equal((53, 253, 103), (PanInvitationProtocol.PartTwoGiven, PanInvitationProtocol.WrongEquipment,
            PanInvitationProtocol.PartTwoTwiceAWeek), "part-2 replies are the client's page-2 SubIDs");
    }

    private static void CheckRows(R[] rows, int mapId, uint firstId, uint lastId)
    {
        Check.True(rows.Length > 0, $"map {mapId} has generated rows");
        var templates = MonsterTemplateSeeds.Monsters
            .Where(seed => seed.SourceMapId == mapId)
            .ToDictionary(static seed => seed.TemplateKey, StringComparer.Ordinal);
        var npcIds = EventDestinationsProtocol.TreasureLandNpcs().Concat(EventDestinationsProtocol.SicilyNpcs())
            .Select(static npc => npc.ObjectId).ToHashSet();
        Check.Equal(rows.Length, rows.Select(static row => row.ObjectId).Distinct().Count(), $"map {mapId} object ids unique");
        Check.Equal(rows.Length, rows.Select(static row => row.Name).Distinct(StringComparer.Ordinal).Count(), $"map {mapId} names unique");
        foreach (var row in rows)
        {
            Check.Equal((short)mapId, row.MapId, $"{row.Name} map");
            Check.True(templates.TryGetValue(row.Template, out var seed), $"{row.Name} template {row.Template} is a map {mapId} template");
            Check.Equal(seed.Rank, row.Rank, $"{row.Name} rank matches the catalog");
            Check.Equal(seed.DisplayName, row.DisplayName, $"{row.Name} display name matches the catalog");
            // Sicily's templates are ordinary monsters; every Treasure Land template is a catchable "[Pet]" model,
            // admitted only through GODSWAR_TREASURE_LAND_SPAWNS.
            Check.Equal(mapId == EventDestinationsProtocol.TreasureLandMapId, seed.IsPet, $"{row.Name} pet flag as expected");
            Check.True(row.ObjectId >= firstId && row.ObjectId <= lastId, $"{row.Name} object id in {firstId}-{lastId}");
            Check.True(WorldObjectIds.IsMonster(row.ObjectId) && !WorldObjectIds.IsReservedForPlayer(row.ObjectId),
                $"{row.Name} object id is in the monster registry");
            Check.True(row.ObjectId is < 48000 or > 48999, $"{row.Name} stays out of the runtime-spawn pool");
            Check.True(!npcIds.Contains(row.ObjectId), $"{row.Name} does not collide with a slice-1 NPC");
            Check.True(row.Level is >= 1 and <= 200 && row.Health > 0, $"{row.Name} level/HP sane");
            Check.True(row.X is > -256f and < 256f && row.Z is > -256f and < 256f, $"{row.Name} inside the map grid");
        }
    }

    private static void CheckQuestCoverage()
    {
        foreach (var (quest, min, max) in SicilyQuests)
        {
            var objectives = StarterQuestObjectives.For(quest);
            Check.True(objectives.Count > 0, $"quest {quest} has a kill objective");
            foreach (var objective in objectives)
            {
                // F-159: what the kill path credits with GODSWAR_SICILY=1 (name match, then the 306/1306 level floor).
                // The Lv121+ group also matches 298/1298 by name; the band check below is about each quest's own group.
                var high = SicilyHighGroup.AppliesTo(quest);
                var targets = EventDestinationSpawns.Sicily.Where(row => StarterQuestObjectives.Matches(
                        objective, (uint)EventDestinationsProtocol.SicilyMapId, row.DisplayName, row.X, row.Z) &&
                        SicilyHighGroup.Admits(quest, EventDestinationsProtocol.SicilyMapId, (uint)row.Level, true) &&
                        (high || row.ObjectId < SicilyHighGroup.FirstObjectId))
                    .ToArray();
                Check.True(targets.Length >= 12, $"quest {quest}: at least 12 '{objective.Target}' on map 32 (found {targets.Length})");
                Check.True(targets.All(row => row.Level >= min - 10 && row.Level <= max),
                    $"quest {quest}: '{objective.Target}' levels near the quest band {min}-{max}");
                // The group sits where the client's quest window points (Quest.xml CreatureMapPos); the F-159 group is
                // 20 units beside that point, next to the 298/1298 group that stands on it.
                var reach = high ? 30f : 20f;
                var near = targets.Count(row => MathF.Abs(row.X - objective.X) <= reach && MathF.Abs(row.Z - objective.Z) <= reach);
                Check.True(near >= 12, $"quest {quest}: 12 targets within {reach} units of ({objective.X}, {objective.Z})");
            }
        }
    }
}
