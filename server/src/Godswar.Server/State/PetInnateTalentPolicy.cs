using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// Assigns innate pet talents from aptitude alone. Talent items and client
/// profile fields are compatibility data and must never author this mask.
/// </summary>
/// <remarks>
/// <para>
/// The ladder is the database rule
/// <c>ck_character_pets_quality_innate_talents</c> and
/// <c>ck_pet_content_aptitude_innate_talents</c>, transcribed tier by tier:
/// </para>
/// <list type="bullet">
/// <item>tiers 1-6: no innate talent.</item>
/// <item>tier 7, the client's <c>PETAPTITUDE7</c> 聪慧型: every talent.</item>
/// <item>tier 8, 热情型: quest dispatch, healing and merge.</item>
/// <item>tier 9, 暴躁型: no innate talent, exactly as the rule spells out --
/// the tier is deliberately absent from the rule's granted tiers.</item>
/// <item>tiers 10-13: quest dispatch, healing and merge.</item>
/// <item>tiers 14-16: every talent.</item>
/// </list>
/// <para>
/// This is the one place the rule is written. The durable hatch INSERT, the
/// published aptitude content, and the startup content validation all resolve
/// the mask here, so code and database can never disagree about a tier again.
/// </para>
/// </remarks>
internal static class PetInnateTalentPolicy
{
    public const byte SmartTalentMask =
        2 | // Quest Dispatch
        8 | // Healing
        16; // Merge

    public const byte GodlyTalentMask = PetTalentCatalog.SupportedMask;

    private static readonly FrozenDictionary<short, byte> Masks =
        PetAptitudeCatalog.All.ToFrozenDictionary(
            static definition => definition.Value,
            static definition => ResolveCore(definition.Value));

    public static byte Resolve(PetAptitude aptitude)
    {
        if (!Masks.TryGetValue((short)aptitude, out var mask))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unsupported pet aptitude.");
        }

        return mask;
    }

    private static byte ResolveCore(short aptitude) => aptitude switch
    {
        7 => GodlyTalentMask,
        8 or 10 or 11 or 12 or 13 => SmartTalentMask,
        >= 14 => GodlyTalentMask,
        _ => 0
    };

    public static bool HasTalent(
        PetAptitude aptitude,
        PetTalentKind talent)
    {
        if (!PetTalentCatalog.TryGet(talent, out var definition))
        {
            return false;
        }

        return (Resolve(aptitude) & definition.MaskBit) != 0;
    }
}
