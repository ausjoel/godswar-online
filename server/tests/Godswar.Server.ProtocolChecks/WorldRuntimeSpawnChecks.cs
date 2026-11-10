using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ROUND3 runtime spawns (2026-11-09): the world-map overlay (attach, routing, owner-only combat, timeout and
/// corpse retirement, id pool) in both monster runtime modes, plus the seal box / boss scroll policy.
/// </summary>
internal static class WorldRuntimeSpawnChecks
{
    public const string CheckName = "ROUND3 world-map runtime spawns: overlay, seal boxes and boss scrolls";

    private const byte MapId = 12;
    private const int Owner = 7;
    private const int Stranger = 8;

    public static Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            CheckOverlay(mode);
        }
        CheckPolicy();
        return Task.CompletedTask;
    }

    private static CapturedMonsterSpawn Spawn(uint objectId, string template, float x, float z, uint health = 500)
    {
        var packet = WorldRuntimeSpawnPackets.Build(null, MapId, objectId, template, 30, health, x, 5f, z, 1f);
        var spawn = new CapturedMonsterSpawn(MapId, "Parnitha_2", template, template, objectId, x, z, packet);
        spawn.Validate(MapId);
        return spawn;
    }

    private static IMonsterMapRuntime Single(MonsterRuntimeMode mode, CapturedMonsterSpawn spawn, DateTimeOffset now) =>
        MonsterMapRuntimeFactory.Create(mode, MapId, [spawn], now, respawnPolicy: MonsterRespawnPolicy.Never);

    private static WorldRuntimeSpawnSpec Spec(int owner, bool ownerOnly, int pills = 198) =>
        new(owner, ownerOnly, TimeSpan.FromMinutes(10),
            SummonItemPolicy.SplitStacks(SummonItemPolicy.ExperiencePill, pills), "check");

    private static void CheckOverlay(MonsterRuntimeMode mode)
    {
        var label = $"[{mode}] ";
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var baseRuntime = MonsterMapRuntimeFactory.Create(mode, MapId, [Spawn(9_000, "B_normalB_centaur_008", 0, 0)],
            now, respawnPolicy: MonsterRespawnPolicy.Timed);
        baseRuntime.Advance(now, []);
        var overlay = new WorldRuntimeSpawnOverlay(baseRuntime);
        Check.Equal(1, overlay.Count, label + "an empty overlay is just the base roster");
        overlay.Advance(now.AddSeconds(1), []);

        // Id pool: skips taken ids and live spawns.
        Check.True(overlay.TryAllocateObjectId(new HashSet<uint> { 48_000 }, now, out var firstId) && firstId == 48_001,
            label + "the id pool skips a taken id");
        var owned = Single(mode, Spawn(firstId, "B_normalB_centaur_008", 50, 50), now);
        Check.True(overlay.TryAttach(owned, Spec(Owner, ownerOnly: true), now, out _), label + "a staged spawn attaches");
        Check.Equal(2, overlay.Count, label + "the overlay lists base + spawn");
        Check.True(overlay.TryGetSnapshot(firstId, out var snapshot) && snapshot.IsAlive && snapshot.IsSpawned,
            label + "the spawn is alive and visible");
        Check.True(overlay.Snapshot().Any(monster => monster.ObjectId == 9_000) &&
                   overlay.Snapshot().Any(monster => monster.ObjectId == firstId), label + "snapshot has both");
        Check.True(overlay.Advance(now.AddMilliseconds(100), []).PositionsChanged,
            label + "the first tick after attaching publishes the spawn");
        Check.True(overlay.TryGetSpawn(firstId, snapshot.RuntimeInstanceId, out var spec) && spec.Drops.Count == 2,
            label + "the spawn's drops are found by id and runtime instance");
        Check.True(!overlay.TryGetSpawn(firstId, Guid.NewGuid(), out _), label + "another runtime instance is not it");

        // Limits and collisions.
        var second = Single(mode, Spawn(48_100, "B_normalB_centaur_008", 60, 60), now);
        Check.True(!overlay.TryAttach(second, Spec(Owner, ownerOnly: true), now, out var ownerRefusal) &&
                   ownerRefusal == "owner limit reached", label + "one live spawn per owner");
        var clash = Single(mode, Spawn(firstId, "B_normalB_centaur_008", 60, 60), now);
        Check.True(!overlay.TryAttach(clash, Spec(99, ownerOnly: true), now, out _), label + "a used id is refused");
        var outside = Single(mode, Spawn(9_001, "B_normalB_centaur_008", 60, 60), now);
        Check.True(!overlay.TryAttach(outside, Spec(98, ownerOnly: true), now, out _),
            label + "an id outside the runtime-spawn pool is refused");

        // Owner-only combat: a stranger cannot hurt it; the owner can; routed calls reach the spawn.
        Check.True(!overlay.TryApplyDamage(firstId, 10, Stranger, null, now.AddSeconds(1), out _),
            label + "a stranger's hit is refused");
        Check.True(!overlay.TryApplyDamage(firstId, 10, null, null, now.AddSeconds(1), out _),
            label + "unattributed damage is refused");
        Check.True(!overlay.TryApplyStun(firstId, Stranger, TimeSpan.FromSeconds(1), null, now.AddSeconds(1), out _),
            label + "a stranger's stun is refused");
        Check.True(overlay.TryApplyDamage(9_000, 1, Stranger, null, now.AddSeconds(1), out _),
            label + "base monsters are still routed to the base runtime");
        Check.True(overlay.TryApplyDamage(firstId, 10_000, Owner, null, now.AddSeconds(2), out var kill) && kill.Killed,
            label + "the owner kills it");
        Check.True(overlay.TrySetCorpseDespawnAt(firstId, kill.Monster.SpawnGeneration, now.AddSeconds(22)),
            label + "the loot corpse window is routed to the spawn");

        // Corpse retirement: after the corpse despawns the spawn leaves the overlay; its id is quarantined.
        var t = now.AddSeconds(2);
        for (var i = 0; i < 40 && overlay.SpawnCount > 0; i++)
        {
            t = t.AddSeconds(1);
            overlay.Advance(t, []);
        }
        Check.Equal(0, overlay.SpawnCount, label + "a dead spawn is retired after its corpse despawns");
        Check.True(overlay.TryAllocateObjectId(new HashSet<uint>(), t, out var nextId) && nextId != firstId,
            label + "a retired id is quarantined");

        // Timeout: an untouched spawn despawns at the end of its lifetime.
        var open = Single(mode, Spawn(48_200, "B_normalB_centaur_008", 70, 70), t);
        Check.True(overlay.TryAttach(open, Spec(Stranger, ownerOnly: false), t, out _), label + "an open spawn attaches");
        Check.True(overlay.TryApplyDamage(48_200, 1, Owner, null, t.AddSeconds(1), out _),
            label + "anyone may hit an open spawn");
        overlay.Advance(t.AddMinutes(5), []);
        Check.True(overlay.TryGetSnapshot(48_200, out _), label + "still there before its lifetime ends");
        var expiry = overlay.Advance(t.AddMinutes(10).AddSeconds(1), []);
        Check.True(expiry.PositionsChanged && !overlay.TryGetSnapshot(48_200, out _) &&
                   overlay.Snapshot().All(monster => monster.ObjectId != 48_200),
            label + "the lifetime ends: removed and published");
        Check.True(overlay.TryGetSnapshot(9_000, out _), label + "the base roster is untouched");
    }

    private static void CheckPolicy()
    {
        // Every fallback key must be a real global (MonsterCon500) template, spelled exactly.
        var globals = MonsterTemplateSeeds.Monsters.Where(seed => seed.SourceMapId is null)
            .Select(seed => seed.TemplateKey).ToHashSet(StringComparer.Ordinal);
        foreach (var item in SummonItemPolicy.Items.Values)
        {
            foreach (var choice in item.Choices)
            {
                Check.True(choice.GlobalTemplateKey is { } key && globals.Contains(key),
                    $"item {item.ItemId} fallback '{choice.GlobalTemplateKey}' is a published global template");
            }
        }

        Check.Equal(10, SummonItemPolicy.Items.Count, "seal boxes 14034-14037 and scrolls 14060-14065");
        Check.Equal(4174u, SummonItemPolicy.ExperiencePill, "the Exp Pill is 4174");
        Check.True(SummonItemPolicy.SplitStacks(4174, 99).Select(d => d.Quantity).SequenceEqual([99]), "99 = 1 stack");
        Check.True(SummonItemPolicy.SplitStacks(4174, 198).Select(d => d.Quantity).SequenceEqual([99, 99]), "198 = 2 x 99");
        Check.True(SummonItemPolicy.SplitStacks(4174, 297).Select(d => d.Quantity).SequenceEqual([99, 99, 99]), "297 = 3 x 99");
        Check.True(SummonItemPolicy.SplitStacks(4174, 100).Select(d => d.Quantity).SequenceEqual([99, 1]), "never above 99");
        Check.Equal(99, SummonItemPolicy.Items[14035].Choices.Single().PillCount, "Centaur box text: 99");
        Check.Equal(198, SummonItemPolicy.Items[14036].Choices.Single().PillCount, "Ogre box text: 198");
        Check.Equal(297, SummonItemPolicy.Items[14037].Choices.Single().PillCount, "Persian Soldier box text: 297");
        Check.True(SummonItemPolicy.Items.Values.Where(i => i.Kind == SummonItemKind.SealBox).All(i => i.MinimumLevel == 20),
            "seal boxes are level 20+ (PlayLv 20,200)");
        Check.True(SummonItemPolicy.Items[14060].Camp == FactionPortalSkillPolicy.AthensCamp &&
                   SummonItemPolicy.Items[14060].Maps!.SetEquals(new short[] { 2, 3, 11, 12 }),
            "Athens scrolls: Suburbs of Athens, Parnitha, Marathon");
        Check.True(SummonItemPolicy.Items[14065].Camp == FactionPortalSkillPolicy.SpartaCamp &&
                   SummonItemPolicy.Items[14065].Maps!.SetEquals(new short[] { 4, 5, 13, 14 }),
            "Sparta scrolls: Suburbs of Sparta, Nemea, Peloponnese");

        GameplayMonsterTemplateDefinition T(short? map, string key, string name, bool boss = false) =>
            new(map is null ? "global" : $"map:{map}", map is null ? "global" : "map", map, "", key, name,
                boss ? "boss" : "normal", boss, false, false, 1, 2.5f);
        var templates = new[]
        {
            T(12, "B_normalB_centaur_008", "Centaur"),
            T(12, "B_normalxs_centaur_008", "Wanted Centaur"),
            T(8, "B_normalB_persiawarrior_001", "Persian Soldier"),
            T(null, "A_normal_Trialcentaurwl_001", "Centaur (Physical)"),
            T(null, "A_normal_Trialantai_001", "Antaeus"),
            T(null, "A_boss_baitoulong_001", "Ladon", boss: true),
        };
        var centaur = SummonItemPolicy.Items[14035].Choices.Single();
        Check.Equal("B_normalB_centaur_008",
            SummonItemPolicy.ResolveTemplate(centaur, templates, 12, SummonItemKind.SealBox)?.TemplateKey ?? "",
            "the map's own Centaur is preferred");
        Check.Equal("A_normal_Trialcentaurwl_001",
            SummonItemPolicy.ResolveTemplate(centaur, templates, 2, SummonItemKind.SealBox)?.TemplateKey ?? "",
            "elsewhere the global Centaur is used");
        Check.Equal("A_normal_Trialantai_001",
            SummonItemPolicy.ResolveTemplate(SummonItemPolicy.Items[14036].Choices.Single(), templates, 12,
                SummonItemKind.SealBox)?.TemplateKey ?? "", "no Ogre anywhere: the GENERATED stand-in");
        Check.Equal("B_normalB_persiawarrior_001",
            SummonItemPolicy.ResolveTemplate(SummonItemPolicy.Items[14037].Choices.Single(), templates, 8,
                SummonItemKind.SealBox)?.TemplateKey ?? "", "Thermopylae has a real Persian Soldier");
        Check.True(SummonItemPolicy.ResolveTemplate(SummonItemPolicy.Items[14037].Choices.Single(), templates, 2,
                SummonItemKind.SealBox) is null, "no drawable template: refused before the item is used");
        Check.Equal("A_boss_baitoulong_001",
            SummonItemPolicy.ResolveTemplate(SummonItemPolicy.Items[14060].Choices.Single(), templates, 2,
                SummonItemKind.BossScroll)?.TemplateKey ?? "", "the Ladon scroll uses the global Ladon");

        var packet = WorldRuntimeSpawnPackets.Build(null, 11, 48_005, "A_boss_baitoulong_001", 40, 140_000, 1, 2, 3, 1);
        Check.Equal((ushort)11, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)),
            "the appearance packet carries the map id (else the client hides it)");
        Check.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)), "tier written");
        Check.True(SummonItemPolicy.Stats(SummonItemKind.SealBox, 50).MaximumHealth <
                   SummonItemPolicy.Stats(SummonItemKind.BossScroll, 50).MaximumHealth, "seal monsters are weak");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SealBoxes) && Finalise2Rates.On(Finalise2RateKeys.BossScrolls),
            "both switches default to on");
        Check.True(WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(48_500) &&
                   !WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(47_750) &&
                   !WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(46_000), "a disjoint object-id pool");
    }
}
