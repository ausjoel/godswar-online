using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

// ItemId is for a female character; MaleItemId, when set, replaces it for a male one (client SexBind 0 / 1).
// ClassMask: bit per Profession (0 Warrior, 1 Champion, 2 Priest, 3 Mage). Weight applies inside a pick.
// W0 F-111: Appends/AppendLevel = a retail dial record's fixed appended attributes; RetailBound = its Binding.
internal readonly record struct GiftPackReward(uint ItemId, int Quantity, uint MaleItemId = 0, byte ClassMask = 15, int Weight = 1,
    int[]? Appends = null, short AppendLevel = 0, short RetailBound = 0);

internal readonly record struct GiftPackPick(int Count, GiftPackReward[] Options);

// SkillId 0: the client gives the item no skill (boxes). KeyItemId: one is used up and the pack refuses without it.
// BindingGoldMinimum..BindingGoldMaximum (F-150): B-Gold credited on open, a flat roll; 0 = none.
internal sealed record GiftPackDefinition(int SkillId, GiftPackReward[] Rewards, GiftPackPick[]? Picks = null,
    int MinimumLevel = 0, int MaximumLevel = 0, uint KeyItemId = 0, int TitleId = 0,
    int BindingGoldMinimum = 0, int BindingGoldMaximum = 0);

/// <summary>
/// Bag packs whose exact contents are stated in the client's EquipDescription.dat.
/// </summary>
/// <remarks>
/// Only fixed lists are here. Packs with random or unnamed contents are listed for review in
/// Development/consumable-audit/C2-STATUS.md. Shining Copper Ore says only "Copper Ore", and two items have that
/// name: 12010 is the crafting material next to the other decomposed materials 12000/12020/12030/12040.
/// </remarks>
internal static partial class GiftPackPolicy
{
    private static readonly FrozenDictionary<uint, GiftPackDefinition> Packs =
        new Dictionary<uint, GiftPackDefinition>
        {
            // Talent Package I: Talent Stone 1 x20
            [4167] = new(5200, [new(4180, 20)]),
            // Talent Package II: Talent Stone 2 x20
            [4168] = new(5201, [new(4181, 20)]),
            // Talent Package III: Talent Stone 3 x20
            [4169] = new(5202, [new(4182, 20)]),
            // Enhancement Package I: Level 2 Crystal x25 + Magic Healing Potion x25
            [3951] = new(5203, [new(4231, 25), new(4011, 25)]),
            // Gear Improvement Package II: Quartz Plate 1 x1 + Quartz Plate 2 x1 + Quartz Plate 3 x1 + Quartz Plate 4 x1
            [3954] = new(5206, [new(9960, 1), new(9961, 1), new(9962, 1), new(9963, 1)]),
            // Holy Stone Kit: Level 1 Eclipse Stone x7 + Level 2 Eclipse Stone x4 + Goddess' Stone x1
            [3968] = new(5209, [new(9040, 7), new(9041, 4), new(9050, 1)]),
            // Gear Forging Kit: Level 1 Crystal x25 + Level 2 Crystal x15 + Level 3 Crystal x5
            [3970] = new(5211, [new(4230, 25), new(4231, 15), new(4232, 5)]),
            // Greater Gear Forging Kit: Level 1 Crystal x30 + Level 2 Crystal x20 + Level 3 Crystal x10
            [3971] = new(5212, [new(4230, 30), new(4231, 20), new(4232, 10)]),
            // Preferential Firework Box: Firework of Love x10 + Adv. Firework of Love x10 + Super Firework of Love x10
            [3975] = new(5216, [new(3949, 10), new(3942, 10), new(3950, 10)]),
            // Preferential Salute Box: Simple Firework x10 + Adv. Firework x10 + Super Firework x10
            [3976] = new(5217, [new(3939, 10), new(3940, 10), new(3941, 10)]),
            // Greater PvP Kit: Magic Healing Potion x30 + Magic Mana Potion x5 + Blood Potion x10 + Energy Holy Water x3 + Anima Potion x1
            [3980] = new(5221, [new(4011, 30), new(4041, 5), new(4510, 10), new(4062, 3), new(4511, 1)]),
            // Artemis Gift Pack I: Delicate Mooncake x10
            [14022] = new(5535, [new(3920, 10)]),
            // Artemis Gift Pack II: Delicate Mooncake x15
            [14023] = new(5536, [new(3920, 15)]),
            // Artemis Gift Pack III: Delicate Mooncake x25
            [14024] = new(5537, [new(3920, 25)]),
            // Artemis Gift Pack IV: Delicate Mooncake x35
            [14025] = new(5538, [new(3920, 35)]),
            // Artemis Gift Pack V: Delicate Mooncake x50
            [14026] = new(5539, [new(3920, 50)]),
            // 1-3 Star VIP Xmas Package: Antler x100
            [14038] = new(5547, [new(4005, 100)]),
            // 4 Star VIP Xmas Package: Antler x200
            [14039] = new(5548, [new(4005, 200)]),
            // 5 Star VIP Xmas Package: Antler x300
            [14040] = new(5549, [new(4005, 300)]),
            // Aphrodite Blessed Pack, Adv. Pack, Wedding Suit: fireworks and the dress the item text names.
            [3937] = new(5126, [new(3942, 1), new(3949, 1), new(3939, 1), new(3940, 1), new(8227, 1, 8257)]),
            [3938] = new(5127, [new(3942, 2), new(3949, 2), new(3950, 2), new(3941, 2), new(8228, 1, 8258)]),
            [3972] = new(5213, [new(8220, 1, 8250), new(3941, 1)]),
            // Holy Stone Package: Heated Holy Stone x2 + Cooled Holy Stone x3 + Level 1 Eclipse Stone x10 + Level 2 Eclipse Stone x5 + Goddess' Stone x1
            [14054] = new(5554, [new(9030, 2), new(9031, 3), new(9040, 10), new(9041, 5), new(9050, 1)]),
            // Gorgeous Hard Leather: Hard Leather x99
            [12101] = new(5163, [new(12000, 99)]),
            // Shining Copper Ore: Copper Ore x99
            [12102] = new(5164, [new(12010, 99)]),
            // Complete Divine Fire: Divine Fire x99
            [12103] = new(5165, [new(12020, 99)]),
            // Complete Herb: Herb x99
            [12104] = new(5160, [new(12030, 99)]),
            // Perfect Raw Gem: Raw Gem x99
            [12105] = new(5161, [new(12040, 99)]),
        }.Concat(GeneratedPacks).Concat(Finalise2Packs) // FINALISE2: + Finalise2Packs
            .Select(WithSealerChestBindingGold).ToFrozenDictionary(); // F-150: Sacred Sealer chests pay B-Gold

    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out GiftPackDefinition pack)
    {
        if (!(Packs.TryGetValue(itemId, out pack!) && (pack.SkillId == 0 ? templates.TryGet(itemId, out _) :
                BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) && skill == pack.SkillId)))
            return false;
        // W0 F-111: keyed boxes follow the box.dial_contents rate (0 today's, 1 retail dial, 2 combined).
        if (pack.KeyItemId != 0) pack = GiftPackDialPools.Apply(itemId, pack, templates, GiftPackDialPools.Mode);
        return true;
    }

    // Rolled once per open; the receipt stores the result. Repeats are allowed within a pick.
    public static IEnumerable<GiftPackReward> RollRewards(GiftPackDefinition pack, int profession)
    {
        bool Fits(GiftPackReward reward) => (reward.ClassMask >> profession & 1) != 0;
        foreach (var reward in pack.Rewards.Where(Fits)) yield return reward;
        foreach (var pick in pack.Picks ?? [])
        {
            var options = pick.Options.Where(Fits).ToArray();
            var total = options.Sum(option => option.Weight);
            for (var n = 0; n < pick.Count && total > 0; n++)
            {
                var roll = Random.Shared.Next(total);
                foreach (var option in options)
                {
                    roll -= option.Weight;
                    if (roll >= 0) continue;
                    yield return option;
                    break;
                }
            }
        }
    }
}
