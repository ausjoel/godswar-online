using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Seasons;

/// <summary>One row of public.seasonal_events: on/off plus an optional UTC window (yearly = same dates every year).</summary>
internal readonly record struct SeasonWindow(string Key, bool Enabled, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, bool Yearly)
{
    public bool IsOpenAt(DateTimeOffset at)
    {
        if (!Enabled) return false;
        if (StartUtc is not { } start || EndUtc is not { } end) return true; // switched on with no dates = open
        if (!Yearly) return at >= start && at < end;
        var years = at.UtcDateTime.Year - start.UtcDateTime.Year;
        for (var shift = Math.Max(0, years - 1); shift <= years; shift++)
            if (at >= start.AddYears(shift) && at < end.AddYears(shift)) return true;
        return false;
    }
}

/// <summary>
/// W3 seasonal events (the admin F-26: "Build them all now but keep hidden but in server editor allow toggles to set
/// enabling and time frames"). Every season starts switched off. A season's envoys are on their map only while it
/// is open (applied at map entry, like the event NPCs); off-season they are removed from the published roster.
/// The Festival Envoy (Athens/Sparta_084) is not seasonal: it hands out the festival gifts (event.festival_gifts).
/// </summary>
internal static class SeasonalEvents
{
    public const string Thanksgiving = "thanksgiving";
    public const string Christmas = "christmas";

    /// <summary>Every season the editor shows, in calendar order, with its label.</summary>
    public static readonly (string Key, string Label)[] All =
    [
        ("spring_festival", "Spring Festival (Spring Festival Envoy, Lunar Beast Slayer)"),
        ("carnival", "Carnival (Carnival Envoy)"),
        ("easter", "Easter (Easter Envoy, Easter Bunny Envoy)"),
        ("may_day", "May Day (May Day Envoy, faction trees)"), // W3 seasons may day
        ("igg_anniversary", "IGG Anniversary (IGG Anniversary Envoy)"),
        ("mid_autumn", "Mid-autumn (Lunar Priest)"),
        ("oriental_cuisine", "Oriental Cuisine (Oriental Cuisine Master)"),
        ("halloween", "Halloween (Halloween Envoy)"),
        (Thanksgiving, "Thanksgiving (Thanksgiving Envoy, turkey drops)"),
        (Christmas, "Christmas (Santa Claus, antler drops from Little Deer / Vicious Fawn)")
    ];

    /// <summary>Published envoys that belong to a season (removed while it is closed).</summary>
    private static readonly Dictionary<string, string> EnvoySeason = new(StringComparer.Ordinal)
    {
        ["Athens_053"] = "halloween", ["Sparta_053"] = "halloween",
        ["Athens_092"] = "spring_festival", ["Sparta_092"] = "spring_festival",
        ["Athens_112"] = "spring_festival", ["Sparta_112"] = "spring_festival",
        ["Athens_119"] = "igg_anniversary", ["Sparta_119"] = "igg_anniversary",
        ["Athens_128"] = "easter", ["Sparta_128"] = "easter",
        ["Athens_117"] = "easter", ["Sparta_117"] = "easter",
        ["Athens_126"] = "carnival", ["Sparta_126"] = "carnival",
        ["Athens_032"] = "mid_autumn", ["Sparta_026"] = "mid_autumn",
        ["Athens_031"] = "oriental_cuisine", ["Sparta_025"] = "oriental_cuisine",
        ["Athens_012"] = Thanksgiving, ["Sparta_012"] = Thanksgiving,
        ["Athens_035"] = Christmas, ["Sparta_035"] = Christmas
    };

    // Envoys no map publishes, added while their season is open. Placement = the reference capture's own
    // (CapturedNpcPlacements: Athens_012 5152 / 0x111, Sparta_012 5011 / 0x11, both at 64,-161 facing 3). The
    // appearance word matters: with the ini word 17 the client draws the NPC but a click opens nothing (gwhost,
    // 2026-11-08), as the capture comment on 0x111 vs 0x11 warns.
    // W3 seasons mid-autumn: Athens_032 (Lunar Priest) has no npc_spawn_definitions row on map 1, and its captured id
    // 5172 is the published Athens_033's there. Placement = the capture's (73,-168 facing 1.7, template
    // Athens_032_Male10, Athens' word 0x111); GENERATED id 5770, used by nothing else (capture, actor catalog,
    // npc_spawn_definitions, event NPCs 5700-5733, Christmas 5740/5741, Halloween/May Day 5750-5753, Easter 5760,
    // spawn pool 48000-48999, training ground 49001-49684, Troy, Pan; not a player id). Sparta_026 is published
    // (5024 / 0x11, the capture's own word, like the working Sparta Wedding Priest 5056) and needs no row here.
    // W3 Christmas: Santa Claus has no capture. GENERATED: ids 5740/5741 (used by nothing else: the capture,
    // npc_spawn_definitions, event NPCs 5700-5733), the appearance word of each capital's captured NPCs, and the one
    // free slot of the capitals' seasonal-envoy grid (x 64/68/72, z -152/-156/-161), 68,-152 facing 3.
    private static readonly (short MapId, string Scene, string Key, string Template, uint ObjectId, uint Appearance,
        float X, float Z, float Facing)[] Added =
    [
        (1, "Athens", "Athens_012", "Athens_012_MaleMerchant3", 5152, 0x111, 64f, -161f, 3f),
        (0, "Sparta", "Sparta_012", "Sparta_012_MaleMerchant3", 5011, 0x11, 64f, -161f, 3f),
        (1, "Athens", "Athens_035", "Athens_035_FemMale9", 5740, 0x111, 68f, -152f, 3f),
        (0, "Sparta", "Sparta_035", "Sparta_035_FemMale9", 5741, 0x11, 68f, -152f, 3f),
        (1, "Athens", "Athens_032", "Athens_032_Male10", AthensLunarPriestId, 0x111, 73f, -168f, 1.7f)
    ];

    /// <summary>W3 seasons mid-autumn: the Athens Lunar Priest's object id (GENERATED, see <see cref="Added"/>).</summary>
    public const uint AthensLunarPriestId = 5770;

    private static Dictionary<string, SeasonWindow> _windows = [];

    public static void Install(IEnumerable<SeasonWindow> windows) =>
        Volatile.Write(ref _windows, windows.ToDictionary(static w => w.Key, StringComparer.Ordinal));

    public static bool IsOpen(string key, DateTimeOffset at) =>
        Volatile.Read(ref _windows).TryGetValue(key, out var window) && window.IsOpenAt(at);

    public static string? SeasonOf(string npcKey) => EnvoySeason.GetValueOrDefault(npcKey);

    /// <summary>
    /// The year of the season occurrence open at <paramref name="at"/>: the year its window started, so a window
    /// that crosses New Year keeps one Christmas model. Switched on with no dates (or closed) = the UTC year.
    /// </summary>
    public static int SeasonYear(string key, DateTimeOffset at)
    {
        if (!Volatile.Read(ref _windows).TryGetValue(key, out var window) ||
            window.StartUtc is not { } start || window.EndUtc is not { } end)
            return at.UtcDateTime.Year;
        if (!window.Yearly) return start.UtcDateTime.Year;
        var years = at.UtcDateTime.Year - start.UtcDateTime.Year;
        for (var shift = Math.Max(0, years - 1); shift <= years; shift++)
            if (at >= start.AddYears(shift) && at < end.AddYears(shift)) return start.AddYears(shift).UtcDateTime.Year;
        return at.UtcDateTime.Year;
    }

    /// <summary>
    /// W3 Christmas: an Antler (4005) from a Little Deer in Athens or a Vicious Fawn in the Sparta suburbs while
    /// Christmas is open, season.christmas_antler_drop_percent of kills (GENERATED). The real client will not use
    /// Santa Gloves on a Little Deer ("...without a target", gwhost 2026-11-08), so the antlers come from kills.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> ChristmasDrops(short mapId, string templateKey, Guid deathEventId,
        DateTimeOffset at, int dropPercent)
    {
        if (deathEventId == Guid.Empty || !ChristmasRules.DropsAntlers(mapId, templateKey) || !IsOpen(Christmas, at))
            yield break;
        Span<byte> bytes = stackalloc byte[16];
        deathEventId.TryWriteBytes(bytes);
        if (BitConverter.ToUInt32(bytes[12..16]) % 10_000 < Math.Clamp(dropPercent, 0, 100) * 100)
            yield return ((uint)ChristmasRules.Antler, 0);
    }

    /// <summary>The map roster with closed seasons' envoys removed and open seasons' added envoys placed.</summary>
    public static List<NpcSpawnDefinition> Apply(short mapId, IReadOnlyList<NpcSpawnDefinition> roster, DateTimeOffset at)
    {
        var result = roster.Where(npc => SeasonOf(npc.NpcKey) is not { } season || IsOpen(season, at)).ToList();
        var keys = result.Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var ids = result.SelectMany(static npc => new[] { npc.ObjectId, npc.InteractionId }).ToHashSet();
        foreach (var npc in Added)
        {
            if (npc.MapId != mapId || keys.Contains(npc.Key) || !IsOpen(EnvoySeason[npc.Key], at)) continue;
            if (ids.Contains(npc.ObjectId))
            {
                Console.WriteLine($"[season] {npc.Key} not placed: object {npc.ObjectId} is taken on map {mapId}");
                continue;
            }
            // ROUND3 W3 Christmas: per-NPC position (was 64,-161 for every added envoy).
            result.Add(new NpcSpawnDefinition(mapId, npc.Scene, npc.Key, npc.Template, npc.ObjectId, npc.X, npc.Z,
                npc.ObjectId, npc.Appearance, npc.Facing, [], []));
        }

        return result;
    }

    /// <summary>
    /// Thanksgiving turkey items from any monster on Marathon (map 11, Athens' envoy text) or Peloponnese (map 13,
    /// Sparta's) while the season is open. The client has no turkey monster, so ordinary monsters there carry them
    /// (GENERATED, console question): season.thanksgiving_drop_percent = one of Savory 40 % / Tender 40 % /
    /// Certificate 20 %; season.thanksgiving_card_percent = a Greetings Card. Rolled from the death id.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> ThanksgivingDrops(short mapId, Guid deathEventId, DateTimeOffset at,
        int dropPercent, int cardPercent)
    {
        if (mapId is not (11 or 13) || deathEventId == Guid.Empty || !IsOpen(Thanksgiving, at)) yield break;
        Span<byte> bytes = stackalloc byte[16];
        deathEventId.TryWriteBytes(bytes);
        var a = BitConverter.ToUInt32(bytes[..4]) % 10_000;
        var b = BitConverter.ToUInt32(bytes[4..8]) % 10_000;
        var c = BitConverter.ToUInt32(bytes[8..12]) % 100;
        if (a < Math.Clamp(dropPercent, 0, 100) * 100)
            yield return (c < 40 ? 3945u : c < 80 ? 3946u : 3947u, 0);
        if (b < Math.Clamp(cardPercent, 0, 100) * 100)
            yield return (3948u, 1);
    }

    /// <summary>"key:enabled:start:end:yearly,..." (epoch seconds, 0 = none) from the store's string_agg.</summary>
    public static IReadOnlyList<SeasonWindow> Parse(string? rows)
    {
        var list = new List<SeasonWindow>();
        foreach (var part in (rows ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 5 || !long.TryParse(bits[2], out var start) || !long.TryParse(bits[3], out var end)) continue;
            list.Add(new SeasonWindow(bits[0], bits[1] == "1",
                start > 0 ? DateTimeOffset.FromUnixTimeSeconds(start) : null,
                end > 0 ? DateTimeOffset.FromUnixTimeSeconds(end) : null, bits[4] == "1"));
        }

        return list;
    }
}
