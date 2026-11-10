using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterCombatBalanceChecks
{
    public const string CheckName =
        "Database-backed boss critical resistance, pinned catalogs and exact identity isolation";

    public static Task RunAsync()
    {
        CheckConfiguredValuesAndIsolation();
        CheckPinnedSnapshot();
        CheckCoordinationRevision();
        CheckPinnedWorldReader();
        CheckAuthoredOverrides();
        CheckRejectedConfiguration();
        return Task.CompletedTask;
    }

    private static void CheckConfiguredValuesAndIsolation()
    {
        MonsterCombatBalanceDefinition[] rows =
        [new(200, "boss", 100), new(204, "boss", 317), new(205, "boss", 0),
            new(207, "boss", 100), new(208, "boss", 317)];
        var configured = MonsterCombatProfileCatalog.Create(Content(rows));
        var historical = MonsterCombatProfileCatalog.Create(Content());
        foreach (var row in rows)
        foreach (var level in new uint[] { 90, 140, 200 })
        {
            var spawn = Spawn(row.MapId, "boss", level);
            var original = historical.Resolve(spawn);
            var actual = configured.Resolve(spawn);
            Check.Equal(original with { CriticalResistance = row.CriticalResistance }, actual,
                "configured rating is independent of level and preserves every other profile field");
            Check.Equal(row.CriticalResistance, actual.ToTargetStats().CriticalResistance,
                "combat targets receive the configured rating, including zero");
        }

        foreach (var spawn in new[]
                 {
                     Spawn(209, "boss", 200), // Known fallback identity, but no exact balance row.
                     Spawn(207, "unlisted-boss", 200),
                     Spawn(207, "ordinary", 200),
                     Spawn(207, "elite", 200),
                     Spawn(207, "unknown", 200)
                 })
            Check.Equal(historical.Resolve(spawn), configured.Resolve(spawn),
                "missing rows, other maps, ordinary/elite monsters and unknown identities retain their profiles");

        var generic = MonsterCombatProfileCatalog.Resolve(200,
            MonsterAttackDamageKind.Physical, isBoss: true);
        Check.Equal(2_632, generic.CriticalResistance,
            "the historical scalar level/rank formula remains available without a configured identity");
        var mage = new CombatAttackerStats { Level = 140, Critical = 206 };
        // trunc(10,000 * 206 / (206 + 100)) = 6732 bp, independent of boss level.
        Check.Equal(6732, AuthoredPlayerPveCurrent.CalculateCriticalChanceBasisPoints(mage,
            configured.Resolve(Spawn(207, "boss", 200)).ToTargetStats()),
            "AresMage's rating uses the shared 67.32 percent critical chance against configured Alpha ratings");
    }

    private static void CheckCoordinationRevision()
    {
        MonsterCombatBalanceDefinition[] rows =
            [new(207, "boss", 100), new(205, "boss", 317), new(204, "boss", 0)];
        var original = MonsterCombatBalanceContent.CoordinationRevision(rows);
        Check.Equal(original, MonsterCombatBalanceContent.CoordinationRevision(rows.Reverse().ToArray()),
            "database result ordering cannot change the coordination revision");
        Check.True(original != MonsterCombatBalanceContent.CoordinationRevision(
                [rows[0] with { CriticalResistance = 101 }, rows[1], rows[2]]),
            "changing a configured rating changes coordination identity");
        Check.True(original != MonsterCombatBalanceContent.CoordinationRevision(
                [rows[0] with { MapId = 208 }, rows[1], rows[2]]) &&
            original != MonsterCombatBalanceContent.CoordinationRevision(
                [rows[0] with { TemplateKey = "unlisted-boss" }, rows[1], rows[2]]),
            "balance row map and template identity both participate in coordination");
        var changed = MonsterCombatBalanceContent.CoordinationRevision(
            [rows[0] with { CriticalResistance = 101 }, rows[1], rows[2]]);
        var fixedRevision = new string('A', 64);
        string Fingerprint(string balanceRevision) => RuntimeContentFingerprint.Create(
            fixedRevision, fixedRevision, fixedRevision, fixedRevision,
            fixedRevision, fixedRevision, fixedRevision, fixedRevision,
            fixedRevision, fixedRevision, fixedRevision, balanceRevision);
        Check.True(Fingerprint(original) != Fingerprint(changed),
            "worker coordination distinguishes changed combat balance even when all eleven other revisions match");
    }

    private static void CheckPinnedWorldReader()
    {
        MonsterCombatBalanceDefinition[] rows = [new(207, "boss", 100), new(205, "boss", 317)];
        var content = Content(rows);
        var reader = PinnedWorldContentReader.Create("boss-balance-check",
            content.Maps.Select(map => map.MapId), [], [], [], gameplay: content);
        Check.True(reader.Gameplay.MonsterCombatBalances.SequenceEqual(rows) &&
            !ReferenceEquals(reader.Gameplay.MonsterCombatBalances, rows),
            "the pinned world reader retains every balance row in an independent collection");
        rows[0] = new(207, "boss", 0);
        Check.Equal(100, reader.Gameplay.MonsterCombatBalances.Single(row => row.MapId == 207).CriticalResistance,
            "source-array mutation after world load cannot change the pinned balance");
        Check.Equal(100, MonsterCombatProfileCatalog.Create(reader.Gameplay)
            .Resolve(Spawn(207, "boss", 200)).CriticalResistance,
            "a catalog built from the pinned world reader consumes its preserved balance");
    }

    private static void CheckPinnedSnapshot()
    {
        MonsterCombatBalanceDefinition[] rows = [new(207, "boss", 100)];
        var content = Content(rows);
        var pinned = MonsterCombatProfileCatalog.Create(content);
        rows[0] = new(207, "boss", 317);
        var refreshed = MonsterCombatProfileCatalog.Create(content);
        var spawn = Spawn(207, "boss", 200);
        Check.Equal(100, pinned.Resolve(spawn).CriticalResistance,
            "a later source-row change cannot mutate an already pinned combat catalog");
        Check.Equal(317, refreshed.Resolve(spawn).CriticalResistance,
            "a newly loaded catalog adopts the revised database value without a formula edit");
    }

    private static void CheckAuthoredOverrides()
    {
        var spawn = Spawn(207, "boss", 200);
        var catalog = MonsterCombatProfileCatalog.Create(Content([new(207, "boss", 317)]));
        var authored = new MonsterCombatProfile(MonsterAttackDamageKind.Magical,
            9f, 200, 15_147, 6_000, 3_000, 2_500, 3_500, 2_000, 707, 8_888, false, true)
        { AuthoredAttackRange = 17f };
        var instanceCatalog = catalog.WithAuthoredOverrides(207,
            [(spawn.ObjectId, spawn.TemplateKey, authored)]);
        Check.Equal(authored with { CriticalResistance = 317 }, instanceCatalog.Resolve(spawn),
            "instance overrides preserve the loaded database rating and all authored attack/defense/range fields");
        Check.Equal(317, instanceCatalog.Resolve(spawn).ToTargetStats().CriticalResistance,
            "the final instance target projection uses the database value after its authored override");

        var absent = Spawn(207, "unlisted-boss", 200);
        var noRow = catalog.WithAuthoredOverrides(207,
            [(absent.ObjectId, absent.TemplateKey, authored)]);
        Check.Equal(authored, noRow.Resolve(absent),
            "an authored boss without a configured row keeps its authored resistance");
        var otherMap = spawn with { MapId = 208 };
        var isolated = catalog.WithAuthoredOverrides(208,
            [(otherMap.ObjectId, otherMap.TemplateKey, authored)]);
        Check.Equal(authored, isolated.Resolve(otherMap),
            "instance overrides cannot transfer a balance row to another map");
    }

    private static void CheckRejectedConfiguration()
    {
        MonsterCombatBalanceDefinition[][] invalid =
        [
            [new(207, "boss", 100), new(207, "boss", 317)],
            [new(207, "boss", -1)],
            [new(207, "unknown", 100)],
            [new(207, "ordinary", 100)],
            [new(207, "elite", 100)],
            [new(209, "boss", 100)], // Fallback publication is not an exact map authority.
            [new(256, "boss", 100)],
            [new(-1, "boss", 100)]
        ];
        foreach (var rows in invalid)
            Check.Throws<InvalidDataException>(
                () => MonsterCombatProfileCatalog.Create(Content(rows)),
                "duplicate, negative, unknown and non-boss configuration fails at catalog construction");
    }

    private static GameplayContentCatalog Content(params MonsterCombatBalanceDefinition[] rows) =>
        GameplayContentCatalog.Empty with
        {
            MonsterCombatBalances = rows,
            Maps = new short[] { 200, 204, 205, 207, 208 }
                .Select(map => new GameplayMapDefinition(map, $"map-{map}", $"Map {map}", null, null))
                .ToArray(),
            MonsterTemplates = new short[] { 200, 204, 205, 207, 208 }
                .Select(map => Template(map, "boss", boss: true))
                .Concat([Template(207, "unlisted-boss", boss: true),
                    Template(207, "ordinary"), Template(207, "elite", elite: true)])
                .ToArray()
        };

    private static GameplayMonsterTemplateDefinition Template(short map, string key,
        bool boss = false, bool elite = false) => new($"{map}:{key}", "map", map,
        $"map-{map}", key, key, boss ? "boss" : elite ? "elite" : "normal",
        boss, elite, false, 1, 2.5f);

    private static CapturedMonsterSpawn Spawn(short map, string key, uint level)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x12);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 46_100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), level);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 8_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 8_000_000);
        Encoding.ASCII.GetBytes(key).CopyTo(packet, 44);
        var spawn = new CapturedMonsterSpawn(map, $"map-{map}", key, key,
            46_100, 0, 0, packet);
        spawn.Validate(map);
        return spawn;
    }
}
