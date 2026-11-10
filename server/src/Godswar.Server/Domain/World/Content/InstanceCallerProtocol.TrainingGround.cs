namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// TRAINING: the Training Ground (Trials of Heracles, 赫拉克里斯的试炼) entry.
/// </summary>
/// <remarks>
/// <para>
/// The Instance Caller's root <see cref="HeraclesTrialRootSubId"/> answers the
/// page <see cref="HeraclesTrialPageSubId"/>; clicking that page's own button
/// (first argument word 231) used to move one character onto the shared, empty
/// map 210. It now opens the same native Enter window every party instance uses
/// (10222/10216, confirmed with 10217) for one exact run of map 210.
/// </para>
/// <para>
/// Retail (IGG guide acid=166, "The Training Ground"): one entry a day, revive
/// near the entrance, back to the main city when the time is up, a termination
/// vote that pays by completion. the admin (DECISIONS-2026-11-03 round 2 item 6): one
/// ground on map 210 with level 75-90 monsters, 300 points, 30 minutes.
/// The level band of the players is this server's choice (see
/// <see cref="TrainingGroundMinimumLevel"/>); party 1-5 follows the wiki
/// ("solo or a group of up to 5"), the IGG guide gives no minimum.
/// </para>
/// </remarks>
internal static partial class InstanceCallerProtocol
{
    /// <summary>Map 210, <c>Heracles</c> (client MapIdToNameConfig: <c>Heracles,256</c>).</summary>
    public const byte TrainingGroundMapId = HeraclesTrialMapId;

    /// <summary>The destination's own client scene id, as 229/230 are for the harbor maps.</summary>
    public const int TrainingGroundClientSceneId = 256;

    /// <summary>
    /// Players from level 75 (the floor of the admin's 75-90 monster band; the IGG
    /// ground with level 80-90 monsters admits level 80-90) up to 130 (the top of
    /// the IGG guide's "ideal for players Level 60-130"). GENERATED choice.
    /// </summary>
    // FINALISE2 (the admin F-16): 60-200 (was 75-130, GENERATED). The page's own monsters stay Lv75-90.
    public const int TrainingGroundMinimumLevel = 60;

    public const int TrainingGroundMaximumLevel = 200; // FINALISE2

    public static InstanceCallerEntryDestination TrainingGroundDestination =>
        new(
            InstanceCallerEntryKind.TrainingGround,
            "TrainingGround",
            TargetMapId: TrainingGroundMapId,
            TargetX: HeraclesTrialArrivalX,
            TargetZ: HeraclesTrialArrivalZ,
            MinimumLevel: TrainingGroundMinimumLevel,
            MaximumLevel: TrainingGroundMaximumLevel,
            RequiredPartySize: null)
        {
            ClientSceneId = TrainingGroundClientSceneId,
            MinimumPartySize = 1
        };
}
