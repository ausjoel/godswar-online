using System.Text.Json;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Items;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// NEXT (Development/server-next): the Olympian potions with the percent heal
// (a01/a08/a23) and the MSG_TIME_ITEM frames of item expiry (b03).
internal static class ServerNextChecks
{
    public const string PotionCheckName = "NEXT Olympian potions: 3x curve, tier IV heals 25% of max (capped), 1 s cooldown";
    public const string ExpiryCheckName = "NEXT item expiry: MSG_TIME_ITEM 10054 frames and timer diff";
    public const string MedalCheckName = "NEXT b06 Rank IV badge 4193 is charged in medals at the Point Exchanger";

    // The same assertion sits at the end of CapitalNpcServiceProtocolChecks,
    // which fails earlier at baseline (Sparta_089), so it is repeated here.
    public static Task RunMedalAsync()
    {
        Check.True(
            Godswar.Server.Domain.World.Content.CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(4193, out var badge) &&
            badge == Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Medal,
            "4193 (Rank IV badge) is a medal item");
        foreach (var (item, expected) in new (uint, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency)[]
                 {
                     (9958, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Medal),
                     (9959, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Medal),
                     (14069, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Medal),
                     (9940, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Honor),
                     (3931, Godswar.Server.Domain.World.Content.CapitalNpcShopCurrency.Point)
                 })
        {
            Check.True(
                Godswar.Server.Domain.World.Content.CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(item, out var actual) &&
                actual == expected,
                $"item {item} keeps its currency");
        }
        Check.True(!Godswar.Server.Domain.World.Content.CapitalNpcServiceProtocol.TryGetPointExchangerCurrency(4194, out _),
            "4194 is not in the list (falls back to the tab currency)");
        return Task.CompletedTask;
    }

    private static readonly (uint Item, int Skill, string Tag, BagConsumableEffectKind Kind, int Amount, int Percent)[] Potions =
    [
        (4017, 3109, "HPPotion_n", BagConsumableEffectKind.RestoreHitPoints, 10_100, 0),
        (4018, 3110, "HPPotion_o", BagConsumableEffectKind.RestoreHitPoints, 14_600, 0),
        (4019, 3111, "HPPotion_p", BagConsumableEffectKind.RestoreHitPoints, 21_000, 0),
        (4020, 3112, "HPPotion_q", BagConsumableEffectKind.RestoreHitPoints, 21_000, 2_500),
        (4044, 3129, "MPPotion_k", BagConsumableEffectKind.RestoreManaPoints, 4_000, 0),
        (4045, 3130, "MPPotion_l", BagConsumableEffectKind.RestoreManaPoints, 5_800, 0),
        (4046, 3131, "MPPotion_m", BagConsumableEffectKind.RestoreManaPoints, 8_400, 0),
        (4047, 3132, "MPPotion_n", BagConsumableEffectKind.RestoreManaPoints, 8_400, 2_500)
    ];

    public static Task RunPotionsAsync()
    {
        var seeds = ClientCatalogItemContentBaseline.ItemTemplates.ToDictionary(static s => s.Id);
        var definitions = new List<ItemTemplateDefinition>();
        foreach (var potion in Potions)
        {
            Check.True(seeds.TryGetValue((int)potion.Item, out var seed), $"item {potion.Item} is a published client-catalog seed");
            Check.Equal("consume item", seed!.Kind, $"item {potion.Item} kind");
            Check.Equal(potion.Tag, seed.NameKey, $"item {potion.Item} tag");
            using var stats = JsonDocument.Parse(seed.StatsJson);
            Check.Equal(potion.Skill.ToString(), stats.RootElement.GetProperty("Skill").GetString()!, $"item {potion.Item} skill");
            Check.Equal("99", stats.RootElement.GetProperty("Overlap").GetString()!, $"item {potion.Item} stacks to 99");
            Check.True(!stats.RootElement.TryGetProperty("BindType", out _), $"item {potion.Item} is tradeable (no BindType)");
            definitions.Add(new ItemTemplateDefinition(potion.Item, seed.Kind, seed.NameKey, seed.DisplayName, 0, [], null, null,
                null, null, seed.Texture, seed.Icon, seed.StatsJson));

            Check.True(BagConsumableEffectCatalog.TryResolve(potion.Skill, out var effect), $"skill {potion.Skill} resolves");
            Check.True(effect.Kind == potion.Kind && effect.Amount == potion.Amount &&
                effect.MaxPercentBasisPoints == potion.Percent, $"skill {potion.Skill} effect is {potion.Kind} {potion.Amount} / {potion.Percent} bp");
            Check.True(PotionUsePolicy.IsReviewedPotion(effect), $"skill {potion.Skill} is a reviewed potion");
        }

        var templates = PinnedItemTemplateCatalog.Create("next-potions", definitions, [], [], []);
        foreach (var potion in Potions)
        {
            Check.True(PotionUsePolicy.TryResolve(templates, potion.Item, out var resolved) && resolved.SkillId == potion.Skill,
                $"item {potion.Item} resolves through its template to skill {potion.Skill}");
            Check.True(BagConsumableCooldownPolicy.TryResolve(templates, potion.Item, out var cooldown) &&
                cooldown.Duration == TimeSpan.FromSeconds(1), $"item {potion.Item} has the one-second potion cooldown");
        }

        BagConsumableEffectCatalog.TryResolve(3112, out var lifeNectar);
        BagConsumableEffectCatalog.TryResolve(3132, out var mindNectar);
        BagConsumableEffectCatalog.TryResolve(3111, out var healThree);
        // 25% of 40,000 = 10,000 (under the 21,000 cap).
        Check.Equal(10_000, Applied(new(1, 5_000, 40_000, 10_000, 3), lifeNectar), "tier IV heals 25% of max HP");
        // 25% of 200,000 = 50,000, capped at the tier III amount.
        Check.Equal(21_000, Applied(new(1, 5_000, 200_000, 10_000, 3), lifeNectar), "tier IV is capped at 21,000 HP");
        Check.Equal(21_000, Applied(new(1, 5_000, 200_000, 10_000, 3), healThree), "tier III heals 21,000 HP");
        // Only the missing HP is restored.
        Check.Equal(1_000, Applied(new(39_000, 5_000, 40_000, 10_000, 3), lifeNectar), "tier IV restores only the missing HP");
        // Rounds down: 25% of 10,001 MP = 2,500.
        Check.Equal(2_500, Applied(new(1_000, 1, 40_000, 10_001, 3), mindNectar), "tier IV heals 25% of max MP, rounded down");
        Check.Equal(8_400, Applied(new(1_000, 0, 40_000, 100_000, 3), mindNectar), "tier IV is capped at 8,400 MP");
        Check.True(!PotionUsePolicy.TryPlan(new(40_000, 5_000, 40_000, 10_000, 3), lifeNectar, out _), "full HP: no use");
        Check.Equal(12_345, PotionUsePolicy.ResolveRestoreCap(healThree with { Amount = 12_345 }, 1_000_000),
            "flat potions keep their stock amount");
        Check.True(!(lifeNectar with { MaxPercentBasisPoints = 10_001 }).IsValid, "more than 100% is rejected");
        Check.True(!new BagConsumableEffect(4600, BagConsumableEffectKind.GrantSilver, 10, MaxPercentBasisPoints: 100).IsValid,
            "a percentage only applies to HP/MP restoration");
        return Task.CompletedTask;
    }

    private static int Applied(PotionVitalsSnapshot before, BagConsumableEffect effect)
    {
        Check.True(PotionUsePolicy.TryPlan(before, effect, out var plan), "valid potion plan");
        return plan!.AppliedAmount;
    }

    public static Task RunExpiryAsync()
    {
        // Golden frame from the Origin capture (gw-server-go_myproxy.jsonl):
        // equipment, page 0, index 12, 2026-03-16 05:30:06 (Monday, yday 74).
        var golden = Convert.FromHexString(
            "34004627" +
            "00000000000000000c000000060000001e0000000500000010000000020000007e000000010000004a000000ffffffff");
        var frame = PacketBuilder.TimeItem(TimeItemContainer.Equipment, 0, 12, new DateTime(2026, 3, 16, 5, 30, 6));
        Check.True(frame.AsSpan().SequenceEqual(golden), "MSG_TIME_ITEM equals the captured Origin frame");

        var clear = PacketBuilder.TimeItem(TimeItemContainer.KitBag, 2, 5, null);
        Check.Equal(52, clear.Length, "clear frame length");
        Check.True(clear.AsSpan(16).ToArray().All(static b => b == 0), "clear frame carries an all-zero tm");
        Check.Equal(1, BitConverter.ToInt32(clear, 4), "kit bag container");
        Check.Equal(2, BitConverter.ToInt32(clear, 8), "kit bag page");
        Check.Equal(5, BitConverter.ToInt32(clear, 12), "kit bag index");
        Check.Equal((2, 5), PacketBuilder.TimeItemKitBagAddress(53), "bag slot 53 = page 2, index 5");

        var utc = new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);
        var map = GameClientHandler.BuildItemTimerMap(
            [
                new TimedItemSlot(ItemExpiryRuntime.LocationEquipment, 12, 8040, utc),
                new TimedItemSlot(ItemExpiryRuntime.LocationKitBag, 30, 6026, utc.AddDays(1)),
                new TimedItemSlot(3, 4, 8000, utc) // warehouse: never framed
            ],
            TimeSpan.FromHours(10));
        Check.Equal(2, map.Count, "only equipment and bag timers are framed");
        Check.True(map[(TimeItemContainer.Equipment, 0, 12)] == new DateTime(2026, 11, 10, 0, 0, 0),
            "equipment timer is realm wall-clock time (UTC+10)");
        Check.True(map.ContainsKey((TimeItemContainer.KitBag, 1, 6)), "bag slot 30 = page 1, index 6");

        var sent = new Dictionary<(TimeItemContainer, int, int), DateTime>(map);
        Check.Equal(0, GameClientHandler.PlanItemTimerFrames(sent, map).Count, "unchanged timers are not resent");
        var moved = new Dictionary<(TimeItemContainer, int, int), DateTime>
        {
            [(TimeItemContainer.Equipment, 0, 12)] = map[(TimeItemContainer.Equipment, 0, 12)],
            [(TimeItemContainer.KitBag, 0, 0)] = map[(TimeItemContainer.KitBag, 1, 6)]
        };
        var frames = GameClientHandler.PlanItemTimerFrames(sent, moved);
        Check.Equal(2, frames.Count, "a moved item: one new timer and one clear");
        Check.True(frames[0].Address == (TimeItemContainer.KitBag, 0, 0) && frames[0].Expiry is not null, "new slot gets the timer");
        Check.True(frames[1].Address == (TimeItemContainer.KitBag, 1, 6) && frames[1].Expiry is null, "old slot is cleared");
        return Task.CompletedTask;
    }
}
