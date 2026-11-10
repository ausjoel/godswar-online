namespace Godswar.Server.State;

/// <summary>
/// City actors of the maps outside the two capitals, recovered from the
/// 2026-09-24/25 capture session.
/// </summary>
/// <remarks>
/// The reference keeps its own identity per map. These rows came from a build
/// whose published ids were unrelated (Thebes_All_001 on 55284, Megara_All_001
/// on 53307) and whose coordinates sat roughly one unit away, so feeding them
/// through the same <c>NpcSpawnDefinition</c> path as the capitals is what lets
/// <c>CapturedNpcPlacementPolicy</c> publish each map with the reference's own
/// ids, appearance word, position and facing.
/// <para>
/// Only the templates the capture actually placed are listed. A published row
/// the capture never recorded is deliberately absent for a map the capture
/// covers: the reference's content is what it placed, not what a client ini
/// happened to contain. That is why Megara_All_012, Derveni_All_004/005 and
/// several Thebes rows have no entry here.
/// </para>
/// <para>
/// Athens and Sparta stay in their own files; this one holds the secondary
/// cities, which share a single capture-driven origin.
/// </para>
/// </remarks>
internal static partial class NpcActorPlacementCatalog
{
    private static readonly IReadOnlyList<NpcActorPlacement> SecondaryCities =
    [
        // Thebes (map 9), captured 2026-09-25.
        new(9, "Thebes_All_001", "Thebes_All_001_FemVillager3", 5435u, 19f, 35f, 3f),
        new(9, "Thebes_All_002", "Thebes_All_002_yishi", 5436u, 33f, 35f, 3f),
        new(9, "Thebes_All_003", "Thebes_All_003_Male6", 5437u, 19f, 64f, 1.7f),
        new(9, "Thebes_All_004", "Thebes_All_004_Male34", 5438u, -3f, 87f, 1.7f),
        new(9, "Thebes_All_005", "Thebes_All_005_FemHero2", 5439u, 4f, 109f, 3f),
        new(9, "Thebes_All_006", "Thebes_All_006_Male11", 5440u, 30f, 95f, 3f),
        new(9, "Thebes_All_007", "Thebes_All_007_FemPaladin1", 5441u, 35.5f, 102f, 1.7f),
        new(9, "Thebes_All_008", "Thebes_All_008_Belle3", 5442u, 36f, 121f, 1.7f),
        new(9, "Thebes_All_009", "Thebes_All_009_Male11", 5443u, 47f, 86f, 5.6f),
        new(9, "Thebes_All_010", "Thebes_All_010_Belle1", 5444u, 18f, 95f, 3f),
        new(9, "Thebes_All_011", "Thebes_All_011_Belle2", 5445u, 53f, 136f, 3f),
        new(9, "Thebes_All_012", "Thebes_All_012_Male6", 5446u, 47f, 136f, 3f),
        new(9, "Thebes_All_015", "Thebes_All_015_Male38", 5447u, 24f, 86f, 3f),
        new(9, "Thebes_All_024", "Thebes_All_024_FemMerchant1", 5448u, 35.5f, 112f, 1.7f),
        new(9, "Thebes_All_027", "Thebes_All_027_Male34", 5449u, 45f, 120f, 2.3f),
        new(9, "Thebes_All_028", "Thebes_All_028_Male11", 5450u, 41f, 108f, 1.7f),

        // Derveni (map 15), captured 2026-09-25.
        new(15, "Derveni_All_002", "Derveni_All_002_FemMerchant3", 5512u, 28f, 28f, 3f),
        new(15, "Derveni_All_003", "Derveni_All_003_Exec7", 5513u, 32f, 28f, 3f),
        new(15, "Derveni_All_006", "Derveni_All_006_MaleMerchant4", 5516u, 24f, 36f, 3f),

        // Megara (map 18), captured 2026-09-25 00:13.
        new(18, "Megara_All_001", "Megara_All_001_Male5", 5540u, 39f, 105f, 2.3f),
        new(18, "Megara_All_002", "Megara_All_002_Male7", 5541u, 31f, 100f, 2.3f),
        new(18, "Megara_All_003", "Megara_All_003_AthenianCivilian1", 5542u, 35f, 132f, 2.3f),
        new(18, "Megara_All_004", "Megara_All_004_FemMale4", 5543u, 13f, 140f, 2.3f),
        new(18, "Megara_All_005", "Megara_All_005_Male21", 5544u, 27f, 151f, 1.7f),
        new(18, "Megara_All_006", "Megara_All_006_FemMale6", 5545u, 42f, 117f, 1.7f),
        new(18, "Megara_All_007", "Megara_All_007_MaleHero2", 5546u, 52f, 80f, 2.3f),
        new(18, "Megara_All_008", "Megara_All_008_MaleSage2", 5547u, 68f, 60f, 5.4f),
        new(18, "Megara_All_009", "Megara_All_009_FemSage1", 5548u, 28f, 168f, 2.3f),
        new(18, "Megara_All_010", "Megara_All_010_MaleVillager2", 5549u, 48f, 116f, 1.7f),
        new(18, "Megara_All_011", "Megara_All_011_MaleSage3", 5550u, 49f, 78f, 2.3f),
        new(18, "Megara_All_014", "Megara_All_014_yishi", 5553u, 53f, 83f, 2.3f),
        new(18, "Megara_All_015", "Megara_All_015_Male6", 5554u, 44f, 79f, 2.3f)
    ];
}
