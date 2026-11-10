using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HarborAttackEntryProtocolChecks
{
    public const string CheckName =
        "港湾遇袭 entry route, level bands, roster floor, and daily allowance";

    private static readonly int[] Arguments = CreateArguments();

    public static Task RunAsync()
    {
        Check.True(
            InstanceCallerProtocol.TryGetPage(
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.HarborAttackRootSubId,
                Arguments,
                out var root,
                out var page) &&
            root == InstanceCallerProtocol.HarborAttackRootSubId &&
            page.SequenceEqual([InstanceCallerProtocol.HarborAttackPageSubId]) &&
            InstanceCallerProtocol.HarborAttackPageSubId == 230,
            "the captured harbor root opens exactly its own single page");

        Check.True(
            InstanceCallerProtocol.TryResolveHarborAttackEntry(
                InstanceCallerProtocol.DialogIndex,
                ArgumentsFor(InstanceCallerProtocol.HarborAttackPageSubId)) &&
            !InstanceCallerProtocol.TryResolveHarborAttackEntry(
                InstanceCallerProtocol.DialogIndex + 1,
                ArgumentsFor(InstanceCallerProtocol.HarborAttackPageSubId)) &&
            !InstanceCallerProtocol.TryResolveHarborAttackEntry(
                InstanceCallerProtocol.DialogIndex,
                ArgumentsFor(InstanceCallerProtocol.HeraclesTrialPageSubId)) &&
            !InstanceCallerProtocol.TryResolveHarborAttackEntry(
                InstanceCallerProtocol.DialogIndex,
                ArgumentsFor(InstanceCallerProtocol.WonderlandEnterSubId)),
            "only the harbor page at its own dialog index is the harbor entry");

        var firstBand = InstanceCallerProtocol.ResolveHarborAttackDestination(50);
        var topOfFirstBand =
            InstanceCallerProtocol.ResolveHarborAttackDestination(69);
        var secondBand = InstanceCallerProtocol.ResolveHarborAttackDestination(70);
        Check.True(
            firstBand.Kind == InstanceCallerEntryKind.HarborAttack &&
            firstBand.TargetMapId == 208 &&
            firstBand.ClientSceneId == 229 &&
            firstBand.MinimumLevel == 50 &&
            firstBand.MaximumLevel == 90 && // BAY2: level 50-90
            secondBand.MaximumLevel == 90 &&
            firstBand.MinimumPartySize == 3 &&
            firstBand.RequiredPartySize is null &&
            firstBand.PaymentMode == InstanceCallerEntryPaymentMode.FreeOnly &&
            firstBand.TargetX == 118f &&
            firstBand.TargetZ == 0f &&
            topOfFirstBand.TargetMapId == 208 &&
            secondBand.TargetMapId == 209 &&
            secondBand.ClientSceneId == 230 &&
            secondBand.MinimumLevel == 50 &&
            secondBand.MinimumPartySize == 3,
            "levels 50-69 resolve map 208/scene 229 and 70-90 resolves " +
            "map 209/scene 230, both with the level floor and a three-player " +
            "roster floor");

        Check.True(
            HarborAttackPolicy.TimeLimit == TimeSpan.FromMinutes(30) &&
            HarborAttackPolicy.TimeLimitSeconds == 1800,
            "the harbor run is the requested thirty-minute window");

        Check.True(
            LegacyInstanceDailyEntryPolicy.GetDefaultPaidRetryLimit(
                InstanceCallerEntryKind.HarborAttack) == 0 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultFreeEntryLimit(
                InstanceCallerEntryKind.HarborAttack) == 1 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(
                InstanceCallerEntryKind.HarborAttack) == 1 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultFreeEntryLimit(
                InstanceCallerEntryKind.Atlantis) == 3 &&
            LegacyInstanceDailyEntryPolicy.GetDefaultDailyEntryLimit(
                InstanceCallerEntryKind.Atlantis) == 4,
            "港湾遇袭 defaults to one free daily entry and no paid retry " +
            "while Atlantis keeps its reviewed three-free-plus-one-retry policy");

        // ROUND3: this feature owns a historical dependency chain, not the global migration head.
        string[] requiredMigrationIds =
        [
            "20260901_132_legacy_instance_daily_entry",
            "20260929_216_harbor_attack_daily_entry",
            "20260930_217_atlantis_partial_rewards",
            "20261001_218_atlantis_completion_roster_constraint",
            "20261002_219_guild_refuse_applications"
        ];
        Check.True(
            PostgresSchemaMigrationCatalog.All
                .Select(static migration => migration.Id)
                .Where(id => requiredMigrationIds.Contains(id, StringComparer.Ordinal))
                .SequenceEqual(requiredMigrationIds),
            "the harbor daily-entry and Atlantis reward migrations remain present in dependency order");

        return Task.CompletedTask;
    }

    private static int[] ArgumentsFor(int firstArgument)
    {
        var arguments = new int[InstanceCallerProtocol.FunctionArgumentCount];
        Array.Fill(arguments, -1);
        arguments[0] = firstArgument;
        return arguments;
    }

    private static int[] CreateArguments() => ArgumentsFor(-1);
}
