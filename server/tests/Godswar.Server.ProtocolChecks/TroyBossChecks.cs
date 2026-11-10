using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Troy;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W2 F-27 TROY slice 2: map 44 layout, the five-hero chain, chests, transporters and the spawn SQL.</summary>
internal static class TroyBossChecks
{
    public const string CheckName = "W2 F-27 Troy slice 2: map 44 spawns, five-hero chain, chests, transporters";

    public static Task RunAsync()
    {
        CheckLayout();
        CheckChain();
        CheckOverlayAttackers();
        CheckRatesAndRouting();
        CheckSql();
        return Task.CompletedTask;
    }

    private static MonsterTemplateSeed Seed(string key) =>
        MonsterTemplateSeeds.Monsters.First(seed => seed.TemplateKey == key);

    private static void CheckLayout()
    {
        var bosses = TroyLayout.Bosses;
        Check.Equal(5, bosses.Length, "five heroes");
        string[] names = ["Archer Pandarus (Boss)", "King Sarpedon (Boss)", "Prince Lycaon (Boss)", "Prince Paris (Boss)",
            "Prince Hector (Boss)"];
        for (var i = 0; i < 5; i++)
        {
            var boss = bosses[i];
            Check.Equal(i + 1, boss.Stage, "heroes in stage order");
            Check.Equal(names[i], boss.DisplayName, $"stage {i + 1} is {names[i]} (client Monster.ini)");
            var seed = Seed(boss.TemplateKey);
            Check.True(seed.IsBoss && seed.Rank == "boss", $"{boss.TemplateKey} is a boss template");
            Check.True(boss.TemplateKey.Contains($"boss{i + 1}_"), "the key digit pairs the hero with its stage");
            Check.Equal($"Troy_{6 + i:000}", boss.ChestNpcKey, "chests Troy_006-010 in hero order (NpcTemplateSeed)");
            Check.True(Math.Abs(boss.ChestX - boss.X) + Math.Abs(boss.ChestZ - boss.Z) < 12, "each chest stands by its hero");
            Check.True(boss.Level == 55 + 10 * (i + 1) && boss.BaseHealth > 100_000, "hero level and HP as generated");
        }

        // Beach heroes south of the city wall, Paris and Hector inside it (two separate walkable components).
        Check.True(bosses.Take(3).All(static boss => boss.Z < 0) && bosses.Skip(3).All(static boss => boss.Z > 10),
            "three heroes on the beach side, two in the city");

        foreach (var (stage, template, rank) in TroyLayout.RetinueTemplates)
        {
            var seed = Seed(template);
            Check.True(seed.Rank == rank && !seed.IsBoss && seed.IsElite == (rank == "elite"),
                $"{template} is a {rank} template");
            Check.True(template.Contains($"{rank}{stage}_"), $"{template} belongs to stage {stage}");
            Check.True(seed.SourceMapId is null or TroyEvent.MapId, $"{template} is map 44's own or a global template");
        }

        Check.True(TroyLayout.FirstMonsterObjectId > 8_000 && TroyLayout.LastMonsterObjectId < 48_000,
            "generated monster ids stay out of the runtime-spawn pool 48000-48999");
        var npcIds = TroyLayout.Npcs.Select(static npc => npc.ObjectId).ToArray();
        Check.True(npcIds.Distinct().Count() == npcIds.Length && npcIds.All(static id => id is < 48_000 or > 48_999),
            "NPC ids unique and outside the runtime-spawn pool");
        Check.True(npcIds.All(static id => id is < 47_901 or > 47_907 && id is < 47_950 or > 47_983),
            "NPC ids clear of the Pindus / other authored NPC ids");
        string[] keys = ["Troy_001", "Troy_002", "Troy_003", "Troy_006", "Troy_007", "Troy_008", "Troy_009", "Troy_010", "Troy_011"];
        Check.True(keys.All(key => TroyLayout.Npcs.Any(npc => npc.NpcKey == key)), "all nine Troy NPCs are placed");
        var sqlTemplates = Regex.Matches(File.ReadAllText(FindRepoFile("docs/evidence/troy/troy-spawns.sql")),
            @"'(Troy_\d{3})', '(Troy_\d{3}_\w+)', NULL").Select(static match => match.Groups[2].Value).ToArray();
        Check.True(sqlTemplates.Length == 9 && sqlTemplates.All(template =>
                NpcTemplateSeeds.Appearances.Any(seed => seed.TemplateKey == template)),
            "every NPC template the SQL writes is an exact Origin client appearance key (else BuildPlacements drops it)");

        var add = typeof(GameClientHandler).GetMethod("AddTroyTemplateKeys", BindingFlags.Static | BindingFlags.NonPublic)!;
        var troy = new HashSet<string>();
        add.Invoke(null, [(short)44, troy]);
        var other = new HashSet<string>();
        add.Invoke(null, [(short)43, other]);
        Check.True(troy.Count == 10 && other.Count == 0, "map 44 admits the ten retinue templates; other maps nothing");
    }

    private static void CheckChain()
    {
        var chain = TroyBossChain.CreateForChecks();
        var day = new DateOnly(2026, 11, 12);
        var t = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var delay = TimeSpan.FromSeconds(30);
        var first = chain.TryBeginSpawn(day, t);
        Check.True(first?.Stage == 1, "Pandarus is first");
        Check.True(chain.TryBeginSpawn(day, t) is null, "one spawn at a time");
        var id1 = Guid.NewGuid();
        chain.AttackersFor(day, 1).TryAdd(7, 0); // the overlay fills this set
        chain.CompleteSpawn(day, 1, 45_900, id1, t);
        Check.True(chain.TryBeginSpawn(day, t.AddMinutes(1)) is null, "no second hero while one is up");
        Check.True(chain.LiveBoss(day) is { Stage: 1, ObjectId: 45_900 }, "the live hero is known");
        Check.True(chain.ChestState(day, 1, 7) == TroyChestState.BossNotDown, "chest closed while its hero lives");
        Check.True(chain.RecordKill(day, 45_901, id1, 7, t, delay) is null, "another monster is not the hero");
        Check.True(chain.RecordKill(day, 45_900, Guid.NewGuid(), 7, t, delay) is null, "another spawn generation is not the hero");
        Check.True(chain.RecordKill(day, 45_900, id1, 8, t.AddMinutes(2), delay)?.Stage == 1, "Pandarus falls");
        Check.True(chain.RecordKill(day, 45_900, id1, 8, t.AddMinutes(2), delay) is null, "a kill is recorded once");
        Check.True(chain.TryBeginSpawn(day, t.AddMinutes(2).AddSeconds(29)) is null, "the next hero waits for the delay");
        var second = chain.TryBeginSpawn(day, t.AddMinutes(2).AddSeconds(30));
        Check.True(second?.Stage == 2, "Sarpedon after the delay");

        Check.True(chain.ChestState(day, 1, 7) == TroyChestState.Available, "an attacker may open the chest");
        Check.True(chain.ChestState(day, 1, 8) == TroyChestState.Available, "the killer counts as an attacker");
        Check.True(chain.ChestState(day, 1, 9) == TroyChestState.NotParticipant, "a bystander gets nothing (207)");
        Check.True(chain.TryClaim(day, 1, 7) && !chain.TryClaim(day, 1, 7), "a chest pays once");
        Check.True(chain.ChestState(day, 1, 7) == TroyChestState.Claimed, "then 1108");
        chain.ReleaseClaim(day, 1, 7);
        Check.True(chain.ChestState(day, 1, 7) == TroyChestState.Available, "a failed payout releases the claim");

        var id2 = Guid.NewGuid();
        chain.CompleteSpawn(day, 2, 45_910, id2, t.AddMinutes(3));
        chain.AttackersFor(day, 2).TryAdd(8, 0);
        Check.True(chain.ChestState(day, 1, 8) == TroyChestState.Forfeited, "fighting the next hero forfeits the chest (1107)");
        Check.True(!chain.TryClaim(day, 1, 8), "a forfeited chest cannot be claimed");
        Check.True(chain.ChestState(day, 1, 7) == TroyChestState.Available, "who has not fought Sarpedon may still claim");

        chain.RecordVanished(day, 45_910, t.AddMinutes(70));
        Check.True(chain.LiveBoss(day) is null && chain.TryBeginSpawn(day, t.AddMinutes(70))?.Stage == 2,
            "a timed-out hero appears again");
        chain.CompleteSpawn(day, 2, 45_911, id2, t.AddMinutes(70));
        Check.True(chain.Killed(day) == 1 && !chain.BeachCleared(day), "one hero down");
        chain.RecordKill(day, 45_911, id2, 7, t.AddMinutes(71), TimeSpan.Zero);
        for (var stage = 3; stage <= 5; stage++)
        {
            Check.True(chain.TryBeginSpawn(day, t.AddMinutes(70 + stage))?.Stage == stage, $"stage {stage} follows");
            var id = Guid.NewGuid();
            chain.CompleteSpawn(day, stage, (uint)(45_920 + stage), id, t.AddMinutes(70 + stage));
            Check.True(chain.BeachCleared(day) == stage > 3, "the Troy Transporter opens after the three beach heroes");
            chain.RecordKill(day, (uint)(45_920 + stage), id, 7, t.AddMinutes(70 + stage), TimeSpan.Zero);
        }

        Check.True(chain.Killed(day) == 5 && chain.TryBeginSpawn(day, t.AddHours(3)) is null, "all five down, nothing more");
        chain.AbortSpawn(t);
        Check.True(chain.TryBeginSpawn(day.AddDays(7), t.AddDays(7))?.Stage == 1 && chain.Killed(day) == 0,
            "the next event day starts again at Pandarus");

        var aborted = TroyBossChain.CreateForChecks();
        Check.True(aborted.TryBeginSpawn(day, t) is not null, "reserve");
        aborted.AbortSpawn(t);
        Check.True(aborted.TryBeginSpawn(day, t.AddSeconds(5)) is null && aborted.TryBeginSpawn(day, t.AddSeconds(20)) is not null,
            "a refused spawn is retried after 20 s");

        Check.True(Enumerable.Range(1, 5).Select(TroyBossChain.OwnerFor).Distinct().Count() == 5 &&
                   Enumerable.Range(1, 5).All(static stage => TroyBossChain.OwnerFor(stage) < 0),
            "each hero has its own synthetic owner (the overlay's one-per-owner limit)");
        Check.True(TroyBossChain.BossOfChest("Troy_009")?.Stage == 4 && TroyBossChain.BossOfChest("Troy_012") is null,
            "chest to hero; the Deific Chest is not a hero chest");
    }

    private static void CheckOverlayAttackers()
    {
        const byte map = 44;
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        CapturedMonsterSpawn Spawn(uint id, string template)
        {
            var packet = WorldRuntimeSpawnPackets.Build(null, map, id, template, 65, 5000, 0, 0, 0, 1f);
            var spawn = new CapturedMonsterSpawn(map, "Troy", template, template, id, 0, 0, packet);
            spawn.Validate(map);
            return spawn;
        }

        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            var baseRuntime = MonsterMapRuntimeFactory.Create(mode, map, [Spawn(45_101, "A_normal1_Tpersiawarrior_005")], now,
                respawnPolicy: MonsterRespawnPolicy.Timed);
            var overlay = new WorldRuntimeSpawnOverlay(baseRuntime);
            var attackers = new ConcurrentDictionary<int, byte>();
            var hero = MonsterMapRuntimeFactory.Create(mode, map, [Spawn(48_100, "A_boss2_TGodGC_001")], now,
                respawnPolicy: MonsterRespawnPolicy.Never);
            var spec = new WorldRuntimeSpawnSpec(TroyBossChain.OwnerFor(2), false, TimeSpan.FromMinutes(65), [], "check",
                Attackers: attackers);
            Check.True(overlay.TryAttach(hero, spec, now, out var refusal), $"[{mode}] hero attaches: {refusal}");
            Check.True(overlay.TryApplyDamage(48_100, 10, 7, null, now.AddSeconds(1), out _) && attackers.ContainsKey(7),
                $"[{mode}] a landed hit records its attacker");
            Check.True(overlay.TryApplyDamage(45_101, 10, 9, null, now.AddSeconds(1), out _) && !attackers.ContainsKey(9),
                $"[{mode}] hits on roster monsters are not recorded");
            var second = MonsterMapRuntimeFactory.Create(mode, map, [Spawn(48_101, "A_boss3_TGodGC_001")], now,
                respawnPolicy: MonsterRespawnPolicy.Never);
            Check.True(overlay.TryAttach(second, spec with { OwnerCharacterId = TroyBossChain.OwnerFor(3), Attackers = null },
                now, out _), $"[{mode}] a second hero with its own owner id attaches");
            Check.True(overlay.TryApplyDamage(48_101, 10, 8, null, now.AddSeconds(2), out _) && !attackers.ContainsKey(8),
                $"[{mode}] a spawn without an attackers set records nothing");
        }
    }

    private static void CheckRatesAndRouting()
    {
        var defaults = TroyBossChain.Definitions.ToDictionary(static d => d.Key, static d => d.DefaultValue);
        Check.True(defaults[TroyBossChain.BossesSwitch] == 1m && defaults[TroyBossChain.BossHpPercent] == 100m &&
                   defaults[TroyBossChain.BossDelaySeconds] == 30m && defaults[TroyBossChain.ChestPills] == 10m &&
                   defaults[TroyBossChain.ChestRecipePercent] == 10m,
            "defaults: bosses 1, HP 100 %, delay 30 s, 10 pills, 10 % recipe");
        Check.True(TroyEvent.Definitions.Single(static d => d.Key == TroyEvent.EntrySwitch).DefaultValue == 0m,
            "troy.entry still defaults to 0");
        Check.True(ServerRateCatalog.Definitions.Count(static d => d.Key.StartsWith("troy.", StringComparison.Ordinal)) ==
                   TroyEvent.Definitions.Count + TroyBossChain.Definitions.Count, "all Troy rates are registered once");
        Check.True(TroyBossChain.PillsFor(1) == 10 && TroyBossChain.PillsFor(5) == 30, "chest pills 10 ... 30");
        Check.True(TroyBossChain.PillsFor(5) <= 99, "a chest never exceeds one stack of 99");
        Check.True(OperatorBuffCatalog.TryGetByStatus(TroyBossChain.AchillesRageFirstStatus, out _) &&
                   OperatorBuffCatalog.TryGetByStatus(TroyBossChain.AchillesRageFirstStatus + 4, out _),
            "Achilles' Rage Lv. 1-5 (620-624) are known statuses");

        Seq([101, 105, 106, 321], OpeningMenu("Troy_003", 67), "Agamemnon draws the coordinator menu");
        Seq([101, 105, 106, 321], OpeningMenu("Troy_001", 67), "the Trojan War Transporter draws the coordinator menu");
        foreach (var key in new[] { "Troy_002", "Troy_006", "Troy_010", "Troy_011" })
        {
            Seq([], OpeningMenu(key, 67), $"{key}: function 67, answered by the handler on open");
        }

        var appear = Enumerable.Range(1, 5).Select(TroyBossChain.AppearNotice).ToArray();
        Check.True(appear[1].StartsWith("Sarpedon has appeared at the Apollo Temple", StringComparison.Ordinal) &&
                   appear[4].Contains("palace of Troy", StringComparison.Ordinal), "notices are LuaText Troy_Notice1-4");
        Check.True(appear.Concat(TroyLayout.Bosses.Select(TroyBossChain.FallNotice))
                .All(static line => line.Length <= 106 && line.All(static c => c is >= ' ' and <= '~')),
            "every notice fits the green announcement (106 printable ASCII)");
    }

    private static void CheckSql()
    {
        var path = FindRepoFile("docs/evidence/troy/troy-spawns.sql");
        var sql = File.ReadAllText(path);
        var rows = Regex.Matches(sql, @"\('(troy\d-[ne]\d\d(?:-\d\d)?)', 44, 'Troy', '([^']+)', '[^']*', (\d+), (-?[\d.]+), (-?[\d.]+), '\\x([0-9a-f]+)'");
        Check.Equal(TroyLayout.GeneratedMonsterRows, rows.Count, "every generated monster row parses");
        var admitted = TroyLayout.RetinueTemplates.Select(static row => row.Template).ToHashSet();
        foreach (Match row in rows)
        {
            var template = row.Groups[2].Value;
            var id = uint.Parse(row.Groups[3].Value);
            var bytes = Convert.FromHexString(row.Groups[6].Value);
            Check.True(admitted.Contains(template), $"{row.Groups[1].Value}: template admitted on map 44");
            Check.True(id is >= TroyLayout.FirstMonsterObjectId and <= TroyLayout.LastMonsterObjectId, "id in range");
            // IsRuntimeSpawnObjectId is global (any map), and the Warriors' Training Ground (PR #95) used 48001-48684.
            Check.True(!WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(id) && id is < 48_001 or > 48_684,
                $"{row.Groups[1].Value}: id {id} outside the runtime-spawn pool and the Training Ground ids");
            var spawn = new CapturedMonsterSpawn(44, "Troy", template, template, id, float.Parse(row.Groups[4].Value),
                float.Parse(row.Groups[5].Value), bytes);
            spawn.Validate(44); // throws on a malformed packet
        }

        Check.True(!Regex.IsMatch(sql, @"'A_boss\d_"),
            "no hero in the static SQL (the server spawns them in order)");
        foreach (var (key, objectId, _, _) in TroyLayout.Npcs)
        {
            Check.True(!WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(objectId) && objectId is < 48_001 or > 48_684,
                $"{key}: NPC id {objectId} outside the runtime-spawn pool and the Training Ground ids");
            Check.True(sql.Contains($"'{key}', '{key}_", StringComparison.Ordinal) && sql.Contains($", {objectId}, 1, ", StringComparison.Ordinal),
                $"{key} is placed by the SQL with id {objectId}");
        }

        Check.True(File.ReadAllText(FindRepoFile("docs/evidence/troy/undo-troy-spawns.sql"))
            .Contains("troy-generated-20261109", StringComparison.Ordinal), "the undo removes exactly the generated rows");
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }

    private static void Seq(int[] expected, int[] actual, string description) =>
        Check.True(expected.SequenceEqual(actual),
            $"{description}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");

    private static int[] OpeningMenu(string key, int function)
    {
        var resolve = typeof(GameClientHandler).GetMethod("ResolveScriptedNpcDialogues",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var npc = new NpcSpawnDefinition(44, "Troy", key, key + "_X", 47_930, 0, 0, 47_930, 1, 0, [], []);
        var dialogue = (resolve.Invoke(null, [npc]) as IDictionary)?[function] ??
            throw new InvalidOperationException($"{key} has no function {function}");
        return ((IEnumerable<int>)dialogue.GetType().GetProperty("OpeningMenu")!.GetValue(dialogue)!).ToArray();
    }
}
