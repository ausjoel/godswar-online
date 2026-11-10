using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// FORGEGEMS: forge window Gems (tab 1) and Comp (tab 2) tabs, the S2C 10109
// compound reply and the Trans-tab preview pick hook.
internal static class ForgeCompoundChecks
{
    public const string CheckName = "Forgegems Gems/Comp tab planners, 10109 compound reply and Trans pick";

    private static readonly IReadOnlyList<ForgeCompoundAttributeOption> GlovePool =
    [
        new(0, 0), new(1, 0), new(20, 2), new(21, 2), new(40, 4), new(60, 6), new(205, 30)
    ];

    public static Task RunAsync()
    {
        var templates = TestItemContent.Catalog;

        Check.True(GameClientHandler.IsForgeCompoundTabEnabled(1), "Gems tab on with GODSWAR_FORGE_GEM_COMPOUND=1 (RELEASE: opt-in; set by Program.Main)");
        Check.True(GameClientHandler.IsForgeCompoundTabEnabled(2), "Comp tab on with GODSWAR_FORGE_EQUIP_COMPOUND=1 (RELEASE: opt-in; set by Program.Main)");
        Check.True(!GameClientHandler.IsForgeCompoundTabEnabled(0), "tab 0 stays with the ordinary forge");
        Check.True(!GameClientHandler.IsForgeCompoundTabEnabled(3), "tab 3 stays with the amethyst transform");

        // Gems: six units of one stack (the client's Fill button).
        Check.True(templates.TryGet(4211, out _) && templates.TryGet(2800, out _) && templates.TryGet(2801, out _),
            "fixture publishes 4211, 2800 and 2801 (so the branches below run)");
        var bag = Bag((0, Gem(4210, 6)));
        var plan = ForgeCompoundPlanner.PlanGemCompound(templates, bag, Units((0, 6, Gem(4210, 6))));
        if (templates.TryGet(4211, out _))
        {
            Check.True(plan.Committed, "6 x L1 Sapphire compounds");
            Check.Equal(4211u, plan.Result.Id, "result is BijouForge NextID 4211");
            Check.Equal((short)1, plan.Result.Stack, "one gem is made");
            Check.Equal(1, plan.Mutations.Count, "the emptied slot receives the new gem");
            Check.Equal(4211u, plan.Mutations[0].After.Id, "slot 0 now holds 4211");

            // Two stacks, one bound: the result is bound; both stacks shrink.
            bag = Bag((3, Gem(4210, 5)), (7, Gem(4210, 2, bound: 1)), (9, Gem(4211, 1)));
            plan = ForgeCompoundPlanner.PlanGemCompound(
                templates, bag, Units((3, 4, Gem(4210, 5)), (7, 2, Gem(4210, 2, bound: 1))));
            Check.True(plan.Committed, "mixed stacks compound");
            Check.Equal((short)1, plan.Result.Bound, "any bound input binds the result");
            Check.Equal((short)1, Find(plan, 3).After.Stack, "four taken from the 5-stack");
            Check.True(Find(plan, 7).After.IsEmpty, "both taken from the bound 2-stack");
            Check.True(plan.Mutations.All(static m => m.Slot != 9), "unbound 4211 stack is not merged with a bound result");

            // Bag full apart from the gems themselves.
            var full = Enumerable.Range(0, 96).Select(static _ => Gear(1000)).ToArray();
            full[5] = Gem(4210, 7);
            plan = ForgeCompoundPlanner.PlanGemCompound(templates, full, Units((5, 6, Gem(4210, 7))));
            Check.True(plan.Status == ForgeCompoundStatus.InsufficientCapacity, "no room for the result");
            Check.Equal(0, plan.Mutations.Count, "a refusal changes nothing");
        }

        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(4210, 5))), Units((0, 6, Gem(4210, 5))));
        Check.True(plan.Status == ForgeCompoundStatus.StaleSelection, "six units from a stack of five are refused");
        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(4230, 6))), Units((0, 6, Gem(4230, 6))));
        Check.True(plan.Status == ForgeCompoundStatus.NotCompoundable, "crystals cannot be compounded");
        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(4252, 6))), Units((0, 6, Gem(4252, 6))));
        Check.True(plan.Status == ForgeCompoundStatus.NotCompoundable, "L3 amethyst has no NextID");
        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(10107, 6))), Units((0, 6, Gem(10107, 6))));
        // FINALISE2 (F-109): pet waters (type 8/9) compound like gems while item.finalise2_pet_items is 1 (default).
        // The test catalogue does not hold Juice of Rebirth 10145, so the planner stops at TargetUnpublished; the
        // full compound is covered on the p4 rig (test_p4_more.py).
        Check.True(plan.Status is ForgeCompoundStatus.Succeeded or ForgeCompoundStatus.TargetUnpublished,
            "pet waters (type 8) are compoundable now");
        plan = ForgeCompoundPlanner.PlanGemCompound(
            templates, Bag((0, Gem(4210, 3)), (1, Gem(4220, 3))), Units((0, 3, Gem(4210, 3)), (1, 3, Gem(4220, 3))));
        Check.True(plan.Status == ForgeCompoundStatus.MixedMaterials, "sapphire and emerald do not mix");
        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(4210, 6))), Units((0, 5, Gem(4210, 6))));
        Check.True(plan.Status == ForgeCompoundStatus.InvalidSelection, "five units are refused");
        plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(4210, 7))), Units((0, 6, Gem(4210, 6))));
        Check.True(plan.Status == ForgeCompoundStatus.StaleSelection, "a stack that changed since staging is refused");
        foreach (var (source, next) in new (uint, uint)[] { (4250, 4251), (4251, 4252), (3800, 3806), (4105, 4107), (4200, 4201), (4220, 4221) })
        {
            plan = ForgeCompoundPlanner.PlanGemCompound(templates, Bag((0, Gem(source, 6))), Units((0, 6, Gem(source, 6))));
            Check.True(
                templates.TryGet(next, out _)
                    ? plan.Committed && plan.Result.Id == next
                    : plan.Status == ForgeCompoundStatus.TargetUnpublished,
                $"{source} -> {next} follows BijouForge (or is refused when {next} is unpublished)");
        }

        // 2008 averaging: (sum + 1) / 4, remainder*10 vs rand(9).
        Check.Equal(1, ForgeCompoundPlanner.AverageRoundUp(3, static _ => 0), "sum 3 -> exactly 1");
        Check.Equal(2, ForgeCompoundPlanner.AverageRoundUp(4, static _ => 1), "sum 4: rand 1 < 2 rounds up");
        Check.Equal(1, ForgeCompoundPlanner.AverageRoundUp(4, static _ => 2), "sum 4: rand 2 does not");
        Check.Equal(2, ForgeCompoundPlanner.AverageRoundUp(6, static _ => 6), "sum 6: rand 6 < 7 rounds up");
        Check.Equal(5, ForgeCompoundPlanner.AverageRoundUp(19, static _ => 8), "sum 19 -> 5");

        // Comp: main glove 2800 + three supporting gloves.
        var main = Gear(2800, quality: 3, grade: 2, attributes: [0, 20, null, null, 9]);
        bag = Bag(
            (10, main),
            (11, Gear(2800, quality: 1, grade: 1, attributes: [40])),
            (12, Gear(2801, quality: 5, grade: 4, bound: 1)),
            (13, Gear(2800, quality: 2, grade: 1, attributes: [60, 1])));
        var supporting = new[] { Sel(bag, 11), Sel(bag, 12), Sel(bag, 13) };
        plan = ForgeCompoundPlanner.PlanEquipmentCompound(templates, bag, Sel(bag, 10), supporting, GlovePool, static _ => 0);
        if (templates.TryGet(2800, out var glove) && templates.TryGet(2801, out _))
        {
            Check.True(plan.Committed, "four gloves compound");
            Check.Equal(4, plan.Mutations.Count, "main updated, three supporting consumed");
            Check.True(plan.Mutations.Skip(1).All(static m => m.After.IsEmpty), "supporting gear disappears");
            // quality sum 11 -> 12/4 = 3; grade sum 8 -> 9/4 = 2 r1 (rand 0 < 2) -> 3;
            // attributes: 3 + 1 + 0 + 2 = 6 -> 7/4 = 1 r3 (rand 0 < 7) -> 2.
            Check.Equal((short)3, plan.Result.Quality, "quality is the 2008 average");
            Check.Equal((short)3, plan.Result.Grade, "grade is the 2008 average with round-up");
            Check.Equal(2, ForgeCompoundPlanner.CountAttributes(plan.Result), "two re-rolled attributes");
            Check.True(plan.Result.Attribute1.HasValue && plan.Result.Attribute2.HasValue, "re-rolled greens fill slots 1 and 2");
            Check.True(plan.Result.Attribute5 is null, "slot 5 without a level (a green, not a stone) is cleared");
            Check.True(plan.Result.Attribute1 != 205 && plan.Result.Attribute2 != 205, "class attribute ids never land in slots 1-4");
            Check.Equal((short)1, plan.Result.Bound, "a bound supporting item binds the result");
            Check.Equal(2800u, plan.Result.Id, "the main item keeps its id");

            plan = ForgeCompoundPlanner.PlanEquipmentCompound(templates, bag, Sel(bag, 10), supporting, [], static _ => 0);
            Check.True(plan.Status == ForgeCompoundStatus.NoAttributePool, "no pool: refused, greens untouched");

            if (templates.TryGet(2805, out var high) && (high.MinLevel ?? 0) > (glove.MinLevel ?? 0) + 5)
            {
                var gap = Bag((10, Gear(2805)), (11, Gear(2800)), (12, Gear(2800)), (13, Gear(2800)));
                plan = ForgeCompoundPlanner.PlanEquipmentCompound(
                    templates, gap, Sel(gap, 10), [Sel(gap, 11), Sel(gap, 12), Sel(gap, 13)], GlovePool, static _ => 0);
                Check.True(plan.Status == ForgeCompoundStatus.LevelGap, "supporting gear more than 5 levels lower is refused");
            }

            var dup = Bag((10, main), (11, Gear(2800)));
            plan = ForgeCompoundPlanner.PlanEquipmentCompound(
                templates, dup, Sel(dup, 10), [Sel(dup, 11), Sel(dup, 11), Sel(dup, 11)], GlovePool, static _ => 0);
            Check.True(plan.Status == ForgeCompoundStatus.InvalidSelection, "one bag item cannot fill two boxes");

            var gems = Bag((10, main), (11, Gem(4210, 1)), (12, Gear(2800)), (13, Gear(2800)));
            plan = ForgeCompoundPlanner.PlanEquipmentCompound(
                templates, gems, Sel(gems, 10), [Sel(gems, 11), Sel(gems, 12), Sel(gems, 13)], GlovePool, static _ => 0);
            Check.True(plan.Status == ForgeCompoundStatus.NotEquipment, "a gem is not supporting gear");
        }

        CheckStoneAttributesKept(templates);

        // S2C 10109 compound reply (MSG_EUQIPFORGE, 40 bytes).
        var reply = PacketBuilder.ForgeCompoundResult(
            PacketBuilder.ForgeResultKindEquipmentCompound,
            Gear(2800, quality: 4, grade: 7, attributes: [20, null, 60, null, 9]));
        Check.Equal(40, reply.Length, "reply is 40 bytes");
        Check.Equal((ushort)40, BinaryPrimitives.ReadUInt16LittleEndian(reply), "length field");
        Check.Equal((ushort)10109, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2)), "opcode 10109");
        Check.Equal((byte)1, reply[4], "success byte");
        Check.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8)), "kind 3");
        Check.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(12)), "BaseLv = quality");
        Check.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(16)), "AppLv = grade");
        int[] expectedAttributes = [20, -1, 60, -1, 9];
        for (var index = 0; index < 5; index++)
        {
            Check.Equal(expectedAttributes[index], BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(20 + 4 * index)), $"AppId[{index}]");
        }

        reply = PacketBuilder.ForgeCompoundResult(PacketBuilder.ForgeResultKindGemCompound, Gem(4211, 1));
        Check.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8)), "kind 2");
        Check.True(reply.AsSpan(12).ToArray().All(static b => b == 0), "kind 2 carries no item data");
        var refusal = PacketBuilder.ForgeResult(success: false, resultKind: 0);
        Check.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(refusal.AsSpan(8)), "refusals use kind 0 (no local apply)");
        Check.Equal((byte)0, refusal[4], "refusal success byte");

        // Trans tab pick hook: stock clients send 0 -> random index kept.
        uint[] targets = [2801, 2802, 3500];
        Check.Equal(1, EquipmentTransformPick.ChooseIndex(targets, 0, 1), "no candidate keeps the random pick");
        Check.Equal(2, EquipmentTransformPick.ChooseIndex(targets, 3500, 0), "a valid candidate is honoured");
        Check.Equal(0, EquipmentTransformPick.ChooseIndex(targets, 9999, 0), "an unknown candidate falls back to random");
        Check.True(EquipmentTransformPick.Enabled, "pick hook on with GODSWAR_AMETHYST_PICK=1 (RELEASE: opt-in; set by Program.Main)");

        return Task.CompletedTask;
    }

    // Review fix 2026-11-03: the Gear Enhancer puts an Attribute Stone into
    // the FIRST empty slot (level 1, Quartz raises it), not slot 5. A slot with
    // AttributeLevel set is kept with its level, wherever it is; its stone
    // chain and stat type never come back from the re-roll.
    private static readonly IReadOnlyList<ForgeCompoundAttributeOption> SwordPool =
    [
        new(0, 0), new(1, 0), new(2, 0), new(20, 2), new(21, 2), new(40, 4), new(60, 6), new(80, 8)
    ];

    private static void CheckStoneAttributesKept(Godswar.Server.Application.Items.IItemTemplateCatalog templates)
    {
        if (!templates.TryGet(1000, out _))
        {
            Check.True(false, "fixture publishes item 1000 (stone-keeping branch)");
            return;
        }

        // Two drop greens, a Strength Stone in slot 3 enhanced to level 2
        // (id 1 = chain 0-4 step 2), and a level-less green in slot 5.
        var main = Gear(1000, attributes: [40, 60, 1, null, 110]) with { AttributeLevel3 = 2 };
        var bag = Bag(
            (20, main),
            (21, Gear(1000, attributes: [40, 60, 80])),
            (22, Gear(1000, attributes: [20])),
            (23, Gear(1000)));
        var supporting = new[] { Sel(bag, 21), Sel(bag, 22), Sel(bag, 23) };
        // attributes 4 + 3 + 1 + 0 = 8 -> 9/4 = 2 r1 (rand 0 < 2) -> 3 re-rolled.
        var plan = ForgeCompoundPlanner.PlanEquipmentCompound(templates, bag, Sel(bag, 20), supporting, SwordPool, static _ => 0);
        Check.True(plan.Committed, "stone item compounds");
        var result = plan.Result;
        Check.True(result.Attribute3 == 1 && result.AttributeLevel3 == 2, "slot-3 stone keeps id 1 and level 2");
        Check.True(result.Attribute1.HasValue && result.Attribute2.HasValue && result.Attribute4.HasValue,
            "three re-rolled greens fill the free slots 1, 2 and 4");
        Check.True(result.Attribute5 is null && result.AttributeLevel5 is null, "level-less slot-5 green is cleared");
        Check.True(result.AttributeLevel1 is null && result.AttributeLevel2 is null && result.AttributeLevel4 is null,
            "re-rolled greens carry no level");
        int?[] rolled = [result.Attribute1, result.Attribute2, result.Attribute4];
        Check.True(rolled.All(static id => id is not (>= 0 and <= 4)),
            "no re-rolled green comes from the kept stone's chain/stat type (rand 0 would pick stat 0 first)");

        // Proof against the live Gear Enhancer: the kept stone can still be
        // enhanced with Quartz Plate 2 (level 2 -> 3), not AttributeAmbiguous.
        var enhanceBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 0, result.ToCompactString());
        enhanceBag = KitBagSlots.SetSlot(enhanceBag, 1, Gem(9930, 1).ToCompactString());
        enhanceBag = KitBagSlots.SetSlot(enhanceBag, 2, Gem(9961, 1).ToCompactString());
        var enhance = GearEnhancementPlanner.Create(templates, enhanceBag, new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(enhanceBag, 0),
            GearEnhancementSlotSelection.Capture(enhanceBag, 1),
            GearEnhancementSlotSelection.Capture(enhanceBag, 2)));
        Check.True(enhance.Status != GearEnhancementStatus.AttributeAmbiguous,
            $"kept stone is not ambiguous for a later Quartz enhance (status {enhance.Status})");
        Check.True(enhance.Committed && enhance.EquipmentAfter.Attribute3 == 2 && enhance.EquipmentAfter.AttributeLevel3 == 3,
            $"kept stone enhances 2 -> 3 in place (status {enhance.Status})");

        // Sensitivity: a same-chain green next to the stone IS ambiguous, so
        // the check above would catch a re-roll from the stone's chain.
        var clash = result with { Attribute1 = 0 };
        var clashBag = KitBagSlots.SetSlot(enhanceBag, 0, clash.ToCompactString());
        var clashed = GearEnhancementPlanner.Create(templates, clashBag, new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(clashBag, 0),
            GearEnhancementSlotSelection.Capture(clashBag, 1),
            GearEnhancementSlotSelection.Capture(clashBag, 2)));
        Check.True(clashed.Status == GearEnhancementStatus.AttributeAmbiguous,
            $"control: a chain-0 green beside the stone is ambiguous (status {clashed.Status})");

        // Total never exceeds five: stone in slot 1 + 4 re-rolled greens.
        var full = Bag(
            (30, Gear(1000, attributes: [2]) with { AttributeLevel1 = 3 }),
            (31, Gear(1000, attributes: [20, 40, 60, 80])),
            (32, Gear(1000, attributes: [20, 40, 60, 80])),
            (33, Gear(1000, attributes: [20, 40, 60, 80])));
        plan = ForgeCompoundPlanner.PlanEquipmentCompound(
            templates, full, Sel(full, 30), [Sel(full, 31), Sel(full, 32), Sel(full, 33)], SwordPool, static _ => 0);
        Check.True(plan.Committed && plan.Result.Attribute1 == 2 && plan.Result.AttributeLevel1 == 3, "slot-1 stone kept with level 3");
        Check.Equal(5, ForgeCompoundPlanner.CountAttributes(plan.Result), "stone + 4 greens = 5, never more");

        // GODSWAR_FORGE_COMPOUND_KEEP_STONES=0: 2008 rule, all five slots wiped.
        plan = ForgeCompoundPlanner.PlanEquipmentCompound(
            templates, bag, Sel(bag, 20), supporting, SwordPool, static _ => 0, keepStoneAttributes: false);
        Check.True(plan.Committed, "2008 rule compounds");
        Check.True(plan.Result.AttributeLevel1 is null && plan.Result.AttributeLevel2 is null &&
                   plan.Result.AttributeLevel3 is null && plan.Result.AttributeLevel4 is null &&
                   plan.Result.AttributeLevel5 is null && plan.Result.Attribute5 is null,
            "2008 rule clears every level and slot 5");
        Check.Equal(3, ForgeCompoundPlanner.CountAttributes(plan.Result), "2008 rule re-rolls the averaged count only");
        Check.True(ForgeCompoundPlanner.KeepStoneAttributes, "stones kept by default (GODSWAR_FORGE_COMPOUND_KEEP_STONES unset)");
    }

    private static CompactItemEntry Gem(uint id, int stack, short bound = 0) =>
        CompactItemEntry.Empty with { Id = id, Quality = 1, Grade = 1, Stack = (short)stack, Bound = bound };

    private static CompactItemEntry Gear(
        uint id,
        short quality = 1,
        short grade = 1,
        short bound = 0,
        int?[]? attributes = null)
    {
        attributes ??= [];
        int? At(int index) => index < attributes.Length ? attributes[index] : null;
        return CompactItemEntry.Empty with
        {
            Id = id,
            Quality = quality,
            Grade = grade,
            Bound = bound,
            Stack = 1,
            Attribute1 = At(0),
            Attribute2 = At(1),
            Attribute3 = At(2),
            Attribute4 = At(3),
            Attribute5 = At(4)
        };
    }

    private static CompactItemEntry[] Bag(params (int Slot, CompactItemEntry Item)[] items)
    {
        var bag = Enumerable.Repeat(CompactItemEntry.Empty, 96).ToArray();
        foreach (var (slot, item) in items)
        {
            bag[slot] = item;
        }

        return bag;
    }

    private static ForgeCompoundSelection[] Units(params (int Slot, int Count, CompactItemEntry Item)[] stacks) =>
        stacks.SelectMany(static stack => Enumerable.Repeat(new ForgeCompoundSelection(stack.Slot, stack.Item), stack.Count))
            .ToArray();

    private static ForgeCompoundSelection Sel(CompactItemEntry[] bag, int slot) => new(slot, bag[slot]);

    private static ForgeCompoundSlotMutation Find(ForgeCompoundPlan plan, int slot) =>
        plan.Mutations.Single(mutation => mutation.Slot == slot);
}
