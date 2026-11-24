using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.Application.Characters;

// B22 B27-C (the admin d136): the Class Shifter's weapon and armour shift.
/// <summary>
/// B27-C: the rules of the Class Shifter's equipment shift (<c>Athens_044</c> / <c>Sparta_044</c>, function 44,
/// <c>NpcFunWork.lua</c>), as retail stated them:
/// <list type="bullet">
/// <item>IGG guide acid=184 (Development/reference-analysis/text/igg-acid=184.txt): "shift a weapon into the same
/// level weapon specific to another class"; over Level 100 an Equipment Shift Permit is needed; "Increasing attack
/// power Lv.1~Lv.5 and increasing spell power Lv.1~Lv.5 can be transformed into each other. Increasing physical
/// damage by % and increasing spell damage by % can be transformed into each other"; "the weapon will be bound, but
/// its rank and star level will remain unchanged"; "Weapons that are part of a Class Suit cannot be shifted".</item>
/// <item>Retail 2.51 client NF_L0_WK127 says the same ("If the damage type shifts, I can also help transform the
/// damage bonuses").</item>
/// <item>Armour (retail 2.51 NF_L0_WK129 and the 1.5.7 zh_cn text of the same key): only the same damage lines are
/// converted, the armour keeps its name, rank and star level, and is bound; Class Suit armour can't be shifted. The
/// page has no class button, so the lines are converted to the damage type of the character's current class. IGG
/// note 3 names rings and helmets, so every non-weapon equipment kind counts as armour.</item>
/// </list>
/// Everything else on the item (quality, forge grade, append levels, holy sockets, class and elemental attributes,
/// experience, expiry) is kept: the sources are silent about it. The ladder is generated
/// (<see cref="ClassWeaponShiftCatalog"/>, tools/b27-class-shift/gen_weapon_shift.py).
/// </summary>
internal static class EquipmentShiftPolicy
{
    /// <summary>Page one, <c>NF_L0_WK101</c> "Please shift my weapon".</summary>
    public const int WeaponEntry = 101;

    /// <summary>Page one, <c>NF_L0_WK102</c> "Please shift my armor".</summary>
    public const int ArmourEntry = 102;

    /// <summary>Item 4267 "Equipment Shift Permit" (sold at Athens_077 for Honor, the admin d137).</summary>
    public const int PermitItemId = 4267;

    /// <summary>
    /// The weapon page (NpcFunWork.lua Index 2): 120 = item slot 1 + NF_L0_WK127, 121 = item slot 2 + the permit
    /// note NF_L0_WK128, then the four "Turn into a weapon for a [class]" buttons. Their positions do not overlap.
    /// </summary>
    public static readonly int[] WeaponPage = [120, 121, 116, 117, 118, 119];

    // Index 3 result lines of NpcFunWork.lua (all end the dialogue).
    public const int WrongItemLine = 205;     // NF_L0_WK123 "Wrong choice of item(s) ..."
    public const int ShiftedLine = 207;       // NF_L0_WK120 "Item shifted! The item has been put into your backpack."
    public const int PermitMissingLine = 208; // NF_L0_WK119 "... above level 100 should give me 1 Equipment Shift Permit"
    public const int SameClassLine = 209;     // NF_L0_WK117 "This weapon is already specific to that class."
    public const int NoWeaponLine = 206;      // NF_L0_WK118 "Please place your weapon in the first slot."
    public const int UnavailableLine = 200;   // NF_L0_WK125 "Please make a choice"

    /// <summary>
    /// The weapon buttons and the profession each names. <c>NF_L0_WK108</c>-<c>WK111</c> read Warrior, Champion,
    /// Mage, Priest; server professions are 0 Warrior, 1 Champion, 2 Priest, 3 Mage, so 118 and 119 cross over
    /// (the same crossover as the class buttons 112/113).
    /// </summary>
    public static bool TryResolveWeaponButton(int button, out byte profession)
    {
        switch (button)
        {
            case 116: profession = 0; return true;
            case 117: profession = 1; return true;
            case 118: profession = 3; return true;
            case 119: profession = 2; return true;
            default: profession = 0; return false;
        }
    }

    public enum Request { None, WeaponPage, ArmourPage, Weapon, Armour }

    /// <summary>
    /// What one 10069 click on the Class Shifter asks for. <paramref name="subId"/> is +12, <paramref name="selection"/>
    /// the dialogue's selection (+16, or +12 when +16 is -1), <paramref name="pageSelection"/> +20 and
    /// <paramref name="firstArgument"/> the raw +16. Real client (B27-C rig, client 1.5.7):
    /// <list type="bullet">
    /// <item>page-one "Please shift my weapon" + OK: sub 101, +16 = -1 (the weapon page);</item>
    /// <item>weapon page, class button + OK: sub 101, +16 = 116-119, slots in +40 / +44 (a weapon shift);</item>
    /// <item>page-one "Please shift my armor" + OK: sub 102, +16 = -1 (the armour page);</item>
    /// <item>armour page + OK: sub 102, +16 = 0, slots in +40 / +44 (an armour shift).</item>
    /// </list>
    /// The class buttons' captured layout (page entry in +16, button in +20) is accepted for the weapon buttons too.
    /// <paramref name="button"/> is the weapon button (or 0 / another value when none was picked).
    /// </summary>
    public static Request Classify(int subId, int selection, int pageSelection, int firstArgument, out int button)
    {
        button = firstArgument;
        if (pageSelection >= 0 && TryResolveWeaponButton(pageSelection, out _))
        {
            button = pageSelection;
        }

        var weapon = subId == WeaponEntry || selection == WeaponEntry || TryResolveWeaponButton(button, out _);
        var armour = !weapon && (subId == ArmourEntry || selection == ArmourEntry);
        if (!weapon && !armour)
        {
            return Request.None;
        }

        if (button < 0 || button is WeaponEntry or ArmourEntry)
        {
            return weapon ? Request.WeaponPage : Request.ArmourPage;
        }

        return weapon ? Request.Weapon : Request.Armour;
    }

    /// <summary>Hot rate <c>npc.class_shift_equipment</c> (default 1). Off: the old menu-only answers.</summary>
    public static bool Enabled(ServerRatesSnapshot rates) =>
        rates.Absolute(ServerRateKeys.ClassShiftEquipmentEnabled) == 1;

    public static bool Enabled() => Enabled(ServerRates.Current);

    /// <summary>
    /// The armour page (NpcFunWork.lua Index 2): 123 = item slot 1 + NF_L0_WK129, 124 = item slot 2 + the permit note
    /// NF_L0_WK130. It has no button of its own: the window's OK submits it (real client, B27-C rig: sub 102, +16 = 0,
    /// the armour slot in +40 and the permit slot in +44).
    /// </summary>
    public static readonly int[] ArmourPage = [123, 124];

    // Index 3 lines for the armour page.
    public const int NoArmourLine = 211;      // NF_L0_WK121 "Please place your armor in the first slot."
    public const int ArmourShiftedLine = 214; // NF_L0_WK124 "Item shifted! The item has been put into your backpack."

    /// <summary>Equipment kinds the armour page accepts (item_templates.kind): every non-weapon equipment kind.</summary>
    public static readonly string[] ArmourKinds =
        ["head", "shield", "amulet", "glove", "armor", "cloth", "cuff", "girdle", "shoes", "leggins", "ring"];

    public static bool IsArmourKind(string? kind) =>
        kind is not null && Array.IndexOf(ArmourKinds, kind.ToLowerInvariant()) >= 0;

    /// <summary>Class Suit items and the gear tiers built on them (Titan, Primordial, Aether): never shifted.</summary>
    public static bool IsClassSuit(uint itemId) =>
        ClassSuitItemCatalog.IsClassSuit(itemId) || GearTierItemSeeds.All.Any(seed => seed.Id == itemId);

    /// <summary>
    /// The armour's five append attribute ids converted to the damage type of <paramref name="profession"/>: a spell
    /// line on a physical class's armour becomes its physical twin, and the other way round; lines already of that
    /// type, and every other line, are unchanged. Equal to the input when nothing needs converting.
    /// </summary>
    public static short?[] ConvertArmourAttributes(IReadOnlyList<short?> attributes, int profession)
    {
        var result = attributes.ToArray();
        var from = IsPhysical(profession) ? SpellAttributes : PhysicalAttributes;
        var to = IsPhysical(profession) ? PhysicalAttributes : SpellAttributes;
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index] is { } id && Array.IndexOf(from, id) is var at and >= 0)
            {
                result[index] = to[at];
            }
        }

        return result;
    }

    /// <summary>Physical classes (Warrior, Champion) against spell classes (Priest, Mage).</summary>
    public static bool IsPhysical(int profession) => profession is 0 or 1;

    /// <summary>The weapon's class (server profession) and its ladder row, when the weapon can be shifted.</summary>
    public static bool TryFindWeapon(uint itemId, out int profession, out int row)
    {
        var rows = ClassWeaponShiftCatalog.Rows;
        for (row = 0; row < rows.Length; row++)
        {
            var ids = rows[row].ByProfession;
            for (profession = 0; profession < ids.Length; profession++)
            {
                if (ids[profession] == itemId)
                {
                    return true;
                }
            }
        }

        profession = -1;
        row = -1;
        return false;
    }

    /// <summary>The same-level weapon of <paramref name="targetProfession"/>, or false.</summary>
    public static bool TryResolveTarget(uint itemId, int targetProfession, out uint targetItemId, out int sourceProfession)
    {
        targetItemId = 0;
        if (!TryFindWeapon(itemId, out sourceProfession, out var row) || targetProfession is < 0 or > 3)
        {
            return false;
        }

        targetItemId = ClassWeaponShiftCatalog.Rows[row].ByProfession[targetProfession];
        return true;
    }

    /// <summary>True when the weapon is one of the deliberately excluded ones (Class Suit, GM, all-class).</summary>
    public static bool IsExcluded(uint itemId, out string reason)
    {
        foreach (var (id, why) in ClassWeaponShiftCatalog.Excluded)
        {
            if (id == itemId)
            {
                reason = why;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// The append-attribute pairs retail converts when the damage type changes (ItemAppendAttribute.xml):
    /// AttackA-F (0-4, Type 0) with AddMagicAkA-F (20-24, Type 2), and PhysicalDamage (80, Type 8) with
    /// MagicDamage (90, Type 9). Index i of the physical list pairs with index i of the spell list.
    /// </summary>
    public static readonly short[] PhysicalAttributes = [0, 1, 2, 3, 4, 80];

    public static readonly short[] SpellAttributes = [20, 21, 22, 23, 24, 90];

    /// <summary>
    /// The five append attribute ids after the shift: when the damage type changes, each physical line becomes its
    /// spell twin and each spell line its physical twin (one simultaneous map, so a twin is never swapped back);
    /// every other line, and every line when the type stays the same, is unchanged.
    /// </summary>
    public static short?[] ShiftAttributes(IReadOnlyList<short?> attributes, int sourceProfession, int targetProfession)
    {
        var result = attributes.ToArray();
        if (IsPhysical(sourceProfession) == IsPhysical(targetProfession))
        {
            return result;
        }

        for (var index = 0; index < result.Length; index++)
        {
            if (result[index] is not { } id)
            {
                continue;
            }

            var physical = Array.IndexOf(PhysicalAttributes, id);
            var spell = Array.IndexOf(SpellAttributes, id);
            if (physical >= 0)
            {
                result[index] = SpellAttributes[physical];
            }
            else if (spell >= 0)
            {
                result[index] = PhysicalAttributes[spell];
            }
        }

        return result;
    }
}
