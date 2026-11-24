using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// B22 B27-C (the admin d136): the Class Shifter's weapon and armour shift.
internal static class EquipmentShiftChecks
{
    public const string CheckName =
        "B22 B27-C Class Shifter equipment shift: generated weapon ladder, bonus swap, real-client layouts";

    public static Task RunAsync()
    {
        CheckLadder();
        CheckAttributes();
        CheckRequests();
        CheckSwitch();
        return Task.CompletedTask;
    }

    private static void CheckLadder()
    {
        var seeds = ItemTemplateSeeds.All.Concat(GearTierItemSeeds.All).ToDictionary(static seed => (uint)seed.Id);
        var rows = ClassWeaponShiftCatalog.Rows;
        Check.Equal(ClassWeaponShiftCatalog.RowCount, rows.Length, "row count constant");
        Check.Equal(16, rows.Length, "16 weapon levels (Lv1-120 and the two Lv100/110 Elite Guards rows)");
        var listed = new HashSet<uint>();
        foreach (var (offset, minLevel, ids) in rows)
        {
            Check.Equal(4, ids.Length, $"row {offset} has one weapon per profession");
            for (var profession = 0; profession < 4; profession++)
            {
                var id = ids[profession];
                Check.True(listed.Add(id), $"weapon {id} is in one row only");
                Check.True(seeds.TryGetValue(id, out var seed), $"weapon {id} exists in the item seeds");
                Check.Equal("weapon", seed.Kind, $"{id} is a weapon");
                Check.True(seed.ClassIds.Length == 1 && seed.ClassIds[0] == profession,
                    $"{id} belongs to profession {profession} only");
                Check.Equal(minLevel, seed.MinLevel ?? -1, $"{id} has the row's level");
                Check.True(!EquipmentShiftPolicy.IsClassSuit(id), $"{id} is not Class Suit");
            }

            // Mage is profession 3 (staffs 18xx), Priest profession 2 (wands 17xx).
            Check.Equal(18u, ids[3] / 100, $"row {offset}: the Mage weapon is an 18xx staff");
            Check.Equal(17u, ids[2] / 100, $"row {offset}: the Priest weapon is a 17xx wand");
        }

        // Completeness: every weapon is either shiftable or deliberately excluded, never forgotten.
        var excluded = ClassWeaponShiftCatalog.Excluded.ToDictionary(static row => row.ItemId, static row => row.Reason);
        foreach (var seed in seeds.Values.Where(static seed => seed.Kind == "weapon"))
        {
            var id = (uint)seed.Id;
            Check.True(listed.Contains(id) ^ excluded.ContainsKey(id), $"weapon {id} is listed xor excluded");
            if (excluded.TryGetValue(id, out var reason) && reason == "Class Suit")
            {
                Check.True(EquipmentShiftPolicy.IsClassSuit(id), $"{id} excluded as Class Suit is Class Suit");
            }
        }

        foreach (var suit in new uint[] { 1032, 1035, 1036, 1038, 1435, 1735, 1835 })
        {
            Check.True(EquipmentShiftPolicy.IsExcluded(suit, out var why) && why == "Class Suit", $"{suit} is not shiftable");
            Check.True(!EquipmentShiftPolicy.TryResolveTarget(suit, 3, out _, out _), $"{suit} has no target");
        }

        Check.True(!EquipmentShiftPolicy.TryResolveTarget(1099, 3, out _, out _), "GM Sword has no target");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
            string.Join(',', listed.Order()))));
        Check.Equal(ClassWeaponShiftCatalog.ItemIdSha256, digest, "generated ladder hash (re-run the generator)");

        Check.True(EquipmentShiftPolicy.TryResolveTarget(1013, 3, out var mage, out var from) && mage == 1813 && from == 0,
            "Lost Sword (Warrior Lv120) -> Mage 1813");
        Check.True(EquipmentShiftPolicy.TryResolveTarget(1013, 2, out var priest, out _) && priest == 1713,
            "Lost Sword -> Priest 1713");
        Check.True(EquipmentShiftPolicy.TryResolveTarget(1009, 1, out var pike, out _) && pike == 1409,
            "Lv100 sword -> Champion 1409");
        Check.True(EquipmentShiftPolicy.TryResolveTarget(1812, 0, out var tulwar, out _) && tulwar == 1012,
            "the Lv105 row (offset 12) pairs Lv105 with Lv105");
    }

    private static void CheckAttributes()
    {
        var templates = ItemAttributeTemplateSeeds.All.ToDictionary(static seed => (short)seed.Id);
        for (var index = 0; index < EquipmentShiftPolicy.PhysicalAttributes.Length; index++)
        {
            var physical = templates[EquipmentShiftPolicy.PhysicalAttributes[index]];
            var spell = templates[EquipmentShiftPolicy.SpellAttributes[index]];
            Check.True(physical.StatType is 0 or 8, $"{physical.NameKey} is a physical damage line");
            Check.Equal(physical.StatType == 0 ? (short)2 : (short)9, spell.StatType, $"{spell.NameKey} is its spell twin");
            Check.Equal(physical.NameKey[^1], spell.NameKey[^1], $"{physical.NameKey} pairs with the same tier");
        }

        short?[] gmSword = [4, 4, 80, 40, 80];
        Check.True(EquipmentShiftPolicy.ShiftAttributes(gmSword, 0, 3).SequenceEqual(new short?[] { 24, 24, 90, 40, 90 }),
            "Warrior -> Mage turns every physical line into its spell twin");
        short?[] mixed = [0, 20, null, 60, 90];
        Check.True(EquipmentShiftPolicy.ShiftAttributes(mixed, 0, 2).SequenceEqual(new short?[] { 20, 0, null, 60, 80 }),
            "one simultaneous map: a twin is never swapped back");
        Check.True(EquipmentShiftPolicy.ShiftAttributes(gmSword, 0, 1).SequenceEqual(gmSword),
            "Warrior -> Champion keeps the lines (same damage type)");
        Check.True(EquipmentShiftPolicy.ShiftAttributes(new short?[] { 24, 90, 60, 40, 144 }, 3, 2)
            .SequenceEqual(new short?[] { 24, 90, 60, 40, 144 }), "Mage -> Priest keeps the lines");
        Check.True(EquipmentShiftPolicy.ShiftAttributes(new short?[] { 24, 90, 60, 40, 144 }, 3, 0)
            .SequenceEqual(new short?[] { 4, 80, 60, 40, 144 }), "Mage -> Warrior turns spell lines physical");
        Check.True(EquipmentShiftPolicy.ShiftAttributes(new short?[] { 200, 210, 340, 360, 420 }, 0, 3)
            .SequenceEqual(new short?[] { 200, 210, 340, 360, 420 }), "lines the sources do not name stay as they are");

        Check.True(EquipmentShiftPolicy.ConvertArmourAttributes(new short?[] { 4, 80, 40, null, null }, 3)
            .SequenceEqual(new short?[] { 24, 90, 40, null, null }), "a Mage's armour: physical lines become spell lines");
        Check.True(EquipmentShiftPolicy.ConvertArmourAttributes(new short?[] { 24, 4, null, null, null }, 0)
            .SequenceEqual(new short?[] { 4, 4, null, null, null }), "a Warrior's armour: spell lines become physical");
        short?[] already = [4, 40, null, null, null];
        Check.True(EquipmentShiftPolicy.ConvertArmourAttributes(already, 1).SequenceEqual(already),
            "nothing to convert leaves the armour unchanged (the handler answers 205 and uses no permit)");

        Check.True(EquipmentShiftPolicy.IsArmourKind("ring") && EquipmentShiftPolicy.IsArmourKind("head") &&
                   EquipmentShiftPolicy.IsArmourKind("armor"), "rings, helmets and armour are armour");
        Check.True(!EquipmentShiftPolicy.IsArmourKind("weapon") && !EquipmentShiftPolicy.IsArmourKind("stylish") &&
                   !EquipmentShiftPolicy.IsArmourKind("mounthead") && !EquipmentShiftPolicy.IsArmourKind(null),
            "weapons, fashion and mount gear are not");
        Check.True(EquipmentShiftPolicy.IsClassSuit(2031) && !EquipmentShiftPolicy.IsClassSuit(2113),
            "Class Suit armour is refused, ordinary armour is not");
    }

    private static void CheckRequests()
    {
        // The real 1.5.7 client on the B27-C rig (evidence/rig-real-client-probe-run2.txt).
        Check.Equal("WeaponPage", Kind(101, 101, -1, -1, out _),
            "page one 101 + OK opens the weapon page");
        Check.Equal("Weapon", Kind(101, 118, -1, 118, out var mage),
            "weapon page: Mage button + OK is a weapon shift");
        Check.Equal(118, mage, "the button travels in +16");
        Check.Equal("ArmourPage", Kind(102, 102, -1, -1, out _),
            "page one 102 + OK opens the armour page");
        Check.Equal("Armour", Kind(102, 0, -1, 0, out _),
            "armour page + OK (+16 = 0) is an armour shift");
        Check.Equal("Weapon", Kind(101, 0, -1, 0, out var none),
            "weapon page OK without a class is a weapon request ...");
        Check.True(!EquipmentShiftPolicy.TryResolveWeaponButton(none, out _), "... with no class (answered 200)");
        Check.Equal("Weapon", Kind(-1, 101, 117, 101, out var spear),
            "the class buttons' captured layout (entry in +16, button in +20) works for weapons too");
        Check.Equal(117, spear, "button read from +20");
        Check.Equal("None", Kind(100, 112, -1, 112, out _),
            "a class button is left to the class change");
        Check.Equal("None", Kind(-1, 100, 112, 100, out _),
            "the class change's captured layout is left to it too");
        Check.Equal("None", Kind(-1, -1, -1, -1, out _),
            "the opening request is left to the menu");
        Check.Equal("None", Kind(103, 103, -1, -1, out _),
            "features of the classes is left to the menu");

        byte[] expected = [0, 1, 3, 2];
        for (var button = 116; button <= 119; button++)
        {
            Check.True(EquipmentShiftPolicy.TryResolveWeaponButton(button, out var profession) &&
                       profession == expected[button - 116], $"weapon button {button} -> profession {expected[button - 116]}");
        }

        Check.True(EquipmentShiftPolicy.WeaponPage.SequenceEqual(new[] { 120, 121, 116, 117, 118, 119 }),
            "weapon page = both item slots + four class buttons");
        Check.True(EquipmentShiftPolicy.ArmourPage.SequenceEqual(new[] { 123, 124 }), "armour page = both item slots");
    }

    private static string Kind(int subId, int selection, int pageSelection, int first, out int button) =>
        EquipmentShiftPolicy.Classify(subId, selection, pageSelection, first, out button).ToString();

    private static void CheckSwitch()
    {
        var definition = ServerRateCatalog.Get(ServerRateKeys.ClassShiftEquipmentEnabled);
        Check.Equal(1m, definition.DefaultValue, "npc.class_shift_equipment defaults to on");
        Check.True(definition.Kind == ServerRateKind.Absolute, "the switch is absolute");
        Check.True(EquipmentShiftPolicy.Enabled(ServerRatesSnapshot.Default), "on by default");
        var off = ServerRatesSnapshot.Create([new(ServerRateKeys.ClassShiftEquipmentEnabled, 0m)], 1,
            DateTimeOffset.UtcNow, "check", "test");
        Check.True(!EquipmentShiftPolicy.Enabled(off), "0 switches it off");
        Check.Equal(4267, EquipmentShiftPolicy.PermitItemId, "Equipment Shift Permit item");
    }
}
