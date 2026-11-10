using System.Text.Json;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ROUND3 summons, the admin F-150 ("Defaults: summons no EXP, guardians no drop, scroll bosses summoner-only, B-Gold flat
/// 10-200x"): which runtime-spawn sources pay kill EXP, that a killed summon is still found by the lookup the kill
/// reward runs, and the Sacred Sealer chests' B-Gold.
/// </summary>
internal static class SummonsNoExperienceChecks
{
    public const string CheckName = "ROUND3 summons F-150: summons no EXP, Sealer chest B-Gold";

    private const byte MapId = 15; // Derveni

    public static Task RunAsync()
    {
        CheckFamilies();
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            CheckKilledSummonLookup(mode);
        }
        CheckChestBindingGold();
        CheckEvidenceEncoding();
        return Task.CompletedTask;
    }

    private static WorldRuntimeSpawnSpec Spec(string source, bool ownerOnly = false) =>
        new(1, ownerOnly, TimeSpan.FromMinutes(1), [], source);

    private static void CheckFamilies()
    {
        Check.True(!Spec("totem:4475").PaysKillExperience, "Totem Pole soldiers (totem:4475) pay no kill EXP");
        Check.True(!Spec("totem:4473").PaysKillExperience, "Totem Pole soldiers (totem:4473) pay no kill EXP");
        Check.True(!Spec("treasure:35505").PaysKillExperience, "treasure guardians (boss set) pay no kill EXP");
        Check.True(!Spec("treasure:32001").PaysKillExperience, "treasure guardians (beside the digger) pay no kill EXP");
        Check.True(Spec("item:14035").PaysKillExperience, "a seal-box monster keeps its kill EXP");
        Check.True(Spec("item:14062").PaysKillExperience, "a scroll boss keeps its kill EXP (summoner-only)");
        Check.True(Spec("troy:hero3").PaysKillExperience, "a Troy hero keeps its kill EXP");
        Check.True(WorldRuntimeSpawnSpec.NoKillRewardFamilies.SetEquals(["totem", "treasure"]),
            "exactly the totem and treasure families are rewardless");
    }

    private static IMonsterMapRuntime Single(MonsterRuntimeMode mode, uint objectId, DateTimeOffset now, byte? camp)
    {
        var packet = WorldRuntimeSpawnPackets.Build(null, MapId, objectId, "A_eliteQ_greecewarrior_004", 30, 100,
            objectId % 100, 5f, 0, 1f, camp);
        var spawn = new Domain.World.Content.CapturedMonsterSpawn(MapId, "", "A_eliteQ_greecewarrior_004",
            "Summoned Spartan Soldier", objectId, objectId % 100, 0, packet);
        spawn.Validate(MapId);
        return MonsterMapRuntimeFactory.Create(mode, MapId, [spawn], now, respawnPolicy: MonsterRespawnPolicy.Never);
    }

    /// <summary>The kill reward looks the spawn up by (object id, runtime instance) right after the killing blow.</summary>
    private static void CheckKilledSummonLookup(MonsterRuntimeMode mode)
    {
        var label = $"[{mode}] ";
        var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var basePacket = WorldRuntimeSpawnPackets.Build(null, MapId, 9_000, "A_eliteQ_mage_001", 40, 5_000, 0, 5f, 0, 1f);
        var baseRuntime = MonsterMapRuntimeFactory.Create(mode, MapId,
            [new Domain.World.Content.CapturedMonsterSpawn(MapId, "", "A_eliteQ_mage_001", "Mage", 9_000, 0, 0, basePacket)],
            now, respawnPolicy: MonsterRespawnPolicy.Timed);
        var camps = new Dictionary<int, int> { [1] = 0, [2] = 1 }; // owner 1 Sparta, killer 2 Athens
        var overlay = new WorldRuntimeSpawnOverlay(baseRuntime, id => camps.TryGetValue(id, out var camp) ? camp : null);

        var totem = new WorldRuntimeSpawnSpec(1, false, ItemRefreshMonsters.TotemLifetime, [], "totem:4475",
            ItemRefreshMonsters.TotemOwnerLimit, 0);
        var soldiers = Enumerable.Range(0, 5).Select(i => (Single(mode, (uint)(48_010 + i), now, 0), totem)).ToArray();
        Check.True(overlay.TryAttachGroup(soldiers, now, out _), label + "5 Spartan Totem soldiers attach");
        Check.True(overlay.TryApplyDamage(48_010, 1_000_000, 2, null, now.AddSeconds(1), out var kill) && kill.Killed,
            label + "an enemy-faction player kills a soldier");
        Check.True(overlay.TryGetSpawn(48_010, kill.Monster.RuntimeInstanceId, out var found) &&
                   found.Source == "totem:4475" && !found.PaysKillExperience,
            label + "the killed soldier is still found by the kill-reward lookup, as a no-EXP summon");

        var seal = new WorldRuntimeSpawnSpec(2, true, TimeSpan.FromMinutes(10), [], "item:14035");
        Check.True(overlay.TryAttach(Single(mode, 48_020, now, null), seal, now, out _), label + "a seal-box monster attaches");
        Check.True(overlay.TryApplyDamage(48_020, 1_000_000, 2, null, now.AddSeconds(1), out var sealKill) && sealKill.Killed &&
                   overlay.TryGetSpawn(48_020, sealKill.Monster.RuntimeInstanceId, out var sealFound) &&
                   sealFound.PaysKillExperience, label + "a killed seal-box monster still pays kill EXP");
        Check.True(!overlay.TryGetSpawn(48_010, Guid.NewGuid(), out _),
            label + "another runtime instance under the same id is not the summon");
    }

    private static void CheckChestBindingGold()
    {
        var templates = TreasureChestTemplates();
        var expected = new Dictionary<uint, (int Price, int Minimum, int Maximum)>
        {
            [14066] = (180, 1_800, 36_000),
            [14067] = (900, 9_000, 180_000),
            [14068] = (1_800, 18_000, 360_000),
        };
        foreach (var (chest, (price, minimum, maximum)) in expected)
        {
            Check.True(GiftPackPolicy.TryResolve(templates, chest, out var pack), $"chest {chest} still opens as a gift pack");
            Check.Equal(minimum, pack.BindingGoldMinimum, $"chest {chest}: at least 10x the {price} Gold");
            Check.Equal(maximum, pack.BindingGoldMaximum, $"chest {chest}: at most 200x the {price} Gold");
            Check.True(pack.Rewards.Length > 0, $"chest {chest} keeps its pack items");
            var random = new Random(150);
            var rolls = Enumerable.Range(0, 5_000).Select(_ => GiftPackPolicy.RollBindingGold(pack, random)).ToArray();
            Check.True(rolls.All(roll => roll >= minimum && roll <= maximum), $"chest {chest}: every roll is inside 10-200x");
            Check.True(rolls.Min() < minimum + (maximum - minimum) / 10 && rolls.Max() > maximum - (maximum - minimum) / 10,
                $"chest {chest}: the roll is flat across the range");
        }
        Check.True(GiftPackPolicy.TryResolve(templates, 3877, out var other) && other.BindingGoldMaximum == 0 &&
                   GiftPackPolicy.RollBindingGold(other, new Random(1)) == 0, "other gift packs pay no B-Gold");
    }

    private static Application.Items.IItemTemplateCatalog TreasureChestTemplates() => TestItemContent.Catalog;

    private static void CheckEvidenceEncoding()
    {
        var old = new GiftPackOpenEvidence(7, 14066, 3, "4232:5:0");
        var json = JsonSerializer.Serialize(old);
        Check.True(!json.Contains("BindingGold", StringComparison.Ordinal),
            "evidence without B-Gold keeps its old JSON (stored receipts hash the same)");
        Check.True(JsonSerializer.Deserialize<GiftPackOpenEvidence>(json) == old && old.IsValid,
            "old evidence round-trips");
        var paid = old with { BindingGold = 12_345 };
        var back = JsonSerializer.Deserialize<GiftPackOpenEvidence>(JsonSerializer.Serialize(paid));
        Check.True(back == paid && back!.BindingGold == 12_345 && paid.IsValid, "B-Gold evidence round-trips");
        Check.True(!(old with { BindingGold = -1 }).IsValid, "negative B-Gold evidence is invalid");
    }
}
