using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterDeathRewardCommitBoundaryChecks
{
    public static async Task RunAsync()
    {
        await CheckCommitPrecedesCancelledDeliveryAsync();
        CheckAllCombatPathsPrepareBeforeDelivery();
        CheckAreaPreparationAdvancesProjection();
        CheckAtlantisSharesSuccessfulSettlementBoundary();
        CheckReceiptObserverPrecedesClaimantProjection();
    }

    private static async Task
        CheckCommitPrecedesCancelledDeliveryAsync()
    {
        var trace = new List<string>();
        var attempts = 0;
        var result = await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            cancellationToken =>
            {
                Check.True(
                    !cancellationToken.CanBeCanceled,
                    "monster reward commit ignores session cancellation");
                attempts++;
                trace.Add($"commit-{attempts}");
                return attempts == 1
                    ? Task.FromException<int>(
                        new IOException("unknown commit outcome"))
                    : Task.FromResult(42);
            },
            allowImmediateReplay: true);

        using var cancelledDelivery = new CancellationTokenSource();
        cancelledDelivery.Cancel();
        try
        {
            trace.Add("delivery");
            cancelledDelivery.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
        }

        Check.True(
            result == 42 &&
            attempts == 2 &&
            trace.SequenceEqual(
                ["commit-1", "commit-2", "delivery"]),
            "durable replay completes before a cancelled post-hit delivery");
    }

    private static void CheckAllCombatPathsPrepareBeforeDelivery()
    {
        var root = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     "src/Godswar.Server/Game/GameClientHandler.MovementCombat.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsBasic.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatSkill.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsSkill.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatArea.cs",
                     "src/Godswar.Server/Game/GameClientHandler.CombatEcsArea.cs"
                 })
        {
            var source = File.ReadAllText(
                Path.Combine(
                    root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prepare = source.IndexOf(
                "PrepareClaimedMonsterKillRewardAsync",
                StringComparison.Ordinal);
            var publish = source.IndexOf(
                "await pendingReward.PublishAsync(",
                StringComparison.Ordinal);
            Check.True(
                prepare >= 0 &&
                publish > prepare &&
                !source.Contains(
                    "AwardMonsterKillAsync",
                    StringComparison.Ordinal),
                $"{relativePath} uses prepare-before-publish reward ordering");

            var successfulMutationBoundary = source.LastIndexOf(
                "CommitPveLifeAbsorption(",
                prepare,
                StringComparison.Ordinal);

            Check.True(
                successfulMutationBoundary >= 0,
                $"{relativePath} exposes its successful mutation boundary");
            var beforePrepare =
                source[successfulMutationBoundary..prepare];
            Check.True(
                beforePrepare.Split(
                    "await ",
                    StringSplitOptions.None).Length == 2,
                $"{relativePath} has no cancellable await before reward preparation");
        }
    }

    private static void CheckAreaPreparationAdvancesProjection()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.Progression.cs"));
        var apply = source.IndexOf(
            "ApplyMonsterRewardProjection(settlement);",
            StringComparison.Ordinal);
        var pending = source.IndexOf(
            "return new PendingMonsterKillReward(",
            StringComparison.Ordinal);
        Check.True(
            apply >= 0 && pending > apply,
            "each prepared AOE reward advances the level used by the next kill");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }

    private static void CheckAtlantisSharesSuccessfulSettlementBoundary()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Godswar.Server", "Game", "GameClientHandler.DurableMonsterRewards.cs"));
        var callback = source.IndexOf("onSettled: settlement =>", StringComparison.Ordinal);
        var guard = source.IndexOf("if (settlement is not null)", StringComparison.Ordinal);
        var score = source.IndexOf("recordAtlantisKill?.Invoke();", StringComparison.Ordinal);
        var capture = source.IndexOf("_registry.CaptureAtlantisMonsterKill(", StringComparison.Ordinal);
        var commit = source.IndexOf("MonsterDeathRewardCommitBoundary.ExecuteAsync(", StringComparison.Ordinal);
        Check.True(callback >= 0 && guard > callback && score > guard &&
            capture >= 0 && commit > capture &&
            !source[callback..score].Contains("IsFirstCommit", StringComparison.Ordinal),
            "Atlantis scores successful and replayed settlements through the shared combat reward boundary");
    }

    private static void CheckReceiptObserverPrecedesClaimantProjection()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Godswar.Server", "Game", "GameClientHandler.DurableMonsterRewards.cs"));
        var observer = source.IndexOf("ExecuteWithCommitObserverAsync(", StringComparison.Ordinal);
        var claimant = source.IndexOf("RevalidateCurrentPlayerOwnership(ownership)", StringComparison.Ordinal);
        var pet = source.IndexOf("return await AttachPetMonsterExperienceAsync(", StringComparison.Ordinal);
        Check.True(observer >= 0 && claimant > observer && pet > claimant,
            "team commit acknowledgement precedes claimant ownership revalidation and pet extras");
    }
}
