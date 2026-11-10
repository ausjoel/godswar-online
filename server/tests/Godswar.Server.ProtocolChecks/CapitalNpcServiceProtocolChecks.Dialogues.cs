using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CapitalNpcServiceProtocolChecks
{
    private static void CheckCapturedDialogRoutes()
    {
        var published = NpcContentBaselineV1.LoadDefinitions();
        var festival = published.Single(static npc =>
            npc.NpcKey == "Sparta_084");
        var halloween = published.Single(static npc =>
            npc.NpcKey == "Sparta_053");
        var levelSealer = published.Single(static npc =>
            npc.NpcKey == "Sparta_142");

        Check.True(
            CapitalNpcServiceProtocol.TryGetDialogueRoutes(
                festival,
                out var festivalRoutes) &&
            festivalRoutes.Count == 1 &&
            festivalRoutes[0].DialogIndex == 28 &&
            festivalRoutes[0].InitialMenuSubIds.SequenceEqual([2, 3, 4, 5]) &&
            CapitalNpcServiceProtocol.TryGetDialogueRoutes(
                halloween,
                out var halloweenRoutes) &&
            halloweenRoutes.Select(static route => route.DialogIndex)
                .SequenceEqual([95, 113]) &&
            CapitalNpcServiceProtocol.TryGetDialogueRoutes(
                levelSealer,
                out var levelRoutes) &&
            levelRoutes.Single().DialogIndex ==
                CapitalNpcServiceProtocol.LevelSealerDialogIndex &&
            levelRoutes.Single().InitialMenuSubIds.SequenceEqual(
                [101, 102, 103]),
            "event and local Level Sealer routes use their exact dialogs");

        Check.True(
            CapitalNpcServiceProtocol.TryGetInitialDialogueReply(
                CapitalNpcServiceKind.HalloweenEnvoy,
                out var halloweenDialog,
                out var halloweenRoot) &&
            halloweenDialog == 113 &&
            halloweenRoot.SequenceEqual([0, 1, 2, 3, 4, 5]) &&
            CapitalNpcServiceProtocol.TryGetInitialDialogueReply(
                CapitalNpcServiceKind.LevelSealer,
                out var levelDialog,
                out var levelRoot) &&
            levelDialog ==
                CapitalNpcServiceProtocol.LevelSealerDialogIndex &&
            levelRoot.SequenceEqual([101, 102, 103]),
            "initial replies reproduce the client function type and menu");

        CheckEventSubmenus();
    }

    private static void CheckEventSubmenus()
    {
        var emptyPath = Enumerable.Repeat(-1, 18).ToArray();
        var festivalPath = Enumerable.Repeat(-1, 18).ToArray();
        festivalPath[0] = 501;
        Check.True(
            CapitalNpcServiceProtocol.TryGetLevelSealerChange(
                CapitalNpcServiceKind.LevelSealer,
                CapitalNpcServiceProtocol.LevelSealerDialogIndex,
                CapitalNpcServiceProtocol.LevelSealerSealSubId,
                emptyPath,
                out var desiredSeal) &&
            desiredSeal &&
            CapitalNpcServiceProtocol.TryGetLevelSealerChange(
                CapitalNpcServiceKind.LevelSealer,
                CapitalNpcServiceProtocol.LevelSealerDialogIndex,
                CapitalNpcServiceProtocol.LevelSealerUnsealSubId,
                emptyPath,
                out desiredSeal) &&
            !desiredSeal &&
            !CapitalNpcServiceProtocol.TryGetLevelSealerChange(
                CapitalNpcServiceKind.LevelSealer,
                CapitalNpcServiceProtocol.LevelSealerDialogIndex,
                CapitalNpcServiceProtocol.LevelSealerSealedSubId,
                emptyPath,
                out _) &&
            FighterLevelSealRules.UnsealBoundGoldCost == 10_000,
            "local Level Sealer buttons map to free sealing and a 10,000 Bound Gold unseal");
        Check.True(
            CapitalNpcServiceProtocol.TryGetDialogueReply(
                CapitalNpcServiceKind.FestivalEnvoy,
                28,
                5,
                emptyPath,
                out var festivalDialog,
                out var festivalPage) &&
            festivalDialog == 28 &&
            festivalPage.SequenceEqual([501, 502, 4, 5]) &&
            CapitalNpcServiceProtocol.IsWeekendExperienceClaim(
                CapitalNpcServiceKind.FestivalEnvoy,
                28,
                5,
                festivalPath) &&
            CapitalNpcServiceProtocol.TryGetDialogueReply(
                CapitalNpcServiceKind.FestivalEnvoy,
                28,
                5,
                festivalPath,
                out _,
                out var festivalResult) &&
            festivalResult.SequenceEqual([511, 3, 4, 5]) &&
            CapitalNpcServiceProtocol.TryGetDialogueReply(
                CapitalNpcServiceKind.HalloweenEnvoy,
                113,
                4,
                emptyPath,
                out var halloweenPageDialog,
                out var halloweenPage) &&
            halloweenPageDialog == 113 &&
            halloweenPage.SequenceEqual([0, 5, 6, 7, 8, 8001]),
            "captured Festival and Halloween submenu transitions are exact");

        Check.True(
            WeekendExperienceClaimRules.BonusBasisPoints == 20_000 &&
            WeekendExperienceClaimRules.Duration == TimeSpan.FromHours(8) &&
            WeekendExperienceClaimRules.IsWeekend(
                new DateOnly(2026, 8, 29)) &&
            WeekendExperienceClaimRules.IsWeekend(
                new DateOnly(2026, 8, 30)) &&
            !WeekendExperienceClaimRules.IsWeekend(
                new DateOnly(2026, 8, 31)),
            "Weekend EXP grants +200 percent for eight online hours on realm weekends");
    }
}
