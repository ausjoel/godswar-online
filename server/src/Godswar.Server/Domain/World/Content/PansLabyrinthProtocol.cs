namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Pan's Labyrinth (runtime map 40, scene <c>Pans_Labyrinth</c>). GATED OFF: nothing
/// here is reachable unless the process runs with <c>GODSWAR_PAN_LABYRINTH=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Evidence and its limits are in <c>Development/pan-labyrinth/DESIGN.md</c>. In short:
/// the entry button (<c>218</c>), the two refusals (<c>2705</c>/<c>2706</c>), the
/// letter item (<c>3898</c>), the NPC keys and templates, the level band and the
/// weekend window are the client's own data. No capture of the original exchange
/// exists. The arrival point, every NPC position, the appearance words and the
/// servants' rewards are GENERATED: the client ships an empty
/// <c>Monster/Pans_Labyrinth/Address.ini</c> and no map-40 spawns.
/// </para>
/// </remarks>
internal static class PansLabyrinthProtocol
{
    public const string FeatureVariable = "GODSWAR_PAN_LABYRINTH";

    /// <summary><c>schedule</c> (default), <c>always</c> or <c>never</c>; for test servers.</summary>
    public const string WindowVariable = "GODSWAR_PAN_LABYRINTH_WINDOW";

    public const byte MapId = 40;

    public const string SceneKey = "Pans_Labyrinth";

    /// <summary>
    /// <c>NF_L0_218</c> "*To Pan's Labyrinth." on page 1 of <c>NpcFunTranmit.lua</c>.
    /// Its single-channel twin 1218 is not offered: its label is commented out in
    /// the shipped <c>LuaText.lua</c>.
    /// </summary>
    public const int EntrySubId = 218;

    /// <summary>"Right now is not the time to visit Pan's Labyrinth." (<c>NF_L0_2705</c>).</summary>
    public const int ClosedResultSubId = 2705;

    /// <summary>"You need an invitation letter before I can send you to Pan's Labyrinth." (<c>NF_L0_2706</c>).</summary>
    public const int NoLetterResultSubId = 2706;

    /// <summary>
    /// <c>NF_L0_34</c> "level requirement not met". The client has no Pan-specific
    /// level line; this is the answer the reference capture gave on the same NPC
    /// for the Cursed Land button (<see cref="CursedLandTransportProtocol.LevelRequirementResultSubId"/>).
    /// </summary>
    public const int LevelResultSubId = CursedLandTransportProtocol.LevelRequirementResultSubId;

    /// <summary>
    /// The dialog index every Event Transporter reply carries. The capture answered
    /// two clicks of this NPC (<c>200</c> -> <c>2001</c>, <c>600</c>/<c>202</c> ->
    /// <c>1010</c>) with index 1 although the script draws both results in its
    /// <c>Index == 2</c> block, so 2705/2706 go out the same way.
    /// </summary>
    public const int DialogIndex = CursedLandTransportProtocol.EventTransporterDialogIndex;

    /// <summary>"Pan's Invitation Letter" (client item 3898, <c>Syrinx7</c>), consumed on entry.</summary>
    public const uint InvitationLetterItemId = 3898;

    /// <summary>Client Event Transporter text: "Pan's Labyrinth 12:00pm - 6:00pm 70-140 Weekends".</summary>
    public const int MinimumLevel = 70;

    /// <summary>
    /// The client line says 140 and IGG's guide says 130; the admin F-70 ("any limits that are 120-140 should be raised
    /// to 200") makes it 200, so the band is 70-200.
    /// </summary>
    // FINALISE2 (the admin F-70): every 120-140 cap goes to 200 (was 140).
    public const int MaximumLevel = 200;

    public static readonly TimeOnly WindowOpens = new(12, 0);

    public static readonly TimeOnly WindowCloses = new(18, 0);

    /// <summary>GENERATED. IGG: entrants are "placed at the center"; (0, 0.5) is the nearest clear cell.</summary>
    public static readonly (float X, float Z) Arrival = (0f, 0.5f);

    public const uint FirstObjectId = 7801;

    public const int ServantCount = 9;

    public const int HomeTeleportFunction =CursedLandTransportProtocol.HomeTeleportFunction;

    public const float InteractionRadius = 12f;

    private const uint ServiceAppearance = 0x211u;

    private const uint HomeAppearance = 0x111u;

    /// <summary>
    /// GENERATED servant points: farthest-point spread over the cells reachable from
    /// the centre on the client block plane (<c>Development/pan-labyrinth/place_npcs.py</c>).
    /// </summary>
    private static readonly (float X, float Z)[] ServantPoints =
    [
        (-215f, 230.5f), (213f, 227.5f), (-215f, -220.5f), (213f, -220.5f), (-219f, 10.5f),
        (217f, 9.5f), (-3f, 213.5f), (-3f, -212.5f), (-114f, -103.5f)
    ];

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(FeatureVariable) == "1";

    public static string WindowMode =>
        Environment.GetEnvironmentVariable(WindowVariable)?.Trim().ToLowerInvariant() switch
        {
            "always" => "always",
            "never" => "never",
            _ => "schedule"
        };

    /// <summary>Saturdays and Sundays 12:00-18:00 realm time, start-inclusive, end-exclusive.</summary>
    public static bool IsOpenAt(DateTime realmLocal) =>
        WindowMode switch
        {
            "always" => true,
            "never" => false,
            _ => realmLocal.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday &&
                TimeOnly.FromDateTime(realmLocal) >= WindowOpens &&
                TimeOnly.FromDateTime(realmLocal) < WindowCloses
        };

    public static bool IsLevelEligible(int level) => level is >= MinimumLevel and <= MaximumLevel;

    public static bool IsEntryClick(string npcKey, int dialogIndex, int subId) =>
        IsEnabled &&
        CursedLandTransportProtocol.IsEventTransporter(npcKey) &&
        dialogIndex == DialogIndex &&
        subId == EntrySubId;

    /// <summary>The Event Transporter's opening page, with 218 appended while the feature is on.</summary>
    public static IReadOnlyList<int> WithEntryButton(string npcKey, int dialogIndex, IReadOnlyList<int> menu) =>
        IsEnabled &&
        CursedLandTransportProtocol.IsEventTransporter(npcKey) &&
        dialogIndex == DialogIndex &&
        !menu.Contains(EntrySubId)
            ? [.. menu, EntrySubId]
            : menu;

    public static bool IsPanNpc(NpcSpawnDefinition npc) =>
        npc.MapId == MapId && npc.NpcKey.StartsWith("Pans_Labyrinth_0", StringComparison.Ordinal);

    /// <summary>Servant number 1-9 for <c>Pans_Labyrinth_001</c>-<c>009</c>.</summary>
    public static bool TryGetServant(NpcSpawnDefinition npc, out int servant)
    {
        servant = 0;
        return IsPanNpc(npc) &&
            int.TryParse(npc.NpcKey.AsSpan("Pans_Labyrinth_".Length), out servant) &&
            servant is >= 1 and <= ServantCount;
    }

    public static bool IsHomeTransporter(NpcSpawnDefinition npc) =>
        IsPanNpc(npc) && npc.NpcKey == "Pans_Labyrinth_011";

    public static bool IsAvatar(NpcSpawnDefinition npc) =>
        IsPanNpc(npc) && npc.NpcKey == "Pans_Labyrinth_010";

    /// <summary>The eleven map-40 actors. Every position and appearance word is GENERATED.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> Npcs()
    {
        var npcs = new List<NpcSpawnDefinition>(ServantCount + 2);
        for (var index = 0; index < ServantCount; index++)
        {
            var key = $"Pans_Labyrinth_{index + 1:000}";
            npcs.Add(Create(key, $"{key}_Losebook1", FirstObjectId + (uint)index,
                ServantPoints[index].X, ServantPoints[index].Z, ServiceAppearance));
        }

        npcs.Add(Create("Pans_Labyrinth_010", "Pans_Labyrinth_010_Male36",
            FirstObjectId + 9, 4f, 0.5f, ServiceAppearance));
        npcs.Add(Create("Pans_Labyrinth_011", "Pans_Labyrinth_011_Sicily1",
            FirstObjectId + 10, -4f, 0.5f, HomeAppearance));
        return npcs;
    }

    /// <summary>Adds the actors to a map-40 roster while the feature is on; otherwise returns it unchanged.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> AddNpcs(int mapId, IReadOnlyList<NpcSpawnDefinition> existing)
    {
        if (!IsEnabled || mapId != MapId)
        {
            return existing;
        }

        var result = existing.Where(npc => !IsPanNpc(npc)).ToList();
        foreach (var npc in Npcs())
        {
            if (result.Any(other => other.ObjectId == npc.ObjectId || other.InteractionId == npc.InteractionId))
            {
                throw new InvalidDataException("Pan's Labyrinth actor identity collides with published content.");
            }

            result.Add(npc);
        }

        return result;
    }

    /// <summary>
    /// GENERATED reward for the n-th servant found in one entry (n = 1..9): n tenths of a
    /// percent of the character's current level-up experience (4.5% for all nine, before
    /// the exp.events rate, which is x10 on live) and one talent point. IGG says
    /// the servants give EXP, talent points, zodiac energy and items; only the first
    /// two are paid here.
    /// </summary>
    public static (int Experience, int TalentPoints) ServantReward(int level, int ordinal)
    {
        var next = (long)Godswar.Server.State.PlayerExperienceCatalog.GetNextLevelExperience(level);
        var experience = (int)Math.Clamp(next * Math.Clamp(ordinal, 1, ServantCount) / 1000, 1, int.MaxValue);
        return (experience, 1);
    }

    private static NpcSpawnDefinition Create(string key, string template, uint objectId, float x, float z, uint appearance) =>
        new(MapId, SceneKey, key, template, objectId, x, z, objectId, appearance, 0f, [], []);
}
