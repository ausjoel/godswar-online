using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// INSTANCES: hot switches (public.server_rates) that close instances which are
/// not finished yet, so a player never spends a daily entry on an empty map.
/// Both default to closed; 1 restores the previous behaviour exactly.
/// </summary>
internal static class InstanceEntrySwitches
{
    public static bool IsHarborAttackOpen =>
        ServerRates.Current.Absolute(ServerRateKeys.HarborAttackEntry) == 1;

    public static bool IsTrainingGroundOpen =>
        ServerRates.Current.Absolute(ServerRateKeys.TrainingGroundEntry) == 1;

    /// <summary>WARRIOR (F-51): the Warriors' Training Ground (root 13).</summary>
    public static bool IsWarriorTrainingOpen =>
        ServerRates.Current.Absolute(ServerRateKeys.WarriorTrainingEntry) == 1;

    /// <summary>WARRIOR (F-51): whether the switch of a Training Ground entry kind is on.</summary>
    public static bool IsOpen(Domain.World.Content.InstanceCallerEntryKind kind) =>
        kind == Domain.World.Content.InstanceCallerEntryKind.TrainingGround ? IsTrainingGroundOpen
        : WarriorTrainingGroundPolicy.IsWarriorKind(kind) ? IsWarriorTrainingOpen
        : true;

    /// <summary>Plain-English note sent with the refusal (personal log line).
    /// Each fits one 63-byte note field, so the client never has to join two halves.</summary>
    public const string HarborAttackClosedNote =
        "Bay Under Attack is closed for now. No daily entry was used.";

    public const string TrainingGroundClosedNote =
        "The Training Ground is closed for now. No daily entry was used.";

    public const string WarriorTrainingClosedNote =
        "Warriors' Training Ground is closed. No daily entry was used.";
}
