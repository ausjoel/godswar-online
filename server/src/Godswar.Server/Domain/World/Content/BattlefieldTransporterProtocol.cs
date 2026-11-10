namespace Godswar.Server.Domain.World.Content;

internal enum BattlefieldDestinationKind : byte
{
    Pindus = 1,
    NiMiniLower = 2,
    NiMiniUpper = 3,
    DuelArena = 4,

    /// <summary>
    /// The Lelantine Farm defence. Its own transport button is sub id 282
    /// (single-channel twin 1282) on the same <c>NpcFunTranmit</c> index-1 menu
    /// the other battlefields use.
    /// </summary>
    LelantineFarm = 5
}

internal sealed record BattlefieldTransportDestination(
    BattlefieldDestinationKind Kind,
    string DisplayName,
    int SourceMapId,
    int TargetMapId,
    float TargetX,
    float TargetZ,
    int MinimumLevel,
    int MaximumLevel);

/// <summary>
/// Finite stock NpcFunTranmit surface for the two capital Battlefield
/// Transporters. Ni Mini is a second-page choice; Pindus and Duel Arena are
/// direct root actions. Destination coordinates are reviewed server-owned
/// arrivals and are never accepted from the client.
/// </summary>
internal static class BattlefieldTransporterProtocol
{
    public const uint SpartaNpcId = 5053;
    // The client is handed the capture's own id: CapturedNpcPlacementPolicy
    // renumbers Athens' city npcs from the published catalog to the capture, and
    // the client echoes back whatever it was handed. The published dialogue
    // baseline still carries the catalog's value, and the npc content tables are
    // append-only (a changed row needs a new revision), so both ids are accepted
    // until the content is republished.
    public const uint AthensNpcId = 5194;
    public const uint PublishedAthensNpcId = 5195;
    public const int DialogIndex = 1;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const int InitialRequestSubId = -1;
    public const int PindusSubId = 251;
    public const int AthensPindusSubId = 252;
    public const int NiMiniRootSubId = 274;
    public const int NiMiniLowerSubId = 272;
    public const int NiMiniUpperSubId = 273;
    public const int DuelArenaSubId = 1001;

    /// <summary>
    /// The Lelantine Farm entry button. Captured as
    /// <c>C2S 10069 {5194, 1, 282}</c> on 2026-09-26 at 21:00:36, which is the
    /// reference server's own transport button for the activity.
    /// </summary>
    public const int LelantineFarmSubId = 282;

    /// <summary>
    /// The "channel 1 only" twin the client also draws
    /// (<c>#1-1-1282</c>). Captured at 21:00:28 for the same NPC. This server
    /// runs one channel, so both buttons resolve to the same entry.
    /// </summary>
    public const int LelantineFarmSingleChannelSubId = 1282;

    /// <summary>
    /// The minimum level the farm's own refusal text names
    /// (<c>NF_L0_Y8005</c>: "the transportation level is 31 to 140").
    /// </summary>
    public const int LelantineFarmMinimumLevel = 31;

    /// <summary>The maximum level that same line names.</summary>
    // FINALISE2 (the admin F-68): 31-200, no cap (was 140, the Chinese refusal line).
    public const int LelantineFarmMaximumLevel = 200;

    /// <summary>FINALISE2 (the admin F-67): Ni Mini admits 50-89 into its one map (both client brackets; was 70-89).</summary>
    public const int NiMiniMinimumLevel = 50;

    public const int NiMiniMaximumLevel = 89;

    /// <summary>FINALISE2 (the admin F-70): the Pindus band with battlefield.pindus_band = 0 (was 31-120).</summary>
    public const int PindusCompiledMinimumLevel = 31;

    public const int PindusCompiledMaximumLevel = 200;

    public const int PindusResultDialogIndex = 2;
    public const int PindusMinimumLevelResultDialogIndex = 1;
    public const int PindusMinimumLevelResultSubId = 8002;
    public const int PindusClosedResultSubId = 8000;
    public const int PindusLevelResultSubId = 8001;
    // BATTLEFIELD2: NF_L0_Y7001 (level refusal, "lv.60-140" in the stock text) and NF_L0_Y7002 (the IGG Pindus times),
    // both drawn by NpcFunTranmit.lua Index 2 like 8000/8001.
    public const int PindusBandLevelResultSubId = 7001;
    public const int PindusIggClosedResultSubId = 7002;
    public const int NiMiniResultDialogIndex = 3;
    public const int NiMiniUnavailableResultSubId = 7000;

    /// <summary>
    /// The farm answers its own admission refusals under the activity's dialog
    /// index 47, not the transport menu's index 1: both refusal lines the
    /// client publishes sit in the <c>NpcFunFarm</c> branch
    /// (<c>NF_L0_FRAM</c> 7004/7005 in
    /// <c>artifacts/npc-dialogue-probe-tags/packet-contracts.md</c>).
    /// </summary>
    public const int LelantineFarmResultDialogIndex = 47;

    /// <summary>
    /// "利兰丁农场保卫战的传送等级是31级到140级,你的等级不合要求".
    /// </summary>
    public const int LelantineFarmLevelResultSubId = 7005;

    /// <summary>
    /// "利兰丁农场保卫战的传送时间是每周五晚上21:45到22:30!如有改动请看官网".
    /// </summary>
    public const int LelantineFarmClosedResultSubId = 7004;

    public const float MaximumInteractionDistance = 12f;

    public static readonly TimeSpan MenuContextLifetime =
        TimeSpan.FromMinutes(2);

    /// <summary>
    /// The farm's entry button for the Spartan transporter. Its capture is
    /// <c>C2S 10069 {5194, 1, 282}</c>, and 282 is the sub id both published
    /// farm entry texts carry (<c>NF_L0_Y8004</c>).
    /// </summary>
    public static IReadOnlyList<int> SpartaInitialMenuSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            PindusSubId,
            NiMiniRootSubId,
            DuelArenaSubId,
            LelantineFarmSubId
        });

    /// <summary>
    /// The farm's entry button for the Athenian transporter. The capture shows
    /// the same <c>282</c> click arriving from Athens, so the same root entry is
    /// published there and the single-channel twin is accepted as well.
    /// </summary>
    public static IReadOnlyList<int> AthensInitialMenuSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            AthensPindusSubId,
            NiMiniRootSubId,
            DuelArenaSubId,
            LelantineFarmSubId
        });

    public static IReadOnlyList<int> NiMiniPageSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            NiMiniUpperSubId
        });

    public static bool IsEditableNpc(string npcKey) =>
        npcKey is "Sparta_056" or "Athens_056";

    public static bool IsEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            ("Sparta_056", SpartaNpcId) or
            ("Athens_056", AthensNpcId) or
            ("Athens_056", PublishedAthensNpcId);

    public static bool TryGetInitialMenu(
        string npcKey,
        uint interactionId,
        out IReadOnlyList<int> menu)
    {
        menu = (npcKey, interactionId) switch
        {
            ("Sparta_056", SpartaNpcId) => SpartaInitialMenuSubIds,
            ("Athens_056", AthensNpcId) or
            ("Athens_056", PublishedAthensNpcId) => AthensInitialMenuSubIds,
            _ => []
        };
        return menu.Count > 0;
    }

    public static bool TryGetNiMiniPage(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        responseSubIds = [];
        if (dialogIndex != DialogIndex ||
            subId != NiMiniRootSubId ||
            !HasExactPath(arguments))
        {
            return false;
        }

        responseSubIds = NiMiniPageSubIds.ToArray();
        return true;
    }

    public static bool TryResolveDestination(
        string npcKey,
        uint interactionId,
        byte sourceMapId,
        byte camp,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out BattlefieldTransportDestination destination)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        destination = null!;
        if (dialogIndex != DialogIndex ||
            !TryResolveFaction(
                npcKey,
                interactionId,
                sourceMapId,
                camp,
                out var sparta))
        {
            return false;
        }

        destination = subId switch
        {
            PindusSubId when sparta && HasExactPath(arguments) =>
                Pindus(sourceMapId, sparta),
            AthensPindusSubId when !sparta && HasExactPath(arguments) =>
                Pindus(sourceMapId, sparta),
            NiMiniRootSubId when HasExactPath(
                arguments,
                NiMiniUpperSubId) =>
                NiMini(sourceMapId, sparta),
            DuelArenaSubId when HasExactPath(arguments) =>
                DuelArena(sourceMapId, sparta),
            LelantineFarmSubId or LelantineFarmSingleChannelSubId
                when HasExactPath(arguments) =>
                LelantineFarm(sourceMapId, sparta),
            _ => null!
        };
        return destination is not null;
    }

    /// <summary>
    /// The farm entry. The arrival is the fighter's own faction base on the
    /// farm, which is the pair the farm's own <c>Address.ini</c> names
    /// (<c>Athenian Base = -138,150</c>, <c>Spartan Base = 168,-153</c>) and the
    /// same values the published map 42 rows carry.
    /// </summary>
    private static BattlefieldTransportDestination LelantineFarm(
        int sourceMapId,
        bool sparta)
    {
        var (x, z) = LelantineFarmProtocol.Arrival(athenian: !sparta);
        return new(
            BattlefieldDestinationKind.LelantineFarm,
            "Lelantine Farm Defence",
            sourceMapId,
            TargetMapId: LelantineFarmProtocol.MapId,
            TargetX: x,
            TargetZ: z,
            MinimumLevel: LelantineFarmMinimumLevel,
            MaximumLevel: LelantineFarmMaximumLevel);
    }

    /// <summary>
    /// The root buttons the web editor can redirect. The single-channel twin
    /// follows sub id 282, so it is not listed on its own.
    /// </summary>
    public static IEnumerable<(string NpcKey, int SubId, BattlefieldTransportDestination Destination)> EditableButtons()
    {
        foreach (var (npcKey, sourceMapId, sparta, pindusSubId) in new[]
                 {
                     ("Sparta_056", 0, true, PindusSubId),
                     ("Athens_056", 1, false, AthensPindusSubId)
                 })
        {
            yield return (npcKey, pindusSubId, Pindus(sourceMapId, sparta));
            yield return (npcKey, NiMiniRootSubId, NiMini(sourceMapId, sparta));
            yield return (npcKey, DuelArenaSubId, DuelArena(sourceMapId, sparta));
            yield return (npcKey, LelantineFarmSubId, LelantineFarm(sourceMapId, sparta));
        }
    }

    /// <summary>The compiled destination with the editor's row applied, if it has one.</summary>
    public static BattlefieldTransportDestination ApplyEdit(
        BattlefieldTransportDestination destination,
        short? targetMapId,
        float x,
        float z,
        int minimumLevel) =>
        targetMapId is null
            ? destination
            : destination with
            {
                TargetMapId = targetMapId.Value,
                TargetX = x,
                TargetZ = z,
                MinimumLevel = minimumLevel
            };

    private static BattlefieldTransportDestination Pindus(
        int sourceMapId,
        bool sparta) =>
        new(
            BattlefieldDestinationKind.Pindus,
            "Pindus Mountains Battlefield",
            sourceMapId,
            TargetMapId: 38,
            TargetX: sparta ? 14f : 10f,
            TargetZ: sparta ? 180f : -183f,
            MinimumLevel: PindusCompiledMinimumLevel, // FINALISE2
            MaximumLevel: PindusCompiledMaximumLevel);

    private static BattlefieldTransportDestination NiMini(
        int sourceMapId,
        bool sparta) =>
        new(
            BattlefieldDestinationKind.NiMiniUpper,
            "Ni Mini Valley (Level 50-89)", // FINALISE2
            sourceMapId,
            TargetMapId: 34,
            TargetX: sparta ? 216f : 136f,
            TargetZ: sparta ? -220f : -4f,
            MinimumLevel: NiMiniMinimumLevel, // FINALISE2
            MaximumLevel: NiMiniMaximumLevel);

    private static BattlefieldTransportDestination DuelArena(
        int sourceMapId,
        bool sparta) =>
        new(
            BattlefieldDestinationKind.DuelArena,
            "Duel Arena",
            sourceMapId,
            TargetMapId: 57,
            TargetX: DuelArenaCapturedLayout.UpperArrivalX,
            TargetZ: DuelArenaCapturedLayout.UpperArrivalZ,
            MinimumLevel: 1,
            MaximumLevel: int.MaxValue);

    private static bool TryResolveFaction(
        string npcKey,
        uint interactionId,
        byte sourceMapId,
        byte camp,
        out bool sparta)
    {
        sparta = false;
        if ((npcKey, interactionId, sourceMapId, camp) is
            ("Sparta_056", SpartaNpcId, 0, 0))
        {
            sparta = true;
            return true;
        }

        return (npcKey, interactionId, sourceMapId, camp) is
            ("Athens_056", AthensNpcId, 1, 1) or
            ("Athens_056", PublishedAthensNpcId, 1, 1);
    }

    private static bool HasExactPath(
        IReadOnlyList<int> arguments,
        params int[] path)
    {
        if (arguments.Count != FunctionArgumentCount ||
            path.Length > arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var expected = index < path.Length ? path[index] : -1;
            if (arguments[index] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
