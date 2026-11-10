namespace Godswar.Server.Domain.World.Content;

internal sealed record TransporterDestination(
    string NpcKey,
    uint InteractionId,
    short SourceMapId,
    int SubId,
    short TargetMapId,
    short ArrivalAnchorSourceMapId,
    int MinimumLevel,
    int LevelRequirementResultSubId,
    string DisplayName);

/// <summary>
/// Finite server-authored contract for the ordinary world Transporters that
/// are present in the reviewed NPC publication. Menu identifiers and level
/// failure identifiers are native NpcFunTranmit values; destination maps use
/// the reviewed world topology.
/// </summary>
internal static class TransporterProtocol
{
    public const uint SpartaNpcId = 5039;
    // The client is handed the capture's own id: CapturedNpcPlacementPolicy
    // renumbers Athens' city npcs from the published catalog to the capture, and
    // the client echoes back whatever it was handed. The published dialogue
    // baseline still carries the catalog's value, and the npc content tables are
    // append-only (a changed row needs a new revision), so both ids are accepted
    // until the content is republished.
    public const uint AthensNpcId = 5179;
    public const uint PublishedAthensNpcId = 5180;
    public const uint MycenaeNpcId = 59721;
    public const int DialogIndex = 1;
    public const int ResultDialogIndex = 2;
    public const int InitialRequestSubId = -1;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const int AthensLevelRequirementResultSubId = 1000;
    public const int SpartaLevelRequirementResultSubId = 1001;
    public const int GenericLevelRequirementResultSubId = 2001;

    /// <summary>FINALISE2 (F-19): NpcFunTranmit Index 1 sub 10001 = NF_L0_10004 "Mycenae opens from 12:00 - 23:55."</summary>
    public const int MycenaeClosedResultSubId = 10001;

    /// <summary>FINALISE2 (F-19): NpcFunTranmit Index 1 sub 10003 = NF_L0_10003 "You need 20,000 Silver!".</summary>
    public const int MycenaeSilverResultSubId = 10003;
    public const float MaximumInteractionDistance = 12f;

    // Mycenae is the only gateway to the north. The client labels sub-ID 11 "Go to Mycenae" (NF_L0_213).
    public const int MycenaeMenuSubId = 11;
    public const int MycenaeMinimumLevel = 130; // the admin c01 (2026-11-03): back to the original 130 (c10 had made it 155)
    // Olympia keeps the client's existing label for this sub-ID, "Go to Olympia" (NF_L0_214).
    public const int OlympiaMenuSubId = 1088;
    public const int DelphiMenuSubId = 1089;
    public const int LarissaMenuSubId = 1090;
    // These three need the client's NpcFunTranmit.lua labels (NF_L0_219 to 221). An older client
    // shows no button for them, so they do nothing there.
    public const int DerveniMenuSubId = 1091;
    public const int ElassonMenuSubId = 1092;
    public const int OlympusMenuSubId = 1093;
    public const int DelphiMinimumLevel = 135;
    public const int OlympiaMinimumLevel = 145;
    // FINALISE2 (the admin F-20): Derveni is a south map you can walk into from Argolis, Plataea and Thebes, so its button
    // uses a south level. 100 is GENERATED (the admin asked for "a south level"; the reason: the lowest Derveni elite on the
    // GodsArena wiki is Lv100, elites 100-120, boss 106). Was 155.
    public const int DerveniMinimumLevel = 100;
    public const int LarissaMinimumLevel = 165;
    public const int ElassonMinimumLevel = 175;
    public const int OlympusMinimumLevel = 185;

    public static readonly TimeSpan MenuContextLifetime =
        TimeSpan.FromMinutes(2);

    private static readonly int[] SpartaMenuValues =
        [1, 3, 2, 8, 9, 10];
    private static readonly int[] AthensMenuValues =
        [4, 6, 5, 7, 9, 10];
    private static readonly int[] MycenaeMenuValues =
        [1088, 1089, 1090];

    // Sent after the published capital menus, never part of them: those lists are hashed into the
    // append-only NPC dialogue publication, so changing them would stop the server starting.
    private static readonly int[] CapitalExtensionMenuValues =
        [MycenaeMenuSubId, DelphiMenuSubId, OlympiaMenuSubId, DerveniMenuSubId, LarissaMenuSubId, ElassonMenuSubId];

    // The client window has 12 buttons and a capital already uses 6, so Olympus is only on Mycenae's list.
    private static readonly int[] MycenaeExtensionMenuValues =
        [DerveniMenuSubId, ElassonMenuSubId, OlympusMenuSubId];

    private static readonly TransporterDestination[] DestinationValues =
    [
        // Sparta progression chain.
        new("Sparta_042", SpartaNpcId, 0, 1, 4, 0, 1,
            SpartaLevelRequirementResultSubId, "Suburbs of Sparta"),
        new("Sparta_042", SpartaNpcId, 0, 2, 5, 13, 40,
            SpartaLevelRequirementResultSubId, "Nemea"),
        new("Sparta_042", SpartaNpcId, 0, 3, 13, 4, 30,
            SpartaLevelRequirementResultSubId, "Peloponnesus"),
        new("Sparta_042", SpartaNpcId, 0, 8, 14, 5, 70,
            SpartaLevelRequirementResultSubId, "Nemea Forest"),
        new("Sparta_042", SpartaNpcId, 0, 9, 8, 14, 100,
            SpartaLevelRequirementResultSubId, "Thermopylae"),
        new("Sparta_042", SpartaNpcId, 0, 10, 9, 19, 130,
            SpartaLevelRequirementResultSubId, "Thebes"),

        // Athens progression chain.
        new("Athens_041", AthensNpcId, 1, 4, 2, 1, 1,
            AthensLevelRequirementResultSubId, "Suburbs of Athens"),
        new("Athens_041", AthensNpcId, 1, 5, 3, 11, 40,
            AthensLevelRequirementResultSubId, "Parnitha"),
        new("Athens_041", AthensNpcId, 1, 6, 11, 2, 30,
            AthensLevelRequirementResultSubId, "Marathon"),
        new("Athens_041", AthensNpcId, 1, 7, 12, 3, 70,
            AthensLevelRequirementResultSubId, "Parnitha Port"),
        new("Athens_041", AthensNpcId, 1, 9, 8, 12, 100,
            AthensLevelRequirementResultSubId, "Thermopylae"),
        new("Athens_041", AthensNpcId, 1, 10, 9, 19, 130,
            AthensLevelRequirementResultSubId, "Thebes"),

        // Both capitals to Mycenae, arriving inside its Olympia gate. The capital level pages
        // list only their stock destinations, so the gate answers the generic page.
        new("Sparta_042", SpartaNpcId, 0, MycenaeMenuSubId, 6, 7,
            MycenaeMinimumLevel, GenericLevelRequirementResultSubId, "Mycenae"),
        new("Athens_041", AthensNpcId, 1, MycenaeMenuSubId, 6, 7,
            MycenaeMinimumLevel, GenericLevelRequirementResultSubId, "Mycenae"),
        new("Sparta_042", SpartaNpcId, 0, DelphiMenuSubId, 20, 7,
            DelphiMinimumLevel, GenericLevelRequirementResultSubId, "Delphi Forest"),
        new("Athens_041", AthensNpcId, 1, DelphiMenuSubId, 20, 7,
            DelphiMinimumLevel, GenericLevelRequirementResultSubId, "Delphi Forest"),
        new("Sparta_042", SpartaNpcId, 0, OlympiaMenuSubId, 7, 6,
            OlympiaMinimumLevel, GenericLevelRequirementResultSubId, "Olympia"),
        new("Athens_041", AthensNpcId, 1, OlympiaMenuSubId, 7, 6,
            OlympiaMinimumLevel, GenericLevelRequirementResultSubId, "Olympia"),
        new("Sparta_042", SpartaNpcId, 0, DerveniMenuSubId, 15, 9,
            DerveniMinimumLevel, GenericLevelRequirementResultSubId, "Derveni"),
        new("Athens_041", AthensNpcId, 1, DerveniMenuSubId, 15, 9,
            DerveniMinimumLevel, GenericLevelRequirementResultSubId, "Derveni"),
        new("Sparta_042", SpartaNpcId, 0, LarissaMenuSubId, 10, 20,
            LarissaMinimumLevel, GenericLevelRequirementResultSubId, "Larissa"),
        new("Athens_041", AthensNpcId, 1, LarissaMenuSubId, 10, 20,
            LarissaMinimumLevel, GenericLevelRequirementResultSubId, "Larissa"),
        new("Sparta_042", SpartaNpcId, 0, ElassonMenuSubId, 22, 10,
            ElassonMinimumLevel, GenericLevelRequirementResultSubId, "Elasson"),
        new("Athens_041", AthensNpcId, 1, ElassonMenuSubId, 22, 10,
            ElassonMinimumLevel, GenericLevelRequirementResultSubId, "Elasson"),

        // Mycenae's outbound world destinations.
        new("Mycenae_All_013", MycenaeNpcId, 6, OlympiaMenuSubId, 7, 6,
            OlympiaMinimumLevel, GenericLevelRequirementResultSubId, "Olympia"),
        new("Mycenae_All_013", MycenaeNpcId, 6, DelphiMenuSubId, 20, 7,
            DelphiMinimumLevel, GenericLevelRequirementResultSubId, "Delphi Forest"),
        new("Mycenae_All_013", MycenaeNpcId, 6, LarissaMenuSubId, 10, 20,
            LarissaMinimumLevel, GenericLevelRequirementResultSubId, "Larissa"),
        new("Mycenae_All_013", MycenaeNpcId, 6, DerveniMenuSubId, 15, 9,
            DerveniMinimumLevel, GenericLevelRequirementResultSubId, "Derveni"),
        new("Mycenae_All_013", MycenaeNpcId, 6, ElassonMenuSubId, 22, 10,
            ElassonMinimumLevel, GenericLevelRequirementResultSubId, "Elasson"),
        new("Mycenae_All_013", MycenaeNpcId, 6, OlympusMenuSubId, 21, 22,
            OlympusMinimumLevel, GenericLevelRequirementResultSubId, "Olympus")
    ];

    public static IReadOnlyList<int> SpartaInitialMenuSubIds { get; } =
        Array.AsReadOnly(SpartaMenuValues);

    public static IReadOnlyList<int> AthensInitialMenuSubIds { get; } =
        Array.AsReadOnly(AthensMenuValues);

    public static IReadOnlyList<int> MycenaeInitialMenuSubIds { get; } =
        Array.AsReadOnly(MycenaeMenuValues);

    public static IReadOnlyList<TransporterDestination> Destinations { get; } =
        Array.AsReadOnly(DestinationValues);

    public static IReadOnlyList<int> GetExtensionMenu(string npcKey, uint interactionId) =>
        (npcKey, interactionId) switch
        {
            ("Sparta_042", SpartaNpcId) or
            ("Athens_041", AthensNpcId) or
            ("Athens_041", PublishedAthensNpcId) => CapitalExtensionMenuValues,
            ("Mycenae_All_013", MycenaeNpcId) => MycenaeExtensionMenuValues,
            _ => []
        };

    public static bool IsEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            ("Sparta_042", SpartaNpcId) or
            ("Athens_041", AthensNpcId) or
            ("Athens_041", PublishedAthensNpcId) or
            ("Mycenae_All_013", MycenaeNpcId);

    public static bool TryGetInitialMenu(
        string npcKey,
        uint interactionId,
        out IReadOnlyList<int> subIds)
    {
        subIds = (npcKey, interactionId) switch
        {
            ("Sparta_042", SpartaNpcId) => SpartaInitialMenuSubIds,
            ("Athens_041", AthensNpcId) or
            ("Athens_041", PublishedAthensNpcId) => AthensInitialMenuSubIds,
            ("Mycenae_All_013", MycenaeNpcId) => MycenaeInitialMenuSubIds,
            _ => []
        };
        return subIds.Count != 0;
    }

    public static bool TryResolveDestination(
        string npcKey,
        uint interactionId,
        short sourceMapId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out TransporterDestination destination)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        destination = null!;
        if (dialogIndex != DialogIndex || !HasEmptyPath(arguments))
        {
            return false;
        }

        // The client is handed the capture's id (5179) while the published
        // dialogue baseline still carries the catalog's (5180), so a destination
        // resolves under either - the sub-id is what actually names the entry.
        var acceptedIds = npcKey == "Athens_041"
            ? new[] { AthensNpcId, PublishedAthensNpcId }
            : new[] { interactionId };
        destination = DestinationValues.FirstOrDefault(candidate =>
            string.Equals(candidate.NpcKey, npcKey, StringComparison.Ordinal) &&
            Array.IndexOf(acceptedIds, candidate.InteractionId) >= 0 &&
            candidate.SourceMapId == sourceMapId &&
            candidate.SubId == subId)!;
        return destination is not null;
    }

    private static bool HasEmptyPath(IReadOnlyList<int> arguments)
    {
        if (arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != -1)
            {
                return false;
            }
        }

        return true;
    }
}
