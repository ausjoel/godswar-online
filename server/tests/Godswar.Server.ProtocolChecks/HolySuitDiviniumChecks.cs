using System.Buffers.Binary;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolySuitDiviniumChecks
{
    public const string CheckName =
        "Holy Suit Divinium progression, legacy publication, wire and socket boundaries";

    public static Task RunAsync()
    {
        CheckPublishedChains();
        CheckMaterialIdentityRelease();
        CheckRejectedContent();
        CheckStateAndWire();
        CheckSocketThreshold();
        return Task.CompletedTask;
    }

    private static void CheckMaterialIdentityRelease()
    {
        var previous = HolySuitContentBaselineV2.ItemTemplates.ToDictionary(item => item.Id);
        var names = new[] { "RuneSteel Ingot", "Arcanite Crystal", "Seraphite Core", "Divinium Essence" };
        foreach (var item in HolySuitContentBaseline.ItemTemplates)
        {
            var old = previous[item.Id];
            var expectedName = item.Id is >= 9014 and <= 9017 ? names[item.Id - 9014] : old.DisplayName;
            Check.True(item.DisplayName == expectedName && (item with { DisplayName = old.DisplayName }) == old,
                $"material {item.Id} changes only its reviewed name and preserves every runtime identity field");
        }
        Check.True(HolySuitContentBaselineV2.ItemTemplates.Where(item => item.Id is >= 9014 and <= 9017)
            .Select(item => item.DisplayName).SequenceEqual(
                ["RuneSteel Ware", "Arcanite Ware", "Seraphite Ware", "Divinium Ware"]),
            "the released eight-tier predecessor retains its original material names");
        Check.True(HolySuitContentBaseline.Tiers.SequenceEqual(HolySuitContentBaselineV2.Tiers) &&
            HolySuitContentBaseline.Upgrades.SequenceEqual(HolySuitContentBaselineV2.Upgrades) &&
            HolySuitContentBaseline.Consumables.SequenceEqual(HolySuitContentBaselineV2.Consumables) &&
            HolySuitContentBaseline.OperationPolicy == HolySuitContentBaselineV2.OperationPolicy,
            "proper material names preserve all eighty recipes, tier stats, stack caps and operation policy");
    }

    private static PinnedHolySuitContentCatalog Create(
        IReadOnlyList<HolySuitTierDefinition>? tiers = null,
        IReadOnlyList<HolySuitUpgradeDefinition>? upgrades = null,
        IReadOnlyList<HolySuitConsumableDefinition>? consumables = null) =>
        PinnedHolySuitContentCatalog.Create(TestItemContent.HolySuitContent.Templates.All,
            tiers ?? HolySuitContentBaseline.Tiers,
            upgrades ?? HolySuitContentBaseline.Upgrades,
            consumables ?? HolySuitContentBaseline.Consumables,
            HolySuitContentBaseline.OperationPolicy);

    private static void CheckPublishedChains()
    {
        var legacy = Create(HolySuitContentBaselineV1.Tiers, HolySuitContentBaselineV1.Upgrades,
            HolySuitContentBaselineV1.Consumables);
        Check.Equal(8, legacy.Tiers.Count, "immutable predecessor retains Common and seven tiers");
        Check.True(legacy.Tiers[5].Name == "Mithril" && legacy.Tiers[6].Name == "Orichalcum" &&
            legacy.Tiers[7].Name == "Adamantium" && !legacy.TryGetUpgrade(7, 10, out _),
            "legacy publication remains independently verifiable with its original names and terminal state");

        var current = Create();
        Check.True(current.Tiers.Select(t => t.Name).SequenceEqual(
            ["Common", "Bronze", "Silver", "Gold", "Platinum", "RuneSteel", "Arcanite", "Seraphite", "Divinium"]),
            "new publication preserves tier identities and lower tiers while naming the complete hierarchy");
        Check.True(current.Upgrades.Take(70).SequenceEqual(legacy.Upgrades),
            "all seventy existing progression costs remain unchanged");
        Check.True(current.TryGetUpgrade(7, 10, out var first) && first.TargetSuitType == 8 &&
            first.TargetLevel == 1 && first.WareItemId == 9017 && first.WareQuantity == 1 &&
            first.RequiredPrisms == 99 && first.RequiredItemExperience == 0,
            "Seraphite ten advances to Divinium one with one new ware and 99 prisms");
        Check.True(current.TryGetUpgrade(8, 9, out var last) && last.TargetLevel == 10 &&
            last.WareQuantity == 10 && last.RequiredPrisms == 126 &&
            !current.TryGetUpgrade(8, 10, out _),
            "Divinium ten is the real terminal state after ten wares and 126 prisms");
        Check.True(current.TryGetConsumable(9017, out var ware) && ware.SuitType == 8 && ware.StackCap == 99,
            "new ware remains a bounded ordinary stack");
    }

    private static void CheckRejectedContent()
    {
        Check.Throws<InvalidOperationException>(() => Create(upgrades: HolySuitContentBaselineV1.Upgrades),
            "eight-tier declaration cannot publish a legacy truncated upgrade chain");
        Check.Throws<InvalidOperationException>(() => Create(tiers: HolySuitContentBaselineV1.Tiers,
            consumables: HolySuitContentBaselineV1.Consumables),
            "legacy declaration cannot admit an undeclared eighth tier");
        Check.Throws<InvalidOperationException>(() => Create(consumables: HolySuitContentBaselineV1.Consumables),
            "new tier cannot omit its ware definition");
        var tiers = HolySuitContentBaseline.Tiers.ToArray();
        tiers[5] = tiers[5] with { SuitType = 9 };
        Check.Throws<InvalidOperationException>(() => Create(tiers: tiers),
            "tier identifiers must form an exact contiguous supported range");
        var upgrades = HolySuitContentBaseline.Upgrades.ToArray();
        upgrades[70] = upgrades[70] with { TargetLevel = 2, WareQuantity = 2 };
        Check.Throws<InvalidOperationException>(() => Create(upgrades: upgrades),
            "tier transition cannot skip Divinium one");
        upgrades = HolySuitContentBaseline.Upgrades.ToArray();
        upgrades[^1] = upgrades[^1] with { TargetSuitType = 9, WareItemId = 9018 };
        Check.Throws<InvalidOperationException>(() => Create(upgrades: upgrades),
            "final transition cannot exceed Divinium");
        var consumables = HolySuitContentBaseline.Consumables.ToArray();
        var index = Array.FindIndex(consumables, c => c.ItemId == 9017);
        consumables[index] = consumables[index] with { ItemId = 9018 };
        Check.Throws<InvalidOperationException>(() => Create(consumables: consumables),
            "unreviewed ware identities cannot substitute for Divinium");
    }

    private static void CheckStateAndWire()
    {
        Check.True(HolySuitProgressionPolicy.TryReadCode(0, out _, out _), "Common remains valid");
        foreach (var type in Enumerable.Range(1, 8))
        foreach (var level in Enumerable.Range(1, 10))
        {
            var code = type * 100 + level;
            Check.True(HolySuitProgressionPolicy.TryReadCode(code, out var parsedType, out var parsedLevel) &&
                parsedType == type && parsedLevel == level, $"valid state {code} decodes exactly");
        }
        foreach (var code in new[] { -1, 1, 100, 700, 711, 800, 811, 901, int.MinValue, int.MaxValue })
            Check.True(!HolySuitProgressionPolicy.TryReadCode(code, out _, out _),
                $"invalid state {code} is rejected without overflow");
        Check.True(!HolySuitProgressionPolicy.IsMaximum(710) && HolySuitProgressionPolicy.IsMaximum(810),
            "only Divinium ten blocks further stored-EXP progression");
        Check.True(Enumerable.Range(801, 10).Select(HolySuitProgressionPolicy.GetBonusPercent)
            .SequenceEqual([71, 73, 75, 77, 79, 82, 84, 86, 88, 90]),
            "ten Divinium levels span the approved seventy-one to ninety percent curve");
        Check.True(HolySuitProgressionPolicy.GetBonusPercent(710) == 70 &&
            HolySuitProgressionPolicy.GetBonusPercent(601) == 51 &&
            HolySuitProgressionPolicy.GetBonusPercent(901) == 0 &&
            HolySuitProgressionPolicy.GetEffectPoints(810) == 90,
            "earlier tiers retain cumulative percentage and invalid codes award no points");

        foreach (var code in new[] { 710, 801, 810 })
        {
            var item = CompactItemEntry.Empty with { Id = 1035, Quality = 20, Grade = 25, Stack = 1,
                Bound = 1, Exp = 123456, HolySuitCode = code, SocketCount = 4,
                Socket1EffectId = 13, Socket1Level = 10, Socket1Value = 321 };
            var parsed = CompactItemEntry.Parse(item.ToCompactString());
            Check.Equal((short)(code / 100), parsed.HolySuitType, "compact state preserves the actual tier");
            Check.Equal((short)(code % 100), parsed.HolySuitLevel, "compact state preserves the actual level");
            var character = new GameCharacter { KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 0,
                parsed.ToCompactString()) };
            var record = PacketBuilder.KitBagDetailPages(character)[0].AsSpan(24, 72);
            Check.Equal((short)code, BinaryPrimitives.ReadInt16LittleEndian(record.Slice(32, 2)),
                "existing native signed-short field preserves the full Holy Suit code");
            Check.Equal((short)4, BinaryPrimitives.ReadInt16LittleEndian(record.Slice(34, 2)),
                "Divinium cannot overwrite the adjacent socket count");
            Check.Equal(123456, BinaryPrimitives.ReadInt32LittleEndian(record.Slice(28, 4)),
                "Divinium cannot overwrite stored gear experience");
        }
    }

    private static void CheckSocketThreshold()
    {
        Check.True(TestItemContent.Catalog.TryGet(1035, out var original), "socket fixture weapon is published");
        var template = original with { MinLevel = 140 };
        var gear = CompactItemEntry.Empty with { Id = 1035, Quality = 15, Grade = 20, SocketCount = 3 };
        var spell = CompactItemEntry.Empty with { Id = HolyStoneDrillEligibilityPolicy.SocketSpellFourItemId, Stack = 1 };
        foreach (var code in new[] { 510, 600, 601, 701, 801, 810 })
        {
            var result = HolyStoneDrillEligibilityPolicy.ValidateAdvanced(template, gear with { HolySuitCode = code }, spell);
            Check.True(result == (code >= 601 ? HolyStoneDrillEligibilityFailure.None :
                HolyStoneDrillEligibilityFailure.FourthSocketEquipment),
                $"fourth socket at {code} retains the original tier-six threshold");
        }
        Check.True(HolyStoneDrillEligibilityPolicy.ValidateAdvanced(template,
            gear with { HolySuitCode = 810, SocketCount = 4 }, spell) == HolyStoneDrillEligibilityFailure.MaximumSockets,
            "Divinium does not grant an unrequested fifth socket");
    }
}
