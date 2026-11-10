namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// HERA'S LABYRINTH (the admin F-161, Pan chain W3 part 3): maps 30 (<c>Labyrint2</c>, the Athenian "Cyclades") and 31
/// (<c>Labyrinth</c>, the Spartan "Dodecanese"), the Event Transporter's 500 / 501, the nine-isle portal maze, Zeus'
/// Envoy (006) and Zeus' Envoys 1-5 with the Zeus' Oracle chain. GATED OFF unless <c>GODSWAR_HERA_LABYRINTH=1</c>;
/// with the switch off nothing is routed, nothing is placed, and the captured menus are unchanged.
/// </summary>
/// <remarks>
/// Evidence (server/tools/heras-labyrinth/README.md):
/// - [IGG] acid=121: Lv 50+, "Labyrinth Quest Envoy" then the Event Transporter; Athens: Cyclades, Sparta: Dodecanese;
///   all day; nine isles with four teleportation spots each; Zeus' Envoy gives Zeus' Oracle 1; Envoy N takes Oracle N
///   and gives Oracle N+1, up to Envoy 5; rewards for every step, better nearer the end; the chain once a day; unused
///   oracles are kept. acid=164: Hera's Labyrinth is one of the four sources of the two Pan's Invitation map parts a week.
/// - [client] NpcFunTranmit.lua 500 "*Teleport to the Cyclades" / 501 "*Teleport to the Dodecanese" (both with the
///   TS500/TS501 confirm box); captures: Athens_072 (5210/5211) advertised 500, Sparta_072 advertised 501.
/// - [client] Quest.xml 1275 (Athens, responder Labyrinth_006 on map 30) and 275 (Sparta, Labyrint2_006 on map 31),
///   responder point (167, -157). So map 30 carries the <c>Labyrinth_00x</c> actors and map 31 the <c>Labyrint2_00x</c>.
/// - [client] NpcFunCredit.lua (function 2) page 1: 1000/1100/1200/1300/1400 "You don't have Zeus' Oracle N";
///   2N00+band reward lines (EXP, TP and 50 Zodiac Energy; band 10 is skipped: 2N10 is "already deposited Oracle N,
///   come during the next round"); 102 "Not enough space"; 103 "part 3 twice per week".
/// - [client] Monster.ini count=0 and Address.ini AddressCount=0 for both maps: no monsters, no boss.
/// GENERATED: the isle numbering, landings, arrival and envoy positions (HeraLabyrinthLayout.Generated.cs), the
/// level-to-band mapping, the window default, Zeus' Envoy's home button and its "open = Oracle 1" behaviour.
/// </remarks>
internal static class HeraLabyrinthProtocol
{
    public const string FeatureVariable = "GODSWAR_HERA_LABYRINTH";
    public const string WindowVariable = "GODSWAR_HERA_LABYRINTH_WINDOW";

    public const byte CycladesMapId = 30;     // Athens (camp 1)
    public const byte DodecaneseMapId = 31;   // Sparta (camp 0)
    public const string CycladesScene = "Labyrint2";
    public const string DodecaneseScene = "Labyrinth";

    public const int CycladesSubId = 500;
    public const int DodecaneseSubId = 501;
    public const int MinimumLevel = 50;        // IGG and quest 275 / 1275
    public const int MaximumLevel = 200;       // quest cap 120; the admin F-70
    public const int LevelResult = 2001;       // NF_L0_34, the captured Event Transporter level refusal
    public const int ClosedResult = 513;       // NF_L0_513 "You are not qualified to go."

    public const int EnvoyFunction = 2;        // NPC_FLAG_SYS_CREDIT_EXCHANGE, NpcFunCredit.lua
    public const int HomeFunction = CursedLandTransportProtocol.HomeTeleportFunction;
    public const float InteractionRadius = 12f;

    public const uint FirstOracleItemId = 3814; // Zeus' Oracle 1..5 = 3814..3818 (Overlap 1, BindType 1)
    public const int EnvoyCount = 5;

    public const int BagFullResult = 102;      // NF_L0_BD5021
    public const int PartThreeTwiceAWeekResult = 103; // NF_L0_BD5032

    public const uint FirstCycladesObjectId = 7851;
    public const uint FirstDodecaneseObjectId = 7861;

    private const uint ServiceAppearance = 0x211u;

    public static bool IsEnabled => Environment.GetEnvironmentVariable(FeatureVariable) == "1";

    /// <summary><c>always</c> (default: IGG "All day") or <c>never</c>.</summary>
    public static string WindowMode =>
        Environment.GetEnvironmentVariable(WindowVariable)?.Trim().ToLowerInvariant() == "never" ? "never" : "always";

    public static bool IsOpenAt(DateTime realmLocal) => WindowMode == "always";

    public static bool IsLabyrinthMap(int mapId) => mapId is CycladesMapId or DodecaneseMapId;

    public static uint OracleItemId(int number) =>
        number is >= 1 and <= EnvoyCount ? FirstOracleItemId + (uint)(number - 1) : throw new ArgumentOutOfRangeException(nameof(number));

    /// <summary>The map behind an Event Transporter button: 500 at Athens_072, 501 at Sparta_072 (as captured).</summary>
    public static bool TryResolveEntry(string npcKey, int dialogIndex, int subId, out byte mapId)
    {
        mapId = 0;
        if (!IsEnabled || dialogIndex != CursedLandTransportProtocol.EventTransporterDialogIndex)
        {
            return false;
        }

        if (npcKey == "Athens_072" && subId == CycladesSubId)
        {
            mapId = CycladesMapId;
        }
        else if (npcKey == "Sparta_072" && subId == DodecaneseSubId)
        {
            mapId = DodecaneseMapId;
        }

        return mapId != 0;
    }

    /// <summary>
    /// The Event Transporter's page 1 with the camp's own button while switched on: Athens shows 500 in place of the
    /// shared captured 501 (the Athens capture advertised 500); Sparta keeps 501. Off: the menu is returned as is.
    /// </summary>
    public static IReadOnlyList<int> WithEntryButton(string npcKey, int dialogIndex, IReadOnlyList<int> menu)
    {
        if (!IsEnabled || dialogIndex != CursedLandTransportProtocol.EventTransporterDialogIndex)
        {
            return menu;
        }

        if (npcKey == "Athens_072" && !menu.Contains(CycladesSubId))
        {
            return menu.Contains(DodecaneseSubId)
                ? [.. menu.Select(static entry => entry == DodecaneseSubId ? CycladesSubId : entry)]
                : [.. menu, CycladesSubId];
        }

        return npcKey == "Sparta_072" && !menu.Contains(DodecaneseSubId) ? [.. menu, DodecaneseSubId] : menu;
    }

    private static string Prefix(int mapId) => mapId == CycladesMapId ? "Labyrinth_" : "Labyrint2_";

    public static bool IsHeraNpc(NpcSpawnDefinition npc) =>
        IsEnabled && IsLabyrinthMap(npc.MapId) && npc.NpcKey.StartsWith(Prefix(npc.MapId), StringComparison.Ordinal);

    /// <summary>Zeus' Envoy (006): gives Oracle 1 and, GENERATED, the way home.</summary>
    public static bool IsZeusEnvoy(NpcSpawnDefinition npc) => IsHeraNpc(npc) && npc.NpcKey.EndsWith("_006", StringComparison.Ordinal);

    /// <summary>Zeus' Envoy 1-5.</summary>
    public static bool TryGetEnvoy(NpcSpawnDefinition npc, out int number)
    {
        number = 0;
        return IsHeraNpc(npc) && int.TryParse(npc.NpcKey.AsSpan(Prefix(npc.MapId).Length), out number) &&
            number is >= 1 and <= EnvoyCount;
    }

    // ---- rewards ---------------------------------------------------------------------------------------------------

    /// <summary>[client] NF_L0_2N0b EXP for envoy N (row N-1) and band slot (0-9, then 11, 12 at slots 10, 11).</summary>
    public static readonly int[][] RewardExperience =
    [
        [1500, 2000, 3000, 5000, 10000, 15000, 20000, 30000, 40000, 50000, 60000, 70000],
        [2000, 3000, 5000, 10000, 20000, 25000, 40000, 50000, 80000, 100000, 120000, 140000],
        [2500, 4000, 7000, 15000, 30000, 40000, 60000, 80000, 120000, 150000, 180000, 210000],
        [3000, 6000, 10000, 25000, 50000, 60000, 100000, 120000, 180000, 250000, 300000, 350000],
        [4000, 8000, 15000, 35000, 70000, 90000, 150000, 180000, 250000, 350000, 400000, 450000],
    ];

    /// <summary>[client] Talent Points per envoy: bands 0-9, then bands 11/12.</summary>
    public static readonly (int Low, int High)[] RewardTalentPoints = [(1, 1), (3, 4), (5, 6), (8, 9), (10, 15)];

    /// <summary>[client] "50 Zodiac Energy" in every reward line.</summary>
    public const int RewardZodiacEnergy = 50;

    /// <summary>
    /// GENERATED level band (the client lists twelve lines per envoy without levels): 50-59 -> 0, ... 140-149 -> 9,
    /// 150-159 -> 11, 160-200 -> 12. Returns the band number used in the reply (2N00 + band).
    /// </summary>
    public static int Band(int level) => level switch
    {
        < 60 => 0,
        < 150 => (level - 50) / 10,
        < 160 => 11,
        _ => 12
    };

    private static int Slot(int band) => band <= 9 ? band : band - 1;

    public static int RewardResult(int envoy, int band) => 2000 + (envoy - 1) * 100 + band;

    public static int MissingOracleResult(int envoy) => 1000 + (envoy - 1) * 100;

    public static int AlreadyDoneResult(int envoy) => 2010 + (envoy - 1) * 100;

    public static (int Experience, int TalentPoints) Reward(int envoy, int level) => RewardForBand(envoy, Band(level));

    /// <summary>The client's EXP / TP for envoy (oracle) N at a band number (0-9, 11, 12).</summary>
    public static (int Experience, int TalentPoints) RewardForBand(int envoy, int band)
    {
        var talent = RewardTalentPoints[envoy - 1];
        return (RewardExperience[envoy - 1][Slot(band)], band <= 9 ? talent.Low : talent.High);
    }

    public static string DailyCounter(int envoy) => $"hera_envoy{envoy}";

    // ---- capital War Supplier: the oracle deposit page (part 2) -------------------------------------------------

    /// <summary>NPC_FLAG_SYS_MATERIALBACK = 114, NpcFunMaterialBack.lua.</summary>
    public const int WarSupplierFunction = 114;

    /// <summary>"*Deposit Zeus' Oracle 1" .. "5" (NF_L0_SY1010-1014), drawn on the script's page 2.</summary>
    public const int WarSupplierFirstDepositButton = 1010;

    /// <summary>
    /// NF_L0_SY2000 "Please make a selection" (page 3, EndMessage). Page 3 has no bag-full line (3100 is drawn on
    /// page 2 only), so a bag-full refusal ends the window with this line; a chat line says why. GENERATED.
    /// </summary>
    public const int WarSupplierEndResult = 3001;

    /// <summary>The supplier keys routed to WarSupplierDialogue (Athens_076 / Sparta_076; War Supplier 3 unspawned).</summary>
    public static bool IsWarSupplier(string npcKey) =>
        npcKey is "Athens_076" or "Sparta_076" or "Marathon_All_010" or "Peloponnese_All_010";

    /// <summary>
    /// A page-3 click on the War Supplier: 1010-1014 = deposit Oracle 1-5. [client] NF_L0_SY1001: "the Zeus' Oracle
    /// deposit here is identical to that in the maze". Off: never claimed, so the captured page flow is unchanged.
    /// </summary>
    public static bool TryResolveWarSupplierDeposit(string npcKey, int dialogIndex, int selection, out int oracle)
    {
        oracle = selection - WarSupplierFirstDepositButton + 1;
        if (!IsEnabled || dialogIndex != WarSupplierFunction || !IsWarSupplier(npcKey) || oracle is < 1 or > EnvoyCount)
        {
            oracle = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// GENERATED: the supplier's page 3 only draws 2N00-2N09 (no 2N11 / 2N12), so Lv150+ is paid band 9 there,
    /// exactly what the drawn line says. In the maze the envoys keep their 11 / 12 bands.
    /// </summary>
    public static int WarSupplierBand(int level) => Math.Min(Band(level), 9);

    /// <summary>NF_L0_SY3002-3006 "You don't have Zeus' Oracle N in your bag" (page 3).</summary>
    public static int WarSupplierMissingResult(int oracle) => 3001 + oracle;

    // ---- daily announcement (part 2) ---------------------------------------------------------------------------

    /// <summary>
    /// Realm-local HH:mm times (comma separated) for the daily announcement, or <c>off</c>. Default <c>19:59</c> is
    /// GENERATED: the client has the line but no time, and the maze stays open all day (IGG "All day"; the Event
    /// Transporter's own table, NPCDescription Athens_072 / Sparta_072: "Labyrinth Anytime 50-140 Everyday").
    /// </summary>
    public const string AnnouncementVariable = "GODSWAR_HERA_LABYRINTH_ANNOUNCE";

    public const string DefaultAnnouncementTimes = "19:59";

    /// <summary>[client] Text/SysMsg/EveryDay.Dat entry 0, verbatim (printable ASCII, fits the centred notice).</summary>
    public const string AnnouncementText =
        "One minute to Hera's Labyrinth. Try to rescue Europa, who has been stranded there!";

    /// <summary>The announcement times, or none when off / unparsable / the switch or window is off.</summary>
    public static IReadOnlyList<TimeOnly> AnnouncementTimes()
    {
        if (!IsEnabled || WindowMode == "never")
        {
            return [];
        }

        var raw = Environment.GetEnvironmentVariable(AnnouncementVariable)?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            raw = DefaultAnnouncementTimes;
        }

        if (raw.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var times = new List<TimeOnly>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TimeOnly.TryParseExact(part, "H:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time))
            {
                times.Add(time);
            }
        }

        return times.Distinct().Order().ToArray();
    }

    /// <summary>
    /// Edge-triggered: true when an announcement time lies in (<paramref name="previous"/>, <paramref name="now"/>],
    /// both realm-local. The first tick after start-up (no previous) never fires, so a restart after the time does
    /// not repeat it; a gap longer than an hour (sleep, clock jump) is skipped rather than replayed.
    /// </summary>
    public static bool AnnouncementDue(IReadOnlyList<TimeOnly> times, DateTime? previous, DateTime now)
    {
        if (times.Count == 0 || previous is not { } before || now <= before || now - before > TimeSpan.FromHours(1))
        {
            return false;
        }

        foreach (var date in before.Date == now.Date ? [now.Date] : new[] { before.Date, now.Date })
        {
            foreach (var time in times)
            {
                var at = date + time.ToTimeSpan();
                if (at > before && at <= now)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public const string OracleOneCounter = "hera_oracle1";

    // ---- maze ------------------------------------------------------------------------------------------------------

    public static (float X, float Z) Arrival => HeraLabyrinthLayout.Arrival;

    /// <summary>
    /// The portal a walked segment passes within <paramref name="radius"/> of (the same shape as the Medusa island
    /// rings), and where it lands: a sign's isle landing, or Isle 1 for the dock pocket's scene_transport_01.
    /// </summary>
    public static bool TryResolvePortal(int mapId, float startX, float startZ, float endX, float endZ, float radius,
        out int sourceIsle, out int destinationIsle, out float landingX, out float landingZ)
    {
        sourceIsle = destinationIsle = 0;
        landingX = landingZ = 0f;
        if (!IsEnabled || !IsLabyrinthMap(mapId) || !float.IsFinite(startX) || !float.IsFinite(startZ) ||
            !float.IsFinite(endX) || !float.IsFinite(endZ) || !(radius > 0f))
        {
            return false;
        }

        var best = double.MaxValue;
        var radiusSquared = (double)radius * radius;
        var candidates = HeraLabyrinthLayout.Portals
            .Append((0, HeraLabyrinthLayout.ExitToIsleOne.X, HeraLabyrinthLayout.ExitToIsleOne.Z, 1));
        foreach (var (isle, x, z, destination) in candidates)
        {
            var d = DistanceToSegmentSquared(x, z, startX, startZ, endX, endZ);
            if (d <= radiusSquared && d < best)
            {
                best = d;
                sourceIsle = isle;
                destinationIsle = destination;
            }
        }

        if (destinationIsle == 0)
        {
            return false;
        }

        (landingX, landingZ) = HeraLabyrinthLayout.Landings[destinationIsle];
        return true;
    }

    private static double DistanceToSegmentSquared(float px, float pz, float sx, float sz, float ex, float ez)
    {
        double dx = ex - sx, dz = ez - sz;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared <= 0 ? 0 : Math.Clamp(((px - sx) * dx + (pz - sz) * dz) / lengthSquared, 0d, 1d);
        var ox = px - (sx + dx * t);
        var oz = pz - (sz + dz * t);
        return ox * ox + oz * oz;
    }

    // ---- rosters ---------------------------------------------------------------------------------------------------

    /// <summary>Zeus' Envoy (006) and Zeus' Envoys 1-5 for map 30 or 31.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> Npcs(byte mapId)
    {
        var scene = mapId == CycladesMapId ? CycladesScene : DodecaneseScene;
        var prefix = Prefix(mapId);
        var first = mapId == CycladesMapId ? FirstCycladesObjectId : FirstDodecaneseObjectId;
        var list = new List<NpcSpawnDefinition>(EnvoyCount + 1)
        {
            Create(mapId, scene, prefix + "006", $"{prefix}006_Migong6", first, HeraLabyrinthLayout.ZeusEnvoy)
        };
        for (var envoy = 1; envoy <= EnvoyCount; envoy++)
        {
            list.Add(Create(mapId, scene, $"{prefix}00{envoy}", $"{prefix}00{envoy}_Migong{envoy}", first + (uint)envoy,
                HeraLabyrinthLayout.Envoys[envoy]));
        }

        return list;
    }

    /// <summary>Adds the six actors to a map 30 / 31 roster while switched on; otherwise returns it unchanged.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> AddNpcs(int mapId, IReadOnlyList<NpcSpawnDefinition> existing)
    {
        if (!IsEnabled || !IsLabyrinthMap(mapId))
        {
            return existing;
        }

        var added = Npcs((byte)mapId);
        var keys = added.Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var result = existing.Where(npc => !keys.Contains(npc.NpcKey)).ToList();
        foreach (var npc in added)
        {
            if (result.Any(other => other.ObjectId == npc.ObjectId || other.InteractionId == npc.InteractionId))
            {
                throw new InvalidDataException($"Hera's Labyrinth actor {npc.NpcKey} collides with published content.");
            }

            result.Add(npc);
        }

        return result;
    }

    private static NpcSpawnDefinition Create(byte map, string scene, string key, string template, uint objectId,
        (float X, float Z) at) =>
        new(map, scene, key, template, objectId, at.X, at.Z, objectId, ServiceAppearance, 0f, [], []);
}
