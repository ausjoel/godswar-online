namespace Godswar.Server.Domain.World.Content;

internal enum InstanceCallerDifficulty
{
    Advanced = 204,
    Normal = 205,
    Mythic = 207
}

internal enum InstanceCallerEntryKind : byte
{
    Atlantis = 1,
    Wonderland = 2,
    HarborAttack = 3,
    // TRAINING: Training Ground (Trials of Heracles, map 210). 10, not 4, so the
    // daily-entry kind cannot collide with another builder's next instance.
    TrainingGround = 10,
    // WARRIOR (F-51): the four Warriors' Training Grounds (root 13), one daily
    // entry each (wiki). 21-24, clear of 10 and of 11+ that other instance work may take.
    WarriorTrainingGround1 = 21,
    WarriorTrainingGround2 = 22,
    WarriorTrainingGround3 = 23,
    WarriorTrainingGround4 = 24
}

internal enum InstanceCallerEntryPaymentMode : byte
{
    FreeOnly = 1,
    OpalRetry = 2
}

internal sealed record InstanceCallerEntryDestination(
    InstanceCallerEntryKind Kind,
    string DisplayName,
    byte TargetMapId,
    float TargetX,
    float TargetZ,
    int MinimumLevel,
    int MaximumLevel,
    int? RequiredPartySize)
{
    public InstanceCallerEntryPaymentMode PaymentMode { get; init; } =
        InstanceCallerEntryPaymentMode.FreeOnly;

    /// <summary>
    /// The client's repetition/scene selector for the entry window
    /// (opcodes 10216/10222 and the 10217 reply). It is the destination map's
    /// own client scene id, never the content map id.
    /// </summary>
    public int ClientSceneId { get; init; }

    /// <summary>
    /// Smallest admitted roster when <see cref="RequiredPartySize"/> is null.
    /// </summary>
    public int MinimumPartySize { get; init; } = 1;
}

/// <summary>
/// Finite stock NpcFunRepetition surface for Medusa Island. The stock client
/// keeps the root choice in the request sub-id and appends the difficulty to
/// the fixed 18-value argument path.
/// </summary>
internal static partial class InstanceCallerProtocol
{
    // The client is handed the capture's own id: CapturedNpcPlacementPolicy
    // renumbers Athens' city npcs from the published catalog to the capture, and
    // the client echoes back whatever it was handed. The published dialogue
    // baseline still carries the catalog's value, and the npc content tables are
    // append-only (a changed row needs a new revision), so both ids are accepted
    // until the content is republished.
    public const uint AthensNpcId = 5198;
    public const uint PublishedAthensNpcId = 5199;
    public const uint SpartaNpcId = 5057;
    public const int DialogIndex = 9;
    public const int WonderlandResultDialogIndex = 3;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const float MaximumInteractionDistance = 12f;
    public const int InitialRequestSubId = -1;
    public const int MedusaRootSubId = 11;
    public const int AtlantisRootSubId = 14;
    public const int WonderlandRootSubId = 15;

    /// <summary>The 勇士试炼场 root, captured on the opening page.</summary>
    /// <remarks>
    /// The September 28 2026 capture (session c202c633, npc 5057 = Sparta_060 at
    /// local 02:02:38) advertised <c>[11, 13, 14, 15, 16, 17]</c>. <c>13</c> is
    /// 勇士试炼场 in the client's own <c>NpcFunRepetition.lua</c> page one; the
    /// player never opened it. WARRIOR (F-51): it now answers the client's own
    /// page (203 + 200/201/202/301), see InstanceCallerProtocol.WarriorTraining.cs.
    /// </remarks>
    public const int WarriorTrialRootSubId = 13;

    /// <summary>The 港湾遇袭 root, captured answering <c>230</c>.</summary>
    public const int HarborAttackRootSubId = 16;

    /// <summary>The 赫拉克里斯的试炼 root, captured answering <c>231</c>.</summary>
    public const int HeraclesTrialRootSubId = 17;

    /// <summary>Page the 港湾遇袭 root answered with.</summary>
    public const int HarborAttackPageSubId = 230;

    /// <summary>Page the 赫拉克里斯的试炼 root answered with.</summary>
    public const int HeraclesTrialPageSubId = 231;

    /// <summary>
    /// The 赫拉克里斯的试炼 arrival: map 210 at (100, -100).
    /// </summary>
    /// <remarks>
    /// Clicking that page's own button admits the character to the trial, so the
    /// server moves them there. The arrival point is this server's own choice
    /// (2026-09-28) rather than a captured one: the capture only holds the
    /// level-gate line the reference drew for a character who had not joined, so
    /// there is no reference arrival coordinate to copy.
    /// </remarks>
    public const byte HeraclesTrialMapId = 210;

    // The widest open point of Heracles.hmp's block plane; the earlier (100, -100) was inside a wall.
    public const float HeraclesTrialArrivalX = 128f;
    public const float HeraclesTrialArrivalZ = -133f;

    /// <summary>
    /// Whether an action is the 赫拉克里斯的试炼 page's own entry, and where it
    /// sends the character.
    /// </summary>
    /// <remarks>
    /// The client keeps the page's number in the first argument word, which is
    /// the same word <see cref="TryGetLevelGateResult"/> reads, so this has to be
    /// consulted first: the page that used to be answered with the reference's
    /// "you may not enter" line now admits the character instead.
    /// </remarks>
    public static bool TryResolveHeraclesTrialEntry(
        int dialogIndex,
        IReadOnlyList<int> arguments,
        out byte targetMapId,
        out float targetX,
        out float targetZ)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        targetMapId = HeraclesTrialMapId;
        targetX = HeraclesTrialArrivalX;
        targetZ = HeraclesTrialArrivalZ;
        return dialogIndex == DialogIndex &&
            arguments.Count > 0 &&
            arguments[0] == HeraclesTrialPageSubId;
    }
    /// <summary>
    /// 港湾遇袭 (Bay Under Attack). The one captured root sub-id
    /// <see cref="HarborAttackRootSubId"/> answers with the single page
    /// <see cref="HarborAttackPageSubId"/>, whose own client label is
    /// <c>港湾遇袭(50级以上玩家可进入)</c> — level 50 and above.
    /// </summary>
    /// <remarks>
    /// Both content maps are the same dungeon at two level bands, so the map is
    /// chosen from the admitted leader's level rather than from the page: the
    /// client only sends the page number. <c>Salame</c> (208) is
    /// 被攻击的海湾1, client scene 229; <c>Salame2</c> (209) is 被攻击的海湾,
    /// client scene 230. Both scene ids come from
    /// <c>MapTemplateSeed.Generated.cs</c>, which is also why they are the
    /// values the entry window must carry.
    /// </remarks>
    public const byte HarborAttackFirstMapId = 208;

    public const byte HarborAttackSecondMapId = 209;

    public const int HarborAttackFirstClientSceneId = 229;

    public const int HarborAttackSecondClientSceneId = 230;

    /// <summary>Client scene selector of the Atlantis destination (map 205).</summary>
    public const int AtlantisClientSceneId = 224;

    /// <summary>Client scene selector of the Wonderland destination (map 207).</summary>
    public const int WonderlandClientSceneId = 227;

    /// <summary>Every admitted member must be at least this level.</summary>
    public const int HarborAttackMinimumLevel = 50;

    /// <summary>BAY2 (the admin 2026-11-03, round 2): joinable by level 50-90 (wiki 61-90; IGG gives no cap).</summary>
    public const int HarborAttackMaximumLevel = 90;

    /// <summary>The leader's level at which the run moves to map 209.</summary>
    public const int HarborAttackSecondBandLevel = 70;

    /// <summary>At least three players, never more than the party cap of five.</summary>
    public const int HarborAttackMinimumPartySize = 3;

    /// <summary>
    /// The reviewed arrival on both harbor maps.
    /// </summary>
    /// <remarks>
    /// <c>Map/Salame.hmp</c> and <c>Map/Salame2.hmp</c> are byte-identical
    /// (<c>7E8F66DBD6380EA4A8F35A4F291DBB106AEF81DA3D189B726FCBD848A95429C3</c>),
    /// and this cell carries 21.75 units of square blocked-cell clearance, the
    /// largest on the map. The client's own <c>Salame/Address.ini</c> publishes
    /// no arrival anchor (<c>AddressCount=0</c> for both camps), so this is an
    /// authored point and not a recovered original-server coordinate.
    /// </remarks>
    public const float HarborAttackArrivalX = 118f;

    public const float HarborAttackArrivalZ = 0f;

    /// <summary>
    /// Whether an action is the 港湾遇袭 page's own entry.
    /// </summary>
    /// <remarks>
    /// Checked before <see cref="TryGetLevelGateResult"/>, which reads the same
    /// argument word: the page keeps answering the reference's level line for a
    /// character below <see cref="HarborAttackMinimumLevel"/>, and this resolves
    /// the destination only for an eligible one.
    /// </remarks>
    public static bool TryResolveHarborAttackEntry(
        int dialogIndex,
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return dialogIndex == DialogIndex &&
            arguments.Count > 0 &&
            arguments[0] == HarborAttackPageSubId;
    }

    /// <summary>
    /// The harbor destination for an admitted leader's level. Both bands share
    /// the arrival, the level floor and the roster floor; only the content map
    /// and its client scene differ.
    /// </summary>
    public static InstanceCallerEntryDestination ResolveHarborAttackDestination(
        int leaderLevel) =>
        leaderLevel >= HarborAttackSecondBandLevel
            ? HarborAttackDestination(
                HarborAttackSecondMapId,
                HarborAttackSecondClientSceneId,
                "HarborAttack(70+)")
            : HarborAttackDestination(
                HarborAttackFirstMapId,
                HarborAttackFirstClientSceneId,
                "HarborAttack(50-69)");

    private static InstanceCallerEntryDestination HarborAttackDestination(
        byte mapId,
        int clientSceneId,
        string displayName) =>
        new(
            InstanceCallerEntryKind.HarborAttack,
            displayName,
            TargetMapId: mapId,
            TargetX: HarborAttackArrivalX,
            TargetZ: HarborAttackArrivalZ,
            MinimumLevel: HarborAttackMinimumLevel,
            MaximumLevel: HarborAttackMaximumLevel, // BAY2
            RequiredPartySize: null)
        {
            ClientSceneId = clientSceneId,
            MinimumPartySize = HarborAttackMinimumPartySize
        };

    public const int DescriptionSubId = 206;

    public const int AdvancedDifficultySubId =
        (int)InstanceCallerDifficulty.Advanced;
    public const int NormalDifficultySubId =
        (int)InstanceCallerDifficulty.Normal;
    public const int MythicDifficultySubId =
        (int)InstanceCallerDifficulty.Mythic;
    public const int QueueUnavailableResultSubId = 1000;
    public const int AtlantisDescriptionSubId = 208;
    public const int AtlantisOpalSubId = 209;
    public const int AtlantisEnterSubId = 210;
    public const int WonderlandEnterSubId = 211;
    public const int AtlantisMaximumEntriesResultSubId = 1500;
    public const int AtlantisInvalidOpalResultSubId = 1501;
    public const int AtlantisInsufficientOpalResultSubId = 1502;
    public const int AtlantisEntryPolicyResultSubId = 1600;
    public const int AtlantisOpalConsentRecordedResultSubId = 1601;
    public const int AtlantisOpalConsentMissingResultSubId = 1602;
    public const int AtlantisLevelResultSubId = 1510;
    public const int AtlantisLeaderResultSubId = 1511;
    public const int AtlantisPartyTooSmallResultSubId = 1521;
    public const int AtlantisPartyTooLargeResultSubId = 1800;
    public const int WonderlandWeekendResultSubId = 300;
    public const int WonderlandCutoffResultSubId = 301;

    public static readonly TimeSpan PageContextLifetime =
        TimeSpan.FromMinutes(2);

    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            MedusaRootSubId,
            WarriorTrialRootSubId,
            AtlantisRootSubId,
            WonderlandRootSubId,
            HarborAttackRootSubId,
            HeraclesTrialRootSubId
        });

    public static IReadOnlyList<int> MedusaPageSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            DescriptionSubId,
            AdvancedDifficultySubId,
            NormalDifficultySubId,
            MythicDifficultySubId
        });

    public static IReadOnlyList<int> AtlantisPageSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            AtlantisDescriptionSubId,
            AtlantisOpalSubId,
            AtlantisEnterSubId
        });

    public static IReadOnlyList<int> WonderlandPageSubIds { get; } =
        Array.AsReadOnly(new[] { WonderlandEnterSubId });

    /// <summary>The 港湾遇袭 page the capture answered <c>16</c> with.</summary>
    public static IReadOnlyList<int> HarborAttackPageSubIds { get; } =
        Array.AsReadOnly(new[] { HarborAttackPageSubId });

    /// <summary>The 赫拉克里斯的试炼 page the capture answered <c>17</c> with.</summary>
    public static IReadOnlyList<int> HeraclesTrialPageSubIds { get; } =
        Array.AsReadOnly(new[] { HeraclesTrialPageSubId });

    /// <summary>
    /// The result the reference answered a 港湾遇袭 or 赫拉克里斯的试炼 page
    /// submission with.
    /// </summary>
    /// <remarks>
    /// Captured on 2026-09-28: the client keeps the level-one number in
    /// <c>+16</c> and puts the clicked page number in the first argument word, so
    /// the reference saw <c>(17, 231)</c> at local 02:02:44 and <c>(16, 230)</c>
    /// at 02:02:50 and answered both with <c>1510</c> - the same
    /// level-requirement line it sends Atlantis candidates who are too low. This
    /// server runs neither instance, so it answers exactly that: the reference's
    /// own line, for the same two submissions, and nothing else.
    /// </remarks>
    public static bool TryGetLevelGateResult(
        int dialogIndex,
        IReadOnlyList<int> arguments,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        responseSubIds = [];
        if (dialogIndex != DialogIndex ||
            arguments.Count == 0 ||
            arguments[0] is not (HarborAttackPageSubId or
                HeraclesTrialPageSubId))
        {
            return false;
        }

        responseSubIds = [AtlantisLevelResultSubId];
        return true;
    }

    /// <summary>
    /// INSTANCES: the answer to a page of a closed instance. Both the index-2 and
    /// index-3 branches of the client's <c>NpcFunRepetition.lua</c> render any
    /// SubID of 10000 or more as <c>NF_L0_R117 .. SubID % 10000</c>, which the
    /// English client prints as "The remaining instances for this activity
    /// today is 0" - the client's own line, and true while the instance is shut.
    /// </summary>
    public const int ClosedInstanceResultSubId = 10000;

    /// <summary>Which closed instance an Instance Caller action belongs to.</summary>
    internal enum ClosedInstance : byte
    {
        None = 0,
        HarborAttack = 1,
        TrainingGround = 2,
        WarriorTraining = 3 // WARRIOR (F-51): root 13 has its own switch
    }

    /// <summary>
    /// INSTANCES: whether an action opens or submits a page of an instance whose
    /// entry switch is off. Covers the root buttons (16 Bay Under Attack, 13
    /// Training Ground, 17 Trials of Hercules) and the page submissions
    /// (230, 231), so nothing past this point - no page context, no countdown,
    /// no daily-entry claim, no teleport - runs for a closed instance.
    /// </summary>
    public static ClosedInstance ResolveClosedInstance(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        bool harborAttackOpen,
        bool trainingGroundOpen,
        bool warriorTrainingOpen = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (dialogIndex != DialogIndex)
        {
            return ClosedInstance.None;
        }

        var firstArgument = arguments.Count > 0 ? arguments[0] : -1;
        if (!harborAttackOpen &&
            (subId == HarborAttackRootSubId ||
             firstArgument == HarborAttackPageSubId))
        {
            return ClosedInstance.HarborAttack;
        }

        // WARRIOR (F-51): root 13 and its four page buttons follow their own switch.
        if (!warriorTrainingOpen && subId == WarriorTrialRootSubId)
        {
            return ClosedInstance.WarriorTraining;
        }

        if (!trainingGroundOpen &&
            (subId == HeraclesTrialRootSubId ||
             firstArgument == HeraclesTrialPageSubId))
        {
            return ClosedInstance.TrainingGround;
        }

        return ClosedInstance.None;
    }

    public static bool IsEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_060", AthensNpcId) or
            ("Athens_060", PublishedAthensNpcId) or
            ("Sparta_060", SpartaNpcId);

    public static bool TryGetMedusaPage(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        responseSubIds = [];
        if (dialogIndex != DialogIndex ||
            subId != MedusaRootSubId ||
            !HasExactPath(arguments))
        {
            return false;
        }

        responseSubIds = MedusaPageSubIds.ToArray();
        return true;
    }

    public static bool TryGetPage(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int rootSubId,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        rootSubId = 0;
        responseSubIds = [];
        if (dialogIndex != DialogIndex || !HasExactPath(arguments))
        {
            return false;
        }

        (rootSubId, IReadOnlyList<int>? page) = subId switch
        {
            MedusaRootSubId => (MedusaRootSubId, MedusaPageSubIds),
            AtlantisRootSubId => (AtlantisRootSubId, AtlantisPageSubIds),
            WonderlandRootSubId =>
                (WonderlandRootSubId, WonderlandPageSubIds),
            // Both captured on 2026-09-28: the reference answered 16 with 230 and
            // 17 with 231. Neither instance is run here, so the page is the
            // script's own "this instance's entry" line and nothing more.
            HarborAttackRootSubId =>
                (HarborAttackRootSubId, HarborAttackPageSubIds),
            HeraclesTrialRootSubId =>
                (HeraclesTrialRootSubId, HeraclesTrialPageSubIds),
            // WARRIOR (F-51): the client's own page one for 勇士试炼场.
            WarriorTrialRootSubId =>
                (WarriorTrialRootSubId, WarriorTrialPageSubIds),
            _ => default
        };
        if (page is null)
        {
            return false;
        }

        responseSubIds = page.ToArray();
        return true;
    }

    public static bool TryResolveEntry(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out InstanceCallerEntryDestination destination)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        destination = null!;
        if (dialogIndex != DialogIndex)
        {
            return false;
        }

        destination = subId switch
        {
            AtlantisRootSubId when HasExactPath(
                arguments,
                AtlantisOpalSubId) =>
                AtlantisDestination with
                {
                    PaymentMode =
                        InstanceCallerEntryPaymentMode.OpalRetry
                },
            AtlantisRootSubId when HasExactPath(
                arguments,
                AtlantisEnterSubId) =>
                AtlantisDestination,
            WonderlandRootSubId when HasExactPath(
                arguments,
                WonderlandEnterSubId) =>
                WonderlandDestination,
            _ => null!
        };
        return destination is not null;
    }

    /// <summary>
    /// The Wonderland arrival: island 1's entrance at <c>(169, -216)</c>.
    /// </summary>
    /// <remarks>
    /// This is the point the reviewed Wonderland terrain carries as island 1's
    /// <c>Entrance</c> (<c>WonderlandTerrainPolicy.GetIsland(1)</c>), which the
    /// same file documents as the instance caller's own landing: free revival and
    /// recovery use it, and <c>IsCombatArea</c> centres island 1's ten-unit safe
    /// zone on it. It replaces an earlier <c>(120, 209)</c> that pointed at the
    /// island's far edge instead of its entrance, so a party landed away from the
    /// safe zone it is supposed to arrive in.
    /// </remarks>
    /// <summary>
    /// Whether this instance hands the leader's own Enter window to the rest of
    /// the admitted party, so that each member enters by confirming it and a
    /// member who never confirms spends no daily attempt.
    /// </summary>
    /// <remarks>
    /// 港湾遇袭 introduced the flow and the party instances now share it: the
    /// leader's request publishes the same native Enter window to every admitted
    /// member, each member enters by confirming it, and a member who never
    /// confirms spends no daily attempt. Heracles' trial is a solo instance and
    /// keeps the direct move.
    /// </remarks>
    public static bool UsesPerMemberEntryWindow(InstanceCallerEntryKind kind) =>
        kind is InstanceCallerEntryKind.HarborAttack or
            InstanceCallerEntryKind.Atlantis or
            InstanceCallerEntryKind.Wonderland or
            InstanceCallerEntryKind.TrainingGround or // TRAINING
            InstanceCallerEntryKind.WarriorTrainingGround1 or // WARRIOR (F-51)
            InstanceCallerEntryKind.WarriorTrainingGround2 or
            InstanceCallerEntryKind.WarriorTrainingGround3 or
            InstanceCallerEntryKind.WarriorTrainingGround4;

    /// <summary>
    /// Resolves the instance a client-named invitation refers to from the client
    /// scene id its frame carries: 224 Atlantis, 227 Wonderland, 229/230 the two
    /// 港湾遇袭 level bands.
    /// </summary>
    public static bool TryResolveInvitedInstance(
        int clientSceneId,
        out InstanceCallerEntryDestination destination)
    {
        destination = clientSceneId switch
        {
            AtlantisClientSceneId => AtlantisDestination,
            WonderlandClientSceneId => WonderlandDestination,
            HarborAttackFirstClientSceneId =>
                ResolveHarborAttackDestination(HarborAttackMinimumLevel),
            HarborAttackSecondClientSceneId =>
                ResolveHarborAttackDestination(HarborAttackSecondBandLevel),
            TrainingGroundClientSceneId => TrainingGroundDestination, // TRAINING
            _ => ResolveWarriorTrainingInvitation(clientSceneId) // WARRIOR (F-51)
        };
        return destination is not null;
    }

    private static InstanceCallerEntryDestination WonderlandDestination =>
        new(
            InstanceCallerEntryKind.Wonderland,
            "Wonderland",
            TargetMapId: 207,
            TargetX: 169f,
            TargetZ: -216f,
            MinimumLevel: 120,
            MaximumLevel: 200, // FINALISE2: level 200 is the cap
            RequiredPartySize: null)
        {
            ClientSceneId = WonderlandClientSceneId
        };

    private static InstanceCallerEntryDestination AtlantisDestination =>
        new(
            InstanceCallerEntryKind.Atlantis,
            "Atlantis",
            TargetMapId: 205,
            TargetX: 171f,
            TargetZ: 24f,
            MinimumLevel: 90,
            MaximumLevel: 200, // FINALISE2 (the admin F-21): 90-200, level 200 is the cap
            RequiredPartySize: null)
        {
            ClientSceneId = AtlantisClientSceneId
        };

    public static bool TryResolveDifficulty(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out InstanceCallerDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        difficulty = default;
        if (dialogIndex != DialogIndex ||
            subId != MedusaRootSubId ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        difficulty = arguments[0] switch
        {
            AdvancedDifficultySubId => InstanceCallerDifficulty.Advanced,
            NormalDifficultySubId => InstanceCallerDifficulty.Normal,
            MythicDifficultySubId => InstanceCallerDifficulty.Mythic,
            _ => default
        };
        return difficulty != default &&
            HasExactPath(arguments, (int)difficulty);
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
