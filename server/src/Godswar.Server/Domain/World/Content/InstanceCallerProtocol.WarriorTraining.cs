using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// WARRIOR (the admin F-51): the Warriors' Training Ground (勇士试炼场), root 13.
/// </summary>
/// <remarks>
/// The client's NpcFunRepetition.lua (index 2) draws text 203 (NF_L0_R203, the
/// "twelve tasks ... Nemean Lion ... Lernaean Hydra ... once per day" line) and
/// the buttons 200 / 201 / 202 / 301. A button press comes back as root 13 with the
/// button's number in the first argument word, the pattern pages 230 / 231 use
/// (2026-09-28 capture), and opens the party's Enter window for that ground
/// (WarriorTrainingGroundPolicy).
/// </remarks>
internal static partial class InstanceCallerProtocol
{
    /// <summary>NF_L0_R203: the page's description line.</summary>
    public const int WarriorTrialDescriptionSubId = 203;

    /// <summary>Root 13's page: the description then the four ground buttons.</summary>
    public static IReadOnlyList<int> WarriorTrialPageSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            WarriorTrialDescriptionSubId,
            200,
            201,
            202,
            301
        });

    /// <summary>
    /// Whether an action is one of root 13's ground buttons, and which ground.
    /// The rest of the path must be empty (-1), as every page submission's is.
    /// </summary>
    public static bool TryResolveWarriorTrialEntry(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out WarriorTrainingGround ground)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ground = null!;
        return dialogIndex == DialogIndex &&
            subId == WarriorTrialRootSubId &&
            arguments.Count > 0 &&
            WarriorTrainingGroundPolicy.TryGetByPage(arguments[0], out ground) &&
            HasExactPath(arguments, arguments[0]);
    }

    private static InstanceCallerEntryDestination ResolveWarriorTrainingInvitation(int clientSceneId) =>
        WarriorTrainingGroundPolicy.TryGetByScene(clientSceneId, out var ground)
            ? ground.Destination
            : null!;
}
