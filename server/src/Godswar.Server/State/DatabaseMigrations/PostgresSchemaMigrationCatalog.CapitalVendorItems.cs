namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateCapitalVendorItems() => new(
        "20260831_126_capital_vendor_items",
        "Seed stock-client item identities advertised by capital vendors",
        CapitalVendorPetSuppliesSql + "\n" +
        CapitalVendorPetSkillBooksOneSql + "\n" +
        CapitalVendorPetSkillBooksTwoSql + "\n" +
        CapitalVendorPropsSql);
}
