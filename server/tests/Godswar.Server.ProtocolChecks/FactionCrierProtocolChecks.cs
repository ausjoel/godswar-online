using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

internal static class FactionCrierProtocolChecks
{
    public const string CheckName =
        "Stock Faction Crier dialogue and mutation protocol";

    public static Task RunAsync()
    {
        Check.Equal(15, FactionCrierProtocol.DialogIndex,
            "Faction Crier uses NpcFunSignact dialog 15");
        Check.Equal(92, FactionCrierProtocol.ActionPacketBytes,
            "Faction Crier action packets use the exact stock frame length");
        Check.True(
            FactionCrierProtocol.InitialMenuSubIds.SequenceEqual(
                [1, 2, 3, 4, 1000]),
            "Faction Crier publishes four root choices and its explanation");
        Check.True(
            FactionCrierProtocol.IsEndpoint("Athens_055", FactionCrierProtocol.AthensNpcId) &&
            FactionCrierProtocol.IsEndpoint(
                "Athens_055", FactionCrierProtocol.PublishedAthensNpcId) &&
            FactionCrierProtocol.IsEndpoint("Sparta_055", 5052) &&
            FactionCrierProtocol.IsEndpoint("Sparta_055", 5054),
            "published and source Faction Crier endpoints are compatible");
        Check.True(
            !FactionCrierProtocol.IsEndpoint("Athens_055", 5052) &&
            !FactionCrierProtocol.IsEndpoint("Sparta_056", 5052),
            "unrelated identities cannot acquire Faction Crier behavior");

        CheckNavigationPages();
        CheckDailyAndWeeklyClaims();
        CheckRenewal();
        CheckTurnIns();
        CheckMalformedPathsFailClosed();
        return Task.CompletedTask;
    }

    private static void CheckNavigationPages()
    {
        (int SubId, int[] Path, int[] Page)[] cases =
        [
            (2, [], [10, 20, 1001]),
            (2, [10], [101, 102, 103, 104, 105, 106, 1002]),
            (2, [20], [107, 108, 109, 1003]),
            (2, [20, 107],
                [110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 1004]),
            (2, [20, 108],
                [120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 1005]),
            (2, [20, 109], [130, 131, 132, 133, 134, 1006]),
            (3, [], [31, 32, 33, 34, 35, 36, 1200]),
            (4, [], [41, 42, 43, 44, 45, 46, 1300])
        ];

        foreach (var (subId, path, expectedPage) in cases)
        {
            Check.True(
                FactionCrierProtocol.TryGetNavigationPage(
                    FactionCrierProtocol.DialogIndex,
                    subId,
                    Arguments(path),
                    out var actualPage) &&
                actualPage.SequenceEqual(expectedPage),
                $"Faction Crier nested path {subId}/{string.Join('/', path)}");
        }

        Check.True(
            !FactionCrierProtocol.TryGetNavigationPage(
                FactionCrierProtocol.DialogIndex + 1,
                2,
                Arguments(),
                out _) &&
            !FactionCrierProtocol.TryGetNavigationPage(
                FactionCrierProtocol.DialogIndex,
                1,
                Arguments(),
                out _) &&
            !FactionCrierProtocol.TryGetNavigationPage(
                FactionCrierProtocol.DialogIndex,
                2,
                Arguments(20, 110),
                out _),
            "mutations and cross-page choices are never served as navigation");
    }

    private static void CheckDailyAndWeeklyClaims()
    {
        Check.True(
            FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                1,
                Arguments(),
                out var daily) &&
            daily.Operation == FactionCrierWireOperation.DailyClaim &&
            daily.NameplateOrdinal == 0 &&
            daily.PaymentCurrency == FactionCrierWireCurrency.None,
            "daily claim carries no client-selected weekday or cost");

        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var action = 30 + ordinal;
            Check.True(
                FactionCrierProtocol.TryResolveMutation(
                    FactionCrierProtocol.DialogIndex,
                    3,
                    Arguments(action),
                    out var weekly) &&
                weekly.Operation ==
                    FactionCrierWireOperation.WeeklyReclaim &&
                weekly.ActionSubId == action &&
                weekly.NameplateOrdinal == ordinal &&
                weekly.PaymentCurrency ==
                    FactionCrierWireCurrency.Gold,
                $"weekly claim action {action} selects Nameplate {ordinal}");
        }
    }

    private static void CheckRenewal()
    {
        var arguments = Arguments(46);
        arguments[FactionCrierProtocol.FirstItemArgumentIndex] = 207;
        arguments[FactionCrierProtocol.FirstScratchArgumentIndex] = 19;
        Check.True(
            FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                4,
                arguments,
                out var renewal) &&
            renewal.Operation ==
                FactionCrierWireOperation.RenewNameplate &&
            renewal.NameplateOrdinal == 6 &&
            renewal.SourceKitBagSlot == 55 &&
            renewal.PaymentCurrency == FactionCrierWireCurrency.Gold,
            "renewal decodes ItemBtn bag-page coordinate at args[6]");

        var invalidCoordinate = Arguments(41);
        invalidCoordinate[FactionCrierProtocol.FirstItemArgumentIndex] = 224;
        var unexpectedArgument = Arguments(41);
        unexpectedArgument[FactionCrierProtocol.FirstItemArgumentIndex] = 0;
        unexpectedArgument[2] = 0;
        Check.True(
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                4,
                Arguments(41),
                out _) &&
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                4,
                invalidCoordinate,
                out _) &&
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                4,
                unexpectedArgument,
                out _),
            "renewal rejects a missing, out-of-page, or polluted selection");
    }

    private static void CheckTurnIns()
    {
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var action = 100 + ordinal;
            Check.True(
                FactionCrierProtocol.TryResolveMutation(
                    FactionCrierProtocol.DialogIndex,
                    2,
                    Arguments(10, action),
                    out var single) &&
                single.Operation ==
                    FactionCrierWireOperation.TurnInNameplates &&
                single.NameplateSet == FactionCrierWireNameplateSet.Single &&
                single.NameplateOrdinal == ordinal &&
                single.RewardKind ==
                    FactionCrierWireRewardKind.Experience &&
                single.RewardMultiplier == 1,
                $"single turn-in action {action} selects Nameplate {ordinal}");
        }

        (FactionCrierWireRewardKind Reward,
            FactionCrierWireCurrency Currency, int Multiplier)[]
            tripleOfferPattern =
        [
            (FactionCrierWireRewardKind.Experience,
                FactionCrierWireCurrency.Silver, 6),
            (FactionCrierWireRewardKind.TalentPoints,
                FactionCrierWireCurrency.Silver, 6),
            (FactionCrierWireRewardKind.Experience,
                FactionCrierWireCurrency.BindingGold, 9),
            (FactionCrierWireRewardKind.TalentPoints,
                FactionCrierWireCurrency.BindingGold, 9),
            (FactionCrierWireRewardKind.Experience,
                FactionCrierWireCurrency.Gold, 9),
            (FactionCrierWireRewardKind.TalentPoints,
                FactionCrierWireCurrency.Gold, 9),
            (FactionCrierWireRewardKind.Experience,
                FactionCrierWireCurrency.BindingGold, 12),
            (FactionCrierWireRewardKind.TalentPoints,
                FactionCrierWireCurrency.BindingGold, 12),
            (FactionCrierWireRewardKind.Experience,
                FactionCrierWireCurrency.Gold, 12),
            (FactionCrierWireRewardKind.TalentPoints,
                FactionCrierWireCurrency.Gold, 12)
        ];
        (int Parent, int FirstAction, FactionCrierWireNameplateSet Set)[]
            tripleSets =
        [
            (107, 110, FactionCrierWireNameplateSet.OddTriple),
            (108, 120, FactionCrierWireNameplateSet.EvenTriple)
        ];
        var offers = new List<(int Parent, int Action,
            FactionCrierWireNameplateSet Set,
            FactionCrierWireRewardKind Reward,
            FactionCrierWireCurrency Currency, int Multiplier)>();
        foreach (var tripleSet in tripleSets)
        {
            for (var index = 0; index < tripleOfferPattern.Length; index++)
            {
                var pattern = tripleOfferPattern[index];
                offers.Add((
                    tripleSet.Parent,
                    tripleSet.FirstAction + index,
                    tripleSet.Set,
                    pattern.Reward,
                    pattern.Currency,
                    pattern.Multiplier));
            }
        }
        offers.AddRange(
        [
            (109, 130, FactionCrierWireNameplateSet.AllSix,
                FactionCrierWireRewardKind.ExperienceAndTalentPoints,
                FactionCrierWireCurrency.Silver, 12),
            (109, 131, FactionCrierWireNameplateSet.AllSix,
                FactionCrierWireRewardKind.ExperienceAndTalentPoints,
                FactionCrierWireCurrency.BindingGold, 18),
            (109, 132, FactionCrierWireNameplateSet.AllSix,
                FactionCrierWireRewardKind.ExperienceAndTalentPoints,
                FactionCrierWireCurrency.Gold, 18),
            (109, 133, FactionCrierWireNameplateSet.AllSix,
                FactionCrierWireRewardKind.ExperienceAndTalentPoints,
                FactionCrierWireCurrency.BindingGold, 24),
            (109, 134, FactionCrierWireNameplateSet.AllSix,
                FactionCrierWireRewardKind.ExperienceAndTalentPoints,
                FactionCrierWireCurrency.Gold, 24)
        ]);
        Check.Equal(25, offers.Count,
            "all stock multi-nameplate offers are regression-covered");

        foreach (var offer in offers)
        {
            Check.True(
                FactionCrierProtocol.TryResolveMutation(
                    FactionCrierProtocol.DialogIndex,
                    2,
                    Arguments(20, offer.Parent, offer.Action),
                    out var intent) &&
                intent.ActionSubId == offer.Action &&
                intent.NameplateSet == offer.Set &&
                intent.RewardKind == offer.Reward &&
                intent.PaymentCurrency == offer.Currency &&
                intent.RewardMultiplier == offer.Multiplier,
                $"turn-in offer {offer.Action} retains its stock semantics");
        }
    }

    private static void CheckMalformedPathsFailClosed()
    {
        var shortFrame = Arguments()[..^1];
        var pollutedDaily = Arguments();
        pollutedDaily[0] = 0;
        Check.True(
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                1,
                pollutedDaily,
                out _) &&
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                2,
                Arguments(20, 107, 129),
                out _) &&
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                2,
                Arguments(20, 109, 119),
                out _) &&
            !FactionCrierProtocol.TryResolveMutation(
                FactionCrierProtocol.DialogIndex,
                3,
                shortFrame,
                out _),
            "wrong nesting, cross-set actions, and loose frames fail closed");
    }

    private static int[] Arguments(params int[] path)
    {
        var arguments = Enumerable.Repeat(
            -1,
            FactionCrierProtocol.FunctionArgumentCount).ToArray();
        path.CopyTo(arguments, 0);
        return arguments;
    }
}
