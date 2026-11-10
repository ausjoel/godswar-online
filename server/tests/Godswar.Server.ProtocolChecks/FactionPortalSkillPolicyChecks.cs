using Godswar.Server.Domain.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class FactionPortalSkillPolicyChecks
{
    public const string CheckName = "Faction portal skill policy";

    public static Task RunAsync()
    {
        Check.Equal(
            3060u,
            FactionPortalSkillPolicy.AthensCapitalPortalSkillId,
            "Athens capital portal keeps its native skill identity");
        Check.Equal(
            3061u,
            FactionPortalSkillPolicy.AthensSuburbPortalSkillId,
            "Athens suburb portal keeps its native skill identity");
        Check.Equal(
            3062u,
            FactionPortalSkillPolicy.SpartaCapitalPortalSkillId,
            "Sparta capital portal keeps its native skill identity");
        Check.Equal(
            3063u,
            FactionPortalSkillPolicy.SpartaSuburbPortalSkillId,
            "Sparta suburb portal keeps its native skill identity");

        Check.Equal(
            FactionPortalSkillPolicy.AthensCapitalPortalSkillId,
            FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(
                GameDefaults.AthensCamp),
            "new Athens characters resolve the Athens capital portal");
        Check.Equal(
            FactionPortalSkillPolicy.SpartaCapitalPortalSkillId,
            FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(
                GameDefaults.SpartaCamp),
            "new Sparta characters resolve the Sparta capital portal");
        Check.Throws<ArgumentOutOfRangeException>(
            () => FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(2),
            "unknown camps cannot receive an arbitrary capital portal");
        return Task.CompletedTask;
    }
}
