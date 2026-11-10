namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// F-159 (the admin, 2026-11-09): Purify Sicily 306 / 1306 (Lv121-200) count only the GENERATED Lv121+ Gela Hunters.
/// </summary>
/// <remarks>
/// The client's quest text gives 306/1306 the same target as 298/1298 ("Gela Hunters", QuestMonster 5167), so the
/// slice-2 spawns let Lv121-200 players finish them on the Lv112-120 group. the admin chose a higher GENERATED group:
/// <c>server/tools/pans-labyrinth/gen_destination_spawns.py</c> places 12 stronger copies per camp (same client
/// template, object ids 47401-47424) 20 units beside the quest point, in <c>sicily-spawns.sql</c>. Levels (F-163, the admin
/// "Spread across Lv122-200"): per camp 122, 129, 136, 143, 150, 157, 165, 172, 179, 186, 193, 200.
/// <para>
/// Both groups carry the client name "Gela Hunter", so the name cannot tell them apart. The kill's monster level (the
/// spawn packet's tier) does: a 306/1306 kill only counts from <see cref="MinimumLevel"/> up. The objective itself
/// (and its MonsterId 5167, which the client's quest window counts) is unchanged.
/// </para>
/// <para>
/// GATED: only while <c>GODSWAR_SICILY=1</c>, read at kill time. Map 32 is unreachable with that switch off, so
/// with it off nothing changes.
/// </para>
/// </remarks>
internal static class SicilyHighGroup
{
    /// <summary>The 306/1306 band starts at Lv121 (client Quest.xml MinLevel).</summary>
    public const uint MinimumLevel = 121;

    public const uint SpartaQuestId = 306;
    public const uint AthensQuestId = 1306;
    public const uint FirstObjectId = 47401;
    public const uint LastObjectId = 47499;

    public static bool AppliesTo(uint questId) => questId is SpartaQuestId or AthensQuestId;

    /// <summary>True unless this is a 306/1306 kill on Sicily below Lv121 while the Sicily switch is on.</summary>
    public static bool Admits(uint questId, uint mapId, uint monsterLevel) =>
        Admits(questId, mapId, monsterLevel, EventDestinationsProtocol.SicilyEnabled);

    public static bool Admits(uint questId, uint mapId, uint monsterLevel, bool sicilyEnabled) =>
        !sicilyEnabled ||
        !AppliesTo(questId) ||
        mapId != EventDestinationsProtocol.SicilyMapId ||
        monsterLevel >= MinimumLevel;
}
