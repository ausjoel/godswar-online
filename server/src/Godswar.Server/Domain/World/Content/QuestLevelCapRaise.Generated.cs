// FINALISE2b (the admin F-121, 2026-11-07): "Raise all of them to 200" = the 159 quests whose client MaxLevel was 121-140
// (Development/finalise2/p1-gates/QUESTS-CAPPED-140.md, Part 1). For these the server drops the nine-level upper band:
// any level from MinLevel-5 up to 200 may take them. The client's Quest.xml MaxLevel is 200 for the same ids (client 1.4.2).
// The 471 stock cap-120 quests (Part 2) are NOT in this list.
namespace Godswar.Server.Domain.World.Content;

internal static class QuestLevelCapRaise
{
    public const int RaisedMaximumLevel = 200;

    public static readonly IReadOnlySet<uint> QuestIds = new HashSet<uint>
    {
        468u, 1342u, 1343u, 1344u, 1345u, 1346u, 1347u, 342u, 343u, 344u, 345u, 346u, 347u, 1388u, 388u, 1348u, 1349u, 1350u, 1351u, 1352u, 1353u, 348u, 349u, 350u, 351u, 352u, 353u, 1386u, 386u, 1308u, 1319u, 1357u, 1387u, 308u, 319u, 357u, 387u, 1358u, 1393u, 358u, 393u, 1394u, 394u, 1359u, 1360u, 359u, 360u, 1355u, 1361u, 1371u, 1372u, 1373u, 1374u, 1375u, 1376u, 1512u, 1517u, 355u, 361u, 371u, 372u, 373u, 374u, 375u, 376u, 512u, 517u, 1306u, 1356u, 1362u, 1369u, 1370u, 1474u, 1475u, 306u, 356u, 362u, 369u, 370u, 474u, 475u, 1383u, 383u, 1363u, 1364u, 1365u, 1366u, 1367u, 1368u, 1377u, 1378u, 1379u, 1380u, 1381u, 1382u, 363u, 364u, 365u, 366u, 367u, 368u, 377u, 378u, 379u, 380u, 381u, 382u, 1627u, 1628u, 1629u, 1630u, 1642u, 1648u, 1649u, 1653u, 1654u, 1655u, 1656u, 627u, 628u, 629u, 630u, 642u, 648u, 649u, 653u, 654u, 655u, 656u, 1631u, 1632u, 1645u, 1651u, 1652u, 631u, 632u, 645u, 651u, 652u, 1633u, 633u, 1643u, 643u, 1635u, 635u, 1636u, 636u, 1637u, 637u, 1638u, 1644u, 638u, 644u, 1639u, 639u, 1640u, 640u, 1641u, 641u
    };

    /// <summary>True only while the hot rate quest.caps_121_140_to_200 is 1 (default 0, until client 1.4.2 ships).</summary>
    public static bool Applies(uint questId) =>
        QuestIds.Contains(questId) &&
        Application.Finalise2.Finalise2Rates.On(Application.Finalise2.Finalise2RateKeys.QuestCapsTo200);
}
