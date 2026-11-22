using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed client-catalog identities that the equipment-only generator
/// (tools/GenerateItemTemplates.ps1) never imported: consumables, materials,
/// quest items, sigil stones, gathering and pet items.
///
/// Every entry is transcribed from the installed client's
/// Localization/en_us/Settings/Sys/ItemBaseAttribute.xml and Text/EquipName.dat
/// by tools/GenerateClientCatalogItemSeeds.ps1. The set is published as the
/// immutable item-content family "client-catalog-v1"; runtime code consumes the
/// sealed PostgreSQL projection rather than these compiled seeds.
/// </summary>
internal static class ClientCatalogItemContentBaseline
{
    // B22: + the generated skill-level books and world-boss skill-book chests (SkillLevelItemSeeds).
    // B22 B23-F: + the 15 class field skill books (ClassFieldItemSeeds).
    public const int ShippedItemCount = ClientCatalogItemSeeds.ShippedItemCount + SkillLevelItemSeeds.Count +
        ClassFieldItemSeeds.Count;

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        ClientCatalogItemSeeds.All.Concat(SkillLevelItemSeeds.All).Concat(ClassFieldItemSeeds.All).ToArray(); // B22, B23-F
}
