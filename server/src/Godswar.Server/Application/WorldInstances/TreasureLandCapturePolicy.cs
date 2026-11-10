using System.Collections.Frozen;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// PETNET: Treasure Land (map 25, <c>Agate</c>) net capture.
/// </summary>
/// <remarks>
/// <para>Sources:</para>
/// <list type="bullet">
/// <item>[IGG] acid=104 Treasure Land: "40 Wooden Tools ... try to net some with the Wooden Tools", "A successful
/// capture is usually followed with a Treasure Bag, full of random items", "The Treasure Bag obtained from ... a Boss
/// even better than ... an Elite monster, which ... promises better drops than ... a regular monster".</item>
/// <item>[wiki] GodsArena "Treasure Land": regular monster -> Simple Travel Pack, elite -> Greater Travel Pack,
/// Icefield Dragon -> Superior Travel Pack; the Icefield Dragon "reappears 6 minutes after being caught".</item>
/// <item>[client] Settings/Sys/ItemBaseAttribute.xml 3958/3959/3960 (consume items, Overlap 99), EquipDescription
/// "A simple / greater / superior pack from Treasure Land. Right click to open." Their contents are the GENERATED
/// <c>GiftPackPolicy.Generated.cs</c> rows. Monster/Agate/Monster.ini lists the eleven templates below.</item>
/// </list>
/// <para>
/// So retail gave a Travel Pack, not a pet or an egg. The monster disappears on a catch and comes back on its
/// respawn timer. Switch: server rate <c>pet.treasure_land_capture</c> (0 = off, the default).
/// </para>
/// </remarks>
internal static class TreasureLandCapturePolicy
{
    public const int MapId = 25;
    public const uint SimpleTravelPackItemId = 3958;
    public const uint GreaterTravelPackItemId = 3959;
    public const uint SuperiorTravelPackItemId = 3960;

    /// <summary>One pack per catch (well under the 99 stack cap).</summary>
    public const int PackQuantity = 1;

    public enum Rank
    {
        Normal,
        Elite,
        Boss
    }

    /// <summary>The client Monster/Agate/Monster.ini templates and their rank (from the key's prefix).</summary>
    private static readonly FrozenDictionary<string, Rank> Templates = new Dictionary<string, Rank>(StringComparer.Ordinal)
    {
        ["A_normalsx_ark_002"] = Rank.Normal,       // Milo Box
        ["A_normalsx_deer_005"] = Rank.Normal,      // Young White Deer
        ["B_normalsx_element_001"] = Rank.Normal,   // Snow Elf
        ["B_normalsx_dragon_004"] = Rank.Normal,    // Frost Dragon
        ["A_elitex_ark_002"] = Rank.Elite,          // [Elite]Milo Box
        ["A_elitex_deer_005"] = Rank.Elite,         // [Elite]Imperial Deer
        ["B_elitex_element_001"] = Rank.Elite,      // [Elite]Snow Elf
        ["B_elitex_dragon_004"] = Rank.Elite,       // [Elite]Titanic Frost Dragon
        ["A_bossx_dragon_014"] = Rank.Boss,         // Icefield Dragon [wiki: Superior Travel Pack]
        ["A_bossy_dragon_014"] = Rank.Boss,         // Frost Dragon (GENERATED: Superior, like the Icefield Dragon)
        ["A_bossz_dragon_014"] = Rank.Boss          // Fire Dragon (GENERATED: Superior, like the Icefield Dragon)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IEnumerable<string> TemplateKeys => Templates.Keys;

    public static bool Enabled => ServerRates.Current.Absolute(ServerRateKeys.TreasureLandCapture) == 1;

    /// <summary>Whether this is a Treasure Land monster a net can catch; ignores the switch.</summary>
    public static bool IsTarget(int mapId, string? templateKey) =>
        mapId == MapId && templateKey is not null && Templates.ContainsKey(templateKey);

    public static bool TryGetRank(int mapId, string? templateKey, out Rank rank)
    {
        rank = Rank.Normal;
        return mapId == MapId && templateKey is not null && Templates.TryGetValue(templateKey, out rank);
    }

    /// <summary>The Travel Pack a catch of this rank gives ([wiki] normal Simple, elite Greater, boss Superior).</summary>
    public static uint PackFor(Rank rank) => rank switch
    {
        Rank.Elite => GreaterTravelPackItemId,
        Rank.Boss => SuperiorTravelPackItemId,
        _ => SimpleTravelPackItemId
    };

    /// <summary>
    /// GENERATED level rule: the guide says to look for "monsters close to your level", nothing more. A net
    /// cannot hold a monster more than this many levels above the player.
    /// </summary>
    public const int MaximumLevelsAbovePlayer = 10;

    public static bool LevelAllows(int playerLevel, uint monsterLevel) =>
        monsterLevel <= (long)playerLevel + MaximumLevelsAbovePlayer;

    /// <summary>
    /// GENERATED success chance: the tool's wild-capture chance (itself GENERATED, Wooden 30% .. Tuck Net 95%,
    /// <c>GODSWAR_WILD_PET_CAPTURE_CHANCES</c>) times a GENERATED rank factor: normal x1, elite x0.5, boss x0.2.
    /// The guide and wiki only say tools raise the rate and bosses are harder. A miss uses up the net and the
    /// monster stays where it is (GENERATED).
    /// </summary>
    public static double CaptureChance(uint netItemId, Rank rank) =>
        WildPetCapturePolicy.CaptureChance((int)netItemId) * rank switch
        {
            Rank.Elite => 0.5,
            Rank.Boss => 0.2,
            _ => 1.0
        };

    /// <summary>[wiki] The Icefield Dragon reappears 6 minutes after being caught; the other bosses follow it (GENERATED).</summary>
    public static readonly TimeSpan BossRespawnInterval = TimeSpan.FromMinutes(6);

    /// <summary>The respawn wait for a Treasure Land boss while the switch is on; other ranks keep their own.</summary>
    public static bool TryGetRespawnInterval(int mapId, string? templateKey, out TimeSpan interval)
    {
        interval = BossRespawnInterval;
        return Enabled && TryGetRank(mapId, templateKey, out var rank) && rank == Rank.Boss;
    }
}
