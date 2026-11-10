using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// PETNET: Treasure Land net capture (switch, roster, packs, chances, boss respawn).
internal static class TreasureLandCaptureChecks
{
    public const string CheckName = "TREASURE LAND CAPTURE: nets give Travel Packs on map 25, gated by pet.treasure_land_capture";

    public static Task RunAsync()
    {
        try
        {
            CheckSwitch();
            CheckRoster();
            CheckPacksAndChances();
            CheckRespawn();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void Install(decimal value) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.TreasureLandCapture, value)],
            1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckSwitch()
    {
        ServerRates.ResetToDefaults();
        Check.True(!TreasureLandCapturePolicy.Enabled, "Treasure Land capture is off by default");
        Install(1);
        Check.True(TreasureLandCapturePolicy.Enabled, "pet.treasure_land_capture = 1 turns it on");
        Install(0);
        Check.True(!TreasureLandCapturePolicy.Enabled, "pet.treasure_land_capture = 0 turns it off");
    }

    private static void CheckRoster()
    {
        // Every client Agate template is covered, and nothing else.
        var clientKeys = MonsterTemplateSeeds.Monsters
            .Where(t => t.SourceMapId == TreasureLandCapturePolicy.MapId)
            .Select(t => t.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in TreasureLandCapturePolicy.TemplateKeys)
        {
            Check.True(clientKeys.Contains(key), $"Treasure Land template {key} is a published map-25 client template");
        }

        foreach (var key in clientKeys)
        {
            Check.True(TreasureLandCapturePolicy.IsTarget(TreasureLandCapturePolicy.MapId, key),
                $"map-25 client template {key} can be netted");
        }

        // The generated spawn rows: each resolves to the pack of its rank.
        var rows = EventDestinationSpawns.TreasureLand;
        Check.True(rows.Length > 0, "Treasure Land has generated spawn rows");
        foreach (var row in rows)
        {
            Check.True(TreasureLandCapturePolicy.TryGetRank(row.MapId, row.Template, out var rank),
                $"spawn {row.Name} ({row.Template}) is catchable");
            var expected = row.Rank switch
            {
                "elite" => TreasureLandCapturePolicy.Rank.Elite,
                "boss" => TreasureLandCapturePolicy.Rank.Boss,
                _ => TreasureLandCapturePolicy.Rank.Normal
            };
            Check.True(expected == rank, $"spawn {row.Name} rank matches the generator's rank");
        }

        foreach (var map in new[] { 0, 2, 11, 32, 42, 200, 205, 207 })
        {
            Check.True(!TreasureLandCapturePolicy.IsTarget(map, "A_normalsx_ark_002") &&
                !TreasureLandCapturePolicy.IsTarget(map, "A_bossx_dragon_014"),
                $"map {map} is not Treasure Land");
        }

        Check.True(!TreasureLandCapturePolicy.IsTarget(25, "A_normal_female_001") &&
            !TreasureLandCapturePolicy.IsTarget(25, null), "unknown templates on map 25 are not catchable");
    }

    private static void CheckPacksAndChances()
    {
        Check.Equal(3958u, TreasureLandCapturePolicy.PackFor(TreasureLandCapturePolicy.Rank.Normal), "normal -> Simple Travel Pack");
        Check.Equal(3959u, TreasureLandCapturePolicy.PackFor(TreasureLandCapturePolicy.Rank.Elite), "elite -> Greater Travel Pack");
        Check.Equal(3960u, TreasureLandCapturePolicy.PackFor(TreasureLandCapturePolicy.Rank.Boss), "boss -> Superior Travel Pack");
        Check.True(TreasureLandCapturePolicy.PackQuantity is > 0 and <= 99, "one catch gives at most 99 packs");
        foreach (var rank in Enum.GetValues<TreasureLandCapturePolicy.Rank>())
        {
            var previous = -1.0;
            for (var net = 10080u; net <= 10084u; net++)
            {
                var chance = TreasureLandCapturePolicy.CaptureChance(net, rank);
                Check.True(chance is > 0 and <= 1 && chance >= previous, $"{rank} net {net} chance {chance} is in (0,1] and rises");
                previous = chance;
            }
        }

        Check.True(TreasureLandCapturePolicy.CaptureChance(10080, TreasureLandCapturePolicy.Rank.Boss) <
            TreasureLandCapturePolicy.CaptureChance(10080, TreasureLandCapturePolicy.Rank.Elite) &&
            TreasureLandCapturePolicy.CaptureChance(10080, TreasureLandCapturePolicy.Rank.Elite) <
            TreasureLandCapturePolicy.CaptureChance(10080, TreasureLandCapturePolicy.Rank.Normal),
            "bosses are harder than elites, elites harder than normals");
        Check.True(TreasureLandCapturePolicy.CaptureChance(10085, TreasureLandCapturePolicy.Rank.Normal) == 0 &&
            TreasureLandCapturePolicy.CaptureChance(4210, TreasureLandCapturePolicy.Rank.Normal) == 0,
            "only the five tools catch");
        Check.True(TreasureLandCapturePolicy.LevelAllows(60, 70) && !TreasureLandCapturePolicy.LevelAllows(60, 71) &&
            TreasureLandCapturePolicy.LevelAllows(200, 135), "a net holds monsters up to 10 levels above the player");
    }

    private static void CheckRespawn()
    {
        Install(0);
        Check.True(!TreasureLandCapturePolicy.TryGetRespawnInterval(25, "A_bossx_dragon_014", out _),
            "switch off: the boss keeps its ordinary respawn");
        Install(1);
        Check.True(TreasureLandCapturePolicy.TryGetRespawnInterval(25, "A_bossx_dragon_014", out var boss) &&
            boss == TimeSpan.FromMinutes(6), "switch on: the Icefield Dragon returns after 6 minutes");
        Check.True(!TreasureLandCapturePolicy.TryGetRespawnInterval(25, "A_normalsx_ark_002", out _),
            "normal monsters keep their own respawn");
    }
}
