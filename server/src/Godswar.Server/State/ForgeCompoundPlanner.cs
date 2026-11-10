using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

// FORGEGEMS: the forge window's "Gems" tab (material compound, tab 1) and
// "Comp" tab (equipment compound, tab 2).
//
// Rules come from the 2008 GameServer source this server line descends from,
// GodsWar-1.0/Server-Source/server-source/GameServer/GameCore/EquipForge.cpp:
//   Forge(type 1) -> ForgeMaterial(): six staged materials, result is the
//     first material's BijouForge NextID, stack 1, bound when any input is
//     bound, each staged unit consumed once, always Success, Type 2.
//   Forge(type 2) -> ForgeEquip(): main gear in forge slot 0 and supporting
//     gear in slots 3, 4 and 5 (all required). Attribute count, quality and
//     grade are each (sum of the four + 1) / 4 with the remainder as a chance
//     to round up (rand(9) < remainder * 10). The main item's appended
//     attributes are re-created at random; every supporting item is consumed;
//     always Success, Type 3.
// The IGG guide (Development/reference-analysis/text/igg-acid=95.txt, "Material
// Compound" and "Equipment Compound") describes the same behaviour: six gems
// of one level -> one of the next level, crystals cannot be compounded,
// supporting gear at most 5 levels below the main gear, green stats re-rolled,
// better inputs give better results.
//
// 2.51 adaptations (not in the 2008 code; called out in the README). Items
// marked "generated" are invented here, not retail rules:
//   - Quality 1-20 and grade 1-25 (the 2.51 client's own checks at 0x424730).
//   - generated: Attribute Stone attributes are kept (default; review fix
//     2026-11-03). The Gear Enhancer (GearEnhancementPlanner.TryAdd) puts a
//     stone into the FIRST empty slot of 1-5 with AttributeLevel = 1 and Quartz
//     Plates raise that level; drops and the loot roller never set a level. So
//     a slot whose AttributeLevel is set is a stone slot, wherever it sits: it
//     keeps its id and level. Every other slot of 1-5 is cleared and up to 4
//     greens are re-rolled into the free slots (lowest first), one per stat
//     type from item_allowed_attributes (like the loot roller), never from a
//     kept stone's chain or stat type (so a later Quartz enhance cannot become
//     ambiguous). The stone does not count against the re-rolled number.
//     GODSWAR_FORGE_COMPOUND_KEEP_STONES=0 selects the 2008 rule instead
//     (ForgeEquip -> ClearAppendAttribute): all five slots and their levels
//     are cleared and re-rolled. Which rule is retail is the admin's decision.
//     Class/elemental attributes, sockets, Holy Suit code and item EXP are kept.
//   - generated: refuse the Comp tab when the main item has no attribute
//     pool (instead of wiping its green stats); refuse when boxes 1/2 hold an
//     item (handler); pet items (types 8/9) are left out of the Gems tab.
//   - generated (follows the client display): the result is bound when any of the four items is bound (the 2.51
//     client's local apply at 0x424cf0 does this; the 2008 line that meant to
//     do it has a bug and never binds).
//   - Gems: all six units must be the same item id (the 2.51 client refuses
//     anything else at 0x4238d0) and the result must be a published item.
internal enum ForgeCompoundStatus
{
    Succeeded = 0,
    CharacterNotFound = 1,
    InvalidSelection = 2,
    StaleSelection = 3,
    NotCompoundable = 4,
    MixedMaterials = 5,
    TargetUnpublished = 6,
    InsufficientCapacity = 7,
    NotEquipment = 8,
    LevelGap = 9,
    NoAttributePool = 10,
    Disabled = 11
}

internal readonly record struct ForgeCompoundSelection(
    int KitBagSlot,
    CompactItemEntry ExpectedItem);

internal sealed record ForgeCompoundSlotMutation(
    int Slot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record ForgeCompoundPlan(
    ForgeCompoundStatus Status,
    string Reason,
    IReadOnlyList<ForgeCompoundSlotMutation> Mutations,
    CompactItemEntry Result)
{
    public bool Committed => Status == ForgeCompoundStatus.Succeeded;

    public static ForgeCompoundPlan Reject(ForgeCompoundStatus status, string reason) =>
        new(status, reason, [], CompactItemEntry.Empty);
}

/// <summary>One attribute an item may carry (item_allowed_attributes).</summary>
internal readonly record struct ForgeCompoundAttributeOption(short AttributeId, short StatType);

internal static class ForgeCompoundPlanner
{
    public const int GemUnitCount = 6;
    public const int KitBagSlotCount = 96;
    public const short MaximumQuality = 20;
    public const short MaximumGrade = 25;
    public const int MaximumRerolledAttributes = 4;
    public const int MaximumLevelGap = 5;
    public const int AttributeSlotCount = 5;

    /// <summary>
    /// generated: GODSWAR_FORGE_COMPOUND_KEEP_STONES=0 switches the Comp tab to
    /// the 2008 rule (all five appended slots re-rolled, stones included).
    /// </summary>
    public static bool KeepStoneAttributes { get; } =
        Environment.GetEnvironmentVariable("GODSWAR_FORGE_COMPOUND_KEEP_STONES") != "0";

    /// <summary>
    /// An Attribute Stone slot: the Gear Enhancer writes AttributeLevel (1-5)
    /// with the stone; drops and the loot roller leave it null. Stone ids alone
    /// cannot tell (161 of 169 item_allowed_attributes ids are in a stone chain).
    /// </summary>
    public static bool IsStoneAttributeSlot(int? attribute, short? level) =>
        attribute.HasValue && level.HasValue;

    /// <summary>
    /// BijouForge MaterialType values that the Gems tab compounds: 1 ruby,
    /// 2 sapphire, 3 emerald, 6 amethyst L1/L2 and the event items (mooncakes,
    /// Ghost). 4 (crystal) has no NextID and is refused like the guide says;
    /// 8/9 (pet items) are left out because no guide text covers them.
    /// </summary>
    public static bool IsCompoundableMaterialType(int materialType) =>
        materialType is 1 or 2 or 3 or 6 ||
        // FINALISE2 (F-109, the admin: "the window offers it: build it"): 8/9 are the pet waters, Spring Water 10107 ->
        // Juice of Rebirth 10145 and Spring Water (Restricted) 11010 -> 10146 (BijouForge NextID), six for one.
        materialType is 8 or 9 && Godswar.Server.Application.Finalise2.Finalise2Rates.On(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.PetItems);

    private static readonly HashSet<string> EquipmentKinds = new(StringComparer.Ordinal)
    {
        "weapon", "shield", "armor", "cloth", "head", "cuff", "leggins",
        "glove", "shoes", "girdle", "amulet", "ring"
    };

    public static bool IsEquipmentKind(string? kind) =>
        kind is not null && EquipmentKinds.Contains(kind);

    /// <summary>Plans six staged gem units into one gem of the next level.</summary>
    public static ForgeCompoundPlan PlanGemCompound(
        IItemTemplateCatalog templates,
        IReadOnlyList<CompactItemEntry> kitBag,
        IReadOnlyList<ForgeCompoundSelection> units)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(kitBag);
        if (units is null || units.Count != GemUnitCount)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.InvalidSelection,
                "Six gems must be staged.");
        }

        if (units.Any(static unit => unit.KitBagSlot is < 0 or >= KitBagSlotCount) ||
            kitBag.Count < KitBagSlotCount)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.InvalidSelection,
                "A staged gem used an invalid bag slot.");
        }

        var materialId = units[0].ExpectedItem.Id;
        if (units.Any(unit => unit.ExpectedItem.IsEmpty || unit.ExpectedItem.Id != materialId))
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.MixedMaterials,
                "All six gems must be the same item.");
        }

        if (!ForgingMaterialRuleCatalog.TryGet(materialId, out var rule) ||
            !IsCompoundableMaterialType(rule.MaterialType) ||
            rule.NextItemId is not { } nextId ||
            nextId == 0)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.NotCompoundable,
                $"Item {materialId} cannot be compounded.");
        }

        if (!templates.TryGet(nextId, out _))
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.TargetUnpublished,
                $"Compound result {nextId} is not a published item.");
        }

        var working = kitBag.Take(KitBagSlotCount).ToArray();
        var bound = false;
        foreach (var group in units.GroupBy(static unit => unit.KitBagSlot))
        {
            var current = working[group.Key];
            var expected = group.First().ExpectedItem;
            if (group.Any(unit => unit.ExpectedItem != expected) ||
                !SameItem(current, expected))
            {
                return ForgeCompoundPlan.Reject(
                    ForgeCompoundStatus.StaleSelection,
                    "A staged gem changed after it was staged.");
            }

            var needed = group.Count();
            if (current.Stack < needed)
            {
                return ForgeCompoundPlan.Reject(
                    ForgeCompoundStatus.StaleSelection,
                    "A staged stack holds fewer gems than were staged from it.");
            }

            bound |= current.Bound != 0;
            working[group.Key] = current.Stack == needed
                ? CompactItemEntry.Empty
                : current with { Stack = checked((short)(current.Stack - needed)) };
        }

        var output = CompactItemEntry.Empty with
        {
            Id = nextId,
            Quality = 1,
            Grade = 1,
            Bound = bound ? (short)1 : (short)0,
            Stack = 1
        };
        if (!TryPlace(ResolveStackCapOrOne(templates.Materials, nextId), working, output))
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.InsufficientCapacity,
                "The compounded gem does not fit in the bag.");
        }

        return new ForgeCompoundPlan(
            ForgeCompoundStatus.Succeeded,
            string.Empty,
            Diff(kitBag, working),
            output);
    }

    /// <summary>
    /// Plans the equipment compound: <paramref name="main"/> is forge slot 0,
    /// <paramref name="supporting"/> are forge slots 3, 4 and 5.
    /// <paramref name="random"/> returns 0..max-1 (RandomNumberGenerator.GetInt32).
    /// </summary>
    public static ForgeCompoundPlan PlanEquipmentCompound(
        IItemTemplateCatalog templates,
        IReadOnlyList<CompactItemEntry> kitBag,
        ForgeCompoundSelection main,
        IReadOnlyList<ForgeCompoundSelection> supporting,
        IReadOnlyList<ForgeCompoundAttributeOption> attributePool,
        Func<int, int> random,
        bool keepStoneAttributes = true)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(kitBag);
        ArgumentNullException.ThrowIfNull(random);
        if (supporting is null || supporting.Count != 3 || kitBag.Count < KitBagSlotCount)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.InvalidSelection,
                "Main gear and three supporting items must be staged.");
        }

        var all = new[] { main }.Concat(supporting).ToArray();
        if (all.Any(static selection => selection.KitBagSlot is < 0 or >= KitBagSlotCount) ||
            all.Select(static selection => selection.KitBagSlot).Distinct().Count() != all.Length)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.InvalidSelection,
                "Each staged gear piece must be a different bag slot.");
        }

        var items = new CompactItemEntry[all.Length];
        var levels = new int[all.Length];
        for (var index = 0; index < all.Length; index++)
        {
            var current = kitBag[all[index].KitBagSlot];
            if (!SameItem(current, all[index].ExpectedItem))
            {
                return ForgeCompoundPlan.Reject(
                    ForgeCompoundStatus.StaleSelection,
                    "A staged gear piece changed after it was staged.");
            }

            if (!templates.TryGet(current.Id, out var template) ||
                !IsEquipmentKind(template.Kind))
            {
                return ForgeCompoundPlan.Reject(
                    ForgeCompoundStatus.NotEquipment,
                    $"Item {current.Id} is not equipment.");
            }

            items[index] = current;
            levels[index] = template.MinLevel ?? 0;
        }

        for (var index = 1; index < items.Length; index++)
        {
            if (levels[index] < levels[0] - MaximumLevelGap)
            {
                return ForgeCompoundPlan.Reject(
                    ForgeCompoundStatus.LevelGap,
                    "Supporting gear is more than 5 levels below the main gear.");
            }
        }

        var slotPool = (attributePool ?? [])
            .Where(static option => IsSlotAttribute(option.AttributeId))
            .ToArray();
        if (slotPool.Length == 0)
        {
            return ForgeCompoundPlan.Reject(
                ForgeCompoundStatus.NoAttributePool,
                $"Item {items[0].Id} has no attribute pool; its green stats are left alone.");
        }

        // Stone slots (AttributeLevel set) keep id and level when keepStoneAttributes.
        var attributes = new int?[]
        {
            items[0].Attribute1, items[0].Attribute2, items[0].Attribute3,
            items[0].Attribute4, items[0].Attribute5
        };
        var attributeLevels = new short?[]
        {
            items[0].AttributeLevel1, items[0].AttributeLevel2, items[0].AttributeLevel3,
            items[0].AttributeLevel4, items[0].AttributeLevel5
        };
        var excludedIds = new HashSet<int>();
        var keptCount = 0;
        for (var slot = 0; slot < AttributeSlotCount; slot++)
        {
            if (keepStoneAttributes &&
                IsStoneAttributeSlot(attributes[slot], attributeLevels[slot]))
            {
                keptCount++;
                var id = attributes[slot]!.Value;
                excludedIds.Add(id);
                foreach (var stone in templates.Materials.AttributeStones)
                {
                    if (stone.AllowedAttributeIds.Contains(id))
                    {
                        excludedIds.UnionWith(stone.AllowedAttributeIds);
                    }
                }

                continue;
            }

            attributes[slot] = null;
            attributeLevels[slot] = null;
        }

        var excludedStatTypes = slotPool
            .Where(option => excludedIds.Contains(option.AttributeId))
            .Select(static option => option.StatType)
            .ToHashSet();
        var groups = slotPool
            .Where(option => !excludedIds.Contains(option.AttributeId) &&
                             !excludedStatTypes.Contains(option.StatType))
            .GroupBy(static option => option.StatType)
            .Select(static group => group.Select(static option => option.AttributeId).Distinct().ToArray())
            .ToArray();

        var attributeCount = AverageRoundUp(items.Sum(CountAttributes), random);
        attributeCount = Math.Min(
            attributeCount,
            Math.Min(
                Math.Min(MaximumRerolledAttributes, AttributeSlotCount - keptCount),
                groups.Length));
        var quality = (short)Math.Clamp(
            AverageRoundUp(items.Sum(static item => Math.Max(1, (int)item.Quality)), random),
            1,
            MaximumQuality);
        var grade = (short)Math.Clamp(
            AverageRoundUp(items.Sum(static item => Math.Max(1, (int)item.Grade)), random),
            1,
            MaximumGrade);

        // One attribute per stat type: partial Fisher-Yates over the groups,
        // written into the free slots, lowest first.
        var order = Enumerable.Range(0, groups.Length).ToArray();
        var freeSlot = 0;
        for (var pick = 0; pick < attributeCount; pick++)
        {
            var swap = pick + random(order.Length - pick);
            (order[pick], order[swap]) = (order[swap], order[pick]);
            var ids = groups[order[pick]];
            while (attributes[freeSlot].HasValue)
            {
                freeSlot++;
            }

            attributes[freeSlot] = ids[random(ids.Length)];
            attributeLevels[freeSlot] = null;
        }

        var bound = items.Any(static item => item.Bound != 0);
        var result = items[0] with
        {
            Attribute1 = attributes[0],
            Attribute2 = attributes[1],
            Attribute3 = attributes[2],
            Attribute4 = attributes[3],
            Attribute5 = attributes[4],
            AttributeLevel1 = attributeLevels[0],
            AttributeLevel2 = attributeLevels[1],
            AttributeLevel3 = attributeLevels[2],
            AttributeLevel4 = attributeLevels[3],
            AttributeLevel5 = attributeLevels[4],
            Quality = quality,
            Grade = grade,
            Bound = bound ? (short)1 : items[0].Bound
        };

        var mutations = new List<ForgeCompoundSlotMutation>
        {
            new(all[0].KitBagSlot, items[0], result)
        };
        for (var index = 1; index < items.Length; index++)
        {
            mutations.Add(new(all[index].KitBagSlot, items[index], CompactItemEntry.Empty));
        }

        return new ForgeCompoundPlan(
            ForgeCompoundStatus.Succeeded,
            string.Empty,
            mutations,
            result);
    }

    /// <summary>
    /// 2008 ForgeEquip averaging: (sum + 1) / 4, and the dropped quarter is a
    /// chance to add one: rand(9) &lt; remainder * 10 (0, 2, 5 or 7 out of 9).
    /// </summary>
    public static int AverageRoundUp(int sum, Func<int, int> random)
    {
        var whole = (sum + 1) / 4;
        var spare = (sum + 1) % 4 * 10 / 4;
        return random(9) < spare ? whole + 1 : whole;
    }

    public static int CountAttributes(CompactItemEntry item) =>
        (item.Attribute1.HasValue ? 1 : 0) +
        (item.Attribute2.HasValue ? 1 : 0) +
        (item.Attribute3.HasValue ? 1 : 0) +
        (item.Attribute4.HasValue ? 1 : 0) +
        (item.Attribute5.HasValue ? 1 : 0);

    /// <summary>Class (200-231) and elemental (480-500) ids never go in slots 1-5.</summary>
    public static bool IsSlotAttribute(short attributeId) =>
        attributeId >= 0 &&
        attributeId is not (>= 200 and <= 231) &&
        attributeId is not (>= 480 and <= 500);

    public static bool SameItem(CompactItemEntry actual, CompactItemEntry expected) =>
        !actual.IsEmpty &&
        actual.Id == expected.Id &&
        actual.Quality == expected.Quality &&
        actual.Grade == expected.Grade &&
        actual.Bound == expected.Bound &&
        actual.Stack == expected.Stack;

    /// <summary>
    /// Items without a pinned material definition (amethyst L2/L3, mooncake and
    /// Ghost results) have no known stack cap: they only go into an empty slot.
    /// </summary>
    private static int ResolveStackCapOrOne(IItemMaterialCatalog materials, uint itemId)
    {
        try
        {
            return materials.ResolveStackCap(itemId);
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    private static bool TryPlace(int stackCap, CompactItemEntry[] working, CompactItemEntry output)
    {
        stackCap = Math.Max(1, stackCap);
        for (var slot = 0; slot < working.Length; slot++)
        {
            var item = working[slot];
            if (!item.IsEmpty &&
                item.Id == output.Id &&
                item.Bound == output.Bound &&
                item.Stack < stackCap)
            {
                working[slot] = item with { Stack = checked((short)(item.Stack + 1)) };
                return true;
            }
        }

        for (var slot = 0; slot < working.Length; slot++)
        {
            if (working[slot].IsEmpty)
            {
                working[slot] = output;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ForgeCompoundSlotMutation> Diff(
        IReadOnlyList<CompactItemEntry> before,
        CompactItemEntry[] after)
    {
        var mutations = new List<ForgeCompoundSlotMutation>();
        for (var slot = 0; slot < after.Length; slot++)
        {
            if (before[slot] != after[slot])
            {
                mutations.Add(new(slot, before[slot], after[slot]));
            }
        }

        return mutations;
    }
}
