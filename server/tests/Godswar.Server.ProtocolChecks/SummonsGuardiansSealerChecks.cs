using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ROUND3 summons (2026-11-09): the ItemRefreshMonster table, treasure-dig rows, Totem Pole rows, the group /
/// owner-limit / camp extensions of the runtime spawn overlay and the Sacred Sealer's prices.
/// </summary>
internal static class SummonsGuardiansSealerChecks
{
    public const string CheckName = "ROUND3 summons: treasure guardians, Totem Poles, Sacred Sealer";

    private const byte MapId = 15; // Derveni

    public static Task RunAsync()
    {
        try
        {
            CheckTable();
            CheckDigRows();
            CheckTotems();
            CheckPackets();
            foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
            {
                CheckGroups(mode);
            }
            CheckSealer();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
        return Task.CompletedTask;
    }

    private static IReadOnlyList<GameplayMonsterTemplateDefinition> SeedTemplates() =>
        MonsterTemplateSeeds.Monsters.Select(seed => new GameplayMonsterTemplateDefinition(seed.SourceKey,
            seed.SourceKind, seed.SourceMapId, seed.SceneKey, seed.TemplateKey, seed.DisplayName, seed.Rank,
            seed.IsBoss, seed.IsElite, seed.IsPet, seed.AttackType, seed.CollisionRange)).ToArray();

    private static void CheckTable()
    {
        Check.Equal(97, ItemRefreshMonsters.Rows.Count, "IRM rows 30000-35505 (4 totem, 57 beside-the-player, 36 boss sets)");
        Check.True(ItemRefreshMonsters.TryGet(30000, out var soldiers) && soldiers.MonsterCount == 5 &&
                   soldiers.Points.Single().Templates.All(key => key == "A_eliteQ_greecewarrior_003"),
            "IRM 30000 = 5 Summoned Athenian Soldiers");
        Check.True(ItemRefreshMonsters.TryGet(31001, out var mages) && mages.MonsterCount == 3 &&
                   mages.Points.Single().Templates.All(key => key == "A_eliteQ_mage_004"),
            "IRM 31001 = 3 Summoned Spartan Mages");
        Check.True(ItemRefreshMonsters.TryGet(35505, out var feltree) && feltree.MonsterCount == 9,
            "IRM 35505: four boss points plus a Feltree with 4 underlings = 9 monsters");
        Check.True(ItemRefreshMonsters.Rows.Where(row => row.Id >= 33000).All(row => row.Points.Length == 5 &&
                row.Points.All(point => !point.AtPlayer && point.Region is 100 or 101 or 102 or 103 or 104 or 152)),
            "every boss-set row has 5 fixed points on a high-level region");
        Check.True(ItemRefreshMonsters.Rows.Where(row => row.Id < 33000).All(row =>
                row.Points.Length == 1 && row.Points[0].AtPlayer && row.Points[0].Region == -1),
            "totem and 32xxx rows spawn beside the player");

        var regions = new Dictionary<int, short> { [0] = 1, [1] = 2, [2] = 11, [3] = 3, [4] = 12, [50] = 0, [51] = 4,
            [52] = 13, [53] = 5, [54] = 14, [100] = 15, [101] = 16, [102] = 17, [103] = 18, [104] = 19, [152] = 8 };
        foreach (var (region, map) in regions)
        {
            Check.Equal(map, ItemRefreshMonsters.ServerMapForRegion(region) ?? (short)-1,
                $"client region {region} is server map {map} (MapIdToNameConfig.ini)");
            Check.Equal(TreasureMapPlaces.ServerMapIdForRegion((short)region),
                ItemRefreshMonsters.ServerMapForRegion(region) ?? (short)-1,
                $"guardians and the dig check (#87) agree on region {region}");
        }

        // Every template a row names is a published global template, except the three the client lacks.
        var globals = MonsterTemplateSeeds.Monsters.Where(seed => seed.SourceMapId is null)
            .Select(seed => seed.TemplateKey).ToHashSet(StringComparer.Ordinal);
        var missing = ItemRefreshMonsters.Rows.SelectMany(row => row.Points).SelectMany(point => point.Templates)
            .Where(key => !globals.Contains(key)).ToHashSet(StringComparer.Ordinal);
        Check.True(missing.SetEquals(["A_eliteQ_CentaurWarrior_001", "A_eliteQ_Cyclops_001", "A_eliteQ_PersianGen_001"]),
            "only the three templates no client Monster.ini lists are missing");
    }

    private static void CheckDigRows()
    {
        var templates = SeedTemplates();
        bool Drawable(ItemRefreshRow row, short map) => ItemRefreshMonsters.IsDrawable(row, templates, map);
        var random = new Random(7);
        Check.Equal(33000, ItemRefreshMonsters.DigRow(12000, row => Drawable(row, 15), random)?.Id ?? 0,
            "Place 12000 (Derveni, first) uses IRM 33000");
        Check.Equal(34505, ItemRefreshMonsters.DigRow(12305, row => Drawable(row, 18), random)?.Id ?? 0,
            "Place 12305 (Megara, sixth) uses IRM 34505");
        Check.Equal(35505, ItemRefreshMonsters.DigRow(12505, row => Drawable(row, 8), random)?.Id ?? 0,
            "Place 12505 (Thermopylae, sixth) uses IRM 35505");
        for (var i = 0; i < 200; i++)
        {
            var row = ItemRefreshMonsters.DigRow(10102, candidate => Drawable(candidate, 2), random);
            Check.True(row is { Id: >= 32000 and <= 32056 } && row.Id is not (32030 or 32031 or 32032),
                "a starting-map dig uses one drawable 32xxx row (never one with a missing template)");
        }

        Check.True(ItemRefreshMonsters.TryGet(33000, out var derveni), "IRM 33000 exists");
        var spawns = ItemRefreshMonsters.Expand(derveni, templates, 15, 0, 0, random);
        Check.True(spawns is { Count: 5 } && spawns.All(spawn => spawn.Template.IsBoss),
            "a Derveni boss set expands to its 5 bosses on Derveni (server map 15)");
        Check.True(ItemRefreshMonsters.Expand(derveni, templates, 16, 0, 0, random) is null,
            "a boss set is never placed on another map");
        Check.True(ItemRefreshMonsters.TryGet(35505, out var feltree) &&
                   ItemRefreshMonsters.Expand(feltree, templates, 8, 0, 0, random) is { Count: 9 } nine &&
                   nine.Where(spawn => spawn.Template.TemplateKey == "A_eliteQ_elves_001").All(spawn =>
                       Math.Sqrt((spawn.X - 99) * (spawn.X - 99) + (spawn.Z - 168) * (spawn.Z - 168)) <= 6.01),
            "IRM 35505 expands to 9 monsters on Thermopylae; the underlings stand within Range 6 of their point");
        Check.True(ItemRefreshMonsters.TryGet(32046, out var five) &&
                   ItemRefreshMonsters.Expand(five, templates, 2, 100, 100, random) is { Count: 5 } ring &&
                   ring.All(spawn => Math.Abs(Math.Sqrt((spawn.X - 100) * (spawn.X - 100) + (spawn.Z - 100) * (spawn.Z - 100)) -
                       ItemRefreshMonsters.BesidePlayerRadius) < 0.01),
            "a 5-monster beside-the-player row stands on a ring around the player");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.TreasureGuardians), "item.treasure_guardians defaults to on");
    }

    private static void CheckTotems()
    {
        var templates = SeedTemplates();
        Check.True(ItemRefreshMonsters.TryGetTotem(4473, out var row, out var camp) && row == 30000 && camp == 1,
            "4473 Ath Sldr = IRM 30000, Athens");
        Check.True(ItemRefreshMonsters.TryGetTotem(4474, out row, out camp) && row == 30001 && camp == 1,
            "4474 Ath Mages = IRM 30001, Athens");
        Check.True(ItemRefreshMonsters.TryGetTotem(4475, out row, out camp) && row == 31000 && camp == 0,
            "4475 Spt Sldr = IRM 31000, Sparta");
        Check.True(ItemRefreshMonsters.TryGetTotem(4476, out row, out camp) && row == 31001 && camp == 0,
            "4476 Spt Mages = IRM 31001, Sparta");
        Check.True(!ItemRefreshMonsters.TryGetTotem(4472, out _, out _), "the Summoning Vessel summons nothing");
        Check.Equal(TimeSpan.FromSeconds(60), ItemRefreshMonsters.TotemLifetime, "item text: 60 seconds");
        var soldier = templates.First(template => template.SourceMapId is null &&
            template.TemplateKey == "A_eliteQ_greecewarrior_003");
        Check.True(ItemRefreshMonsters.Stats(soldier, 10, (int)MonsterAggroPolicy.MinimumAggressiveTier).Level >=
                   MonsterAggroPolicy.MinimumAggressiveTier, "a low-level user's summons are still aggressive");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.TotemPoles), "item.totem_poles defaults to on");
    }

    private static void CheckPackets()
    {
        var fresh = WorldRuntimeSpawnPackets.Build(null, MapId, 48_010, "A_eliteQ_mage_001", 40, 5_000, 1, 2, 3, 1, 1);
        Check.Equal((byte)1, fresh[5], "an allied spawn's camp is in byte 5 (Atlantis layout)");
        var published = WorldRuntimeSpawnPackets.Build(null, MapId, 9_000, "A_eliteQ_mage_001", 40, 5_000, 1, 2, 3, 1);
        published[5] = 2;
        var reused = WorldRuntimeSpawnPackets.Build(published, MapId, 48_011, "A_eliteQ_mage_001", 40, 5_000, 1, 2, 3,
            1, 0);
        Check.Equal((byte)0, reused[5], "the camp overwrites a reused published packet's byte 5");
        var neutral = WorldRuntimeSpawnPackets.Build(published, MapId, 48_012, "A_eliteQ_mage_001", 40, 5_000, 1, 2, 3, 1);
        Check.Equal((byte)2, neutral[5], "without a camp the packet keeps its own byte 5");
        Check.Throws<ArgumentOutOfRangeException>(() =>
            WorldRuntimeSpawnPackets.Build(null, MapId, 48_013, "A_eliteQ_mage_001", 40, 5_000, 1, 2, 3, 1, 2),
            "an allied camp is 0 or 1");
    }

    private static IMonsterMapRuntime Single(MonsterRuntimeMode mode, uint objectId, DateTimeOffset now, byte? camp = null)
    {
        var packet = WorldRuntimeSpawnPackets.Build(null, MapId, objectId, "A_eliteQ_mage_001", 40, 5_000, objectId % 100,
            5f, 0, 1f, camp);
        var spawn = new Domain.World.Content.CapturedMonsterSpawn(MapId, "", "A_eliteQ_mage_001", "Mage", objectId,
            objectId % 100, 0, packet);
        spawn.Validate(MapId);
        return MonsterMapRuntimeFactory.Create(mode, MapId, [spawn], now, respawnPolicy: MonsterRespawnPolicy.Never);
    }

    private static void CheckGroups(MonsterRuntimeMode mode)
    {
        var label = $"[{mode}] ";
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var basePacket = WorldRuntimeSpawnPackets.Build(null, MapId, 9_000, "A_eliteQ_mage_001", 40, 5_000, 0, 5f, 0, 1f);
        var baseRuntime = MonsterMapRuntimeFactory.Create(mode, MapId,
            [new Domain.World.Content.CapturedMonsterSpawn(MapId, "", "A_eliteQ_mage_001", "Mage", 9_000, 0, 0, basePacket)],
            now, respawnPolicy: MonsterRespawnPolicy.Timed);
        var camps = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 0 };
        var overlay = new WorldRuntimeSpawnOverlay(baseRuntime, id => camps.TryGetValue(id, out var camp) ? camp : null);

        // Default limit 1 is unchanged; another source family does not count against it.
        var sealSpec = new WorldRuntimeSpawnSpec(1, true, TimeSpan.FromMinutes(10), [], "item:14035");
        Check.True(overlay.TryAttach(Single(mode, 48_000, now), sealSpec, now, out _), label + "a seal-box spawn attaches");
        Check.True(!overlay.TryAttach(Single(mode, 48_001, now), sealSpec, now, out var refusal) &&
                   refusal == "owner limit reached", label + "a second one of the same family is refused (limit 1)");

        // A 9-monster treasure group attaches all at once beside the live seal-box monster.
        var treasure = new WorldRuntimeSpawnSpec(1, true, TimeSpan.FromMinutes(30), [], "treasure:35505",
            ItemRefreshMonsters.TreasureOwnerLimit);
        var group = Enumerable.Range(0, 9)
            .Select(i => (Single(mode, (uint)(48_100 + i), now), treasure)).ToArray();
        Check.True(overlay.TryAttachGroup(group, now, out _) && overlay.SpawnCount == 10,
            label + "a 9-monster treasure group attaches beside a live seal-box spawn");
        var again = Enumerable.Range(0, 2).Select(i => (Single(mode, (uint)(48_200 + i), now), treasure)).ToArray();
        Check.True(!overlay.TryAttachGroup(again, now, out refusal) && refusal == "owner limit reached" &&
                   overlay.SpawnCount == 10, label + "a group over the owner limit is refused whole");

        // Map limit: all or nothing at the edge (10 live, 20 allowed).
        var strangerSpec = new WorldRuntimeSpawnSpec(3, false, TimeSpan.FromSeconds(60), [], "totem:4475", 20, 0);
        var eleven = Enumerable.Range(0, 11).Select(i => (Single(mode, (uint)(48_300 + i), now, 0), strangerSpec)).ToArray();
        Check.True(!overlay.TryAttachGroup(eleven, now, out refusal) && refusal == "map limit reached" &&
                   overlay.SpawnCount == 10, label + "a group past the map limit leaves nothing behind");
        var duplicate = new[] { (Single(mode, 48_400, now, 0), strangerSpec), (Single(mode, 48_400, now, 0), strangerSpec) };
        Check.True(!overlay.TryAttachGroup(duplicate, now, out _) && overlay.SpawnCount == 10,
            label + "a group with a repeated id is refused whole");

        // Allied (camp) spawns: their owner and their faction cannot hurt them; the other faction can.
        var allySpec = new WorldRuntimeSpawnSpec(2, false, TimeSpan.FromSeconds(60), [], "totem:4474",
            ItemRefreshMonsters.TotemOwnerLimit, 1);
        var allies = Enumerable.Range(0, 3).Select(i => (Single(mode, (uint)(48_500 + i), now, 1), allySpec)).ToArray();
        Check.True(overlay.TryAttachGroup(allies, now, out _), label + "3 allied mages attach");
        Check.True(!overlay.TryApplyDamage(48_500, 1, 2, null, now.AddSeconds(1), out _),
            label + "the owner cannot hurt their own summon");
        Check.True(!overlay.TryApplyDamage(48_500, 1, 1, null, now.AddSeconds(1), out _),
            label + "the summon's faction cannot hurt it");
        Check.True(overlay.TryApplyDamage(48_500, 1, 3, null, now.AddSeconds(1), out _),
            label + "an enemy-faction player can");
        Check.True(overlay.TryGetSnapshot(48_501, out var ally) && ally.Definition.FactionCamp == 1,
            label + "the allied spawn's FactionCamp is Athens (aggro filter skips Athenians)");
        overlay.Advance(now.AddSeconds(61), []);
        Check.True(!overlay.TryGetSnapshot(48_501, out _), label + "the allies leave after 60 seconds");
    }

    private static void CheckSealer()
    {
        Check.True(SacredSealerPolicy.TryGetOffer("Athens_130", 102, out var ladon) &&
                   ladon == new SacredSealerOffer(14060, 180, 203), "Athens: Ladon Scroll 14060 for 180 Gold (Legendary4/9)");
        Check.True(SacredSealerPolicy.TryGetOffer("Athens_130", 103, out var cyclops) &&
                   cyclops == new SacredSealerOffer(14061, 900, 204), "Athens: Cyclops Scroll 14061 for 900 Gold");
        Check.True(SacredSealerPolicy.TryGetOffer("Sparta_130", 104, out var medusa) &&
                   medusa == new SacredSealerOffer(14065, 1_800, 205), "Sparta: Medusa Scroll 14065 for 1,800 Gold");
        Check.True(!SacredSealerPolicy.TryGetOffer("Athens_129", 102, out _) &&
                   !SacredSealerPolicy.TryGetOffer("Athens_130", 105, out _), "only the Sealer's three buttons sell");
        Check.Equal(201, SacredSealerPolicy.BagFull, "Legendary7: make more space");
        Check.Equal(202, SacredSealerPolicy.NotEnoughGold, "Legendary8: not enough Gold");
        Check.True(SacredSealerPolicy.ChestFor(14060) == 14066 && SacredSealerPolicy.ChestFor(14064) == 14067 &&
                   SacredSealerPolicy.ChestFor(14062) == 14068 && SacredSealerPolicy.ChestFor(14034) == 0,
            "Ladon -> Copper (14066), Cyclops -> Silver (14067), Medusa -> Gold (14068) Treasure Chest");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SacredSealer), "npc.sacred_sealer defaults to on");
    }
}
