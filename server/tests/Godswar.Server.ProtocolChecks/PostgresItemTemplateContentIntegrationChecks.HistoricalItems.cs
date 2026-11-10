namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    // Migration 118 added Anemone, Copper Ore and Herb; migration 126 added
    // these 87 capital-vendor identities. They did not exist in the pinned
    // pets-v2/v3 releases. Keep their original revision/count goldens intact
    // when reconstructing those releases from today's larger publication.
    // Migration 118's 9916/9940/9941 already existed and must be retained.
    private static readonly int[] PostPetItemsV3ItemIds =
    [
        10001, 12010, 12030,
        3935, 3936, 3939, 3949, 4001, 4002, 4031, 4032, 4100, 4150, 4151,
        4152, 4468, 10000, 10020, 10021, 10040, 10041, 10061, 10080,
        10081, 10082, 10200, 10201, 10202, 10203, 10204, 10205, 10206,
        10207, 10208, 10209, 10210, 10211, 10212, 10213, 10214, 10215,
        10216, 10217, 10218, 10219, 10220, 10221, 10222, 10223, 10224,
        10225, 10226, 10227, 10228, 10229, 10230, 10231, 10232, 10295,
        10298, 10301, 10304, 10307, 10410, 10416, 10422, 10428, 10434,
        10440, 10446, 10452, 10458, 10470, 10476, 10486, 10520, 10540,
        10550, 10560, 10570, 10580, 10600, 10610, 10710, 10720, 10730,
        10740, 12110, 12120, 12130
    ];
}
