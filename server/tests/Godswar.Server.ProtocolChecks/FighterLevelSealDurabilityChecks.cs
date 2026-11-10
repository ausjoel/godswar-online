using Godswar.Server.Application.Progression;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FighterLevelSealDurabilityChecks
{
    public const string CheckName =
        "Durable Level Sealer ownership, economy, and replay";

    public static async Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var contracts = await ReadSourceAsync(
            root,
            "src",
            "Godswar.Server",
            "Application",
            "Progression",
            "FighterLevelSealContracts.cs");
        var store = await ReadSourceAsync(
            root,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Progression",
            "PostgresFighterLevelSealStore.FighterLevelSeal.cs");
        var evidence = await ReadSourceAsync(
            root,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Progression",
            "PostgresFighterLevelSealStore.Evidence.cs");
        var handler = await ReadSourceAsync(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.CapturedCapitalNpcDialogs.cs");

        AssertEconomyContract(contracts, store, evidence);
        AssertReplayContract(store, evidence, handler);
        AssertReplayProjectionContract();
        CheckSecureFighterExperienceProjection();
        CheckRawFighterExperienceProjection(handler);
        AssertOwnershipContract(store, handler);
        await RunPostgresIntegrationAsync();
    }

    private static void AssertEconomyContract(
        string contracts,
        string store,
        string evidence)
    {
        Check.True(
            contracts.Contains(
                "public const int UnsealBoundGoldCost = 10_000;",
                StringComparison.Ordinal) &&
            store.Contains(
                "status == FighterLevelSealChangeStatus.Unsealed",
                StringComparison.Ordinal) &&
            store.Contains(
                "? FighterLevelSealRules.UnsealBoundGoldCost",
                StringComparison.Ordinal) &&
            store.Contains(
                ": 0;",
                StringComparison.Ordinal),
            "sealing is free and only unsealing costs 10,000 Bound Gold");
        Check.True(
            store.Contains(
                "before.Value.BindingGold - unsealCost",
                StringComparison.Ordinal) &&
            evidence.Contains("'binding_gold'", StringComparison.Ordinal) &&
            evidence.Contains(
                "'fighter_level_unseal'",
                StringComparison.Ordinal),
            "paid unsealing debits Bound Gold through the currency ledger");
        Check.True(
            store.Contains(
                "before.Value.SealRevision + 1",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "fighter_level_seal_revision = @sealRevision",
                StringComparison.Ordinal) &&
            !evidence.Contains(
                "SET progression_reward_revision",
                StringComparison.Ordinal),
            "seal transitions advance only their dedicated revision");
    }

    private static void AssertReplayContract(
        string store,
        string evidence,
        string handler)
    {
        Check.True(
            store.Contains(
                "ReadFighterLevelSealInboxAsync",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "CryptographicOperations.FixedTimeEquals",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "duplicate_count = LEAST(duplicate_count + 1, 1000000)",
                StringComparison.Ordinal) &&
            evidence.Contains("Replayed: true", StringComparison.Ordinal),
            "the same operation replays one hashed durable result");
        Check.True(
            evidence.Contains(
                "FighterLevelSealChangeStatus.RequestHashConflict",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "request_conflict_count",
                StringComparison.Ordinal),
            "a reused operation ID with another request records a conflict");
        Check.True(
            handler.Contains(
                "packet.ClientOperationId ?? Guid.NewGuid()",
                StringComparison.Ordinal) &&
            handler.Contains(
                "_session.IsSecure && !packet.ClientOperationId.HasValue",
                StringComparison.Ordinal),
            "secure Level Sealer requests preserve client operation identity");
        Check.True(
            evidence.Contains(
                "ReplayProjection: new(",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "current.SealRevision < result.SealRevision",
                StringComparison.Ordinal) &&
            handler.Contains(
                "if (result.RequiresLiveProjection)",
                StringComparison.Ordinal) &&
            handler.Contains(
                "var projection = result.Projection;",
                StringComparison.Ordinal) &&
            handler.Contains(
                "SendSecureFighterLevelSealResultAsync(",
                StringComparison.Ordinal) &&
            handler.Contains(
                "result.Projection,",
                StringComparison.Ordinal) &&
            handler.Contains(
                "var previousBindingGold = _character.BindingGold;",
                StringComparison.Ordinal) &&
            handler.Contains(
                "if (result.RequiresWalletRefresh(previousBindingGold))",
                StringComparison.Ordinal) &&
            handler.Contains(
                "if (result.RequiresLiveProjection)",
                StringComparison.Ordinal) &&
            handler.Contains(
                "BuildFighterLevelSealVitalsProjection(_character)",
                StringComparison.Ordinal) &&
            handler.Contains(
                "\"LevelSealerVitalsStatus\"",
                StringComparison.Ordinal) &&
            handler.IndexOf(
                "\"LevelSealerResult\"",
                StringComparison.Ordinal) <
                handler.IndexOf(
                    "\"LevelSealerVitalsStatus\"",
                    StringComparison.Ordinal) &&
            handler.IndexOf(
                "\"LevelSealerWalletStatus\"",
                StringComparison.Ordinal) <
                handler.IndexOf(
                    "\"LevelSealerVitalsStatus\"",
                    StringComparison.Ordinal) &&
            handler.IndexOf(
                "SendSecureFighterLevelSealResultAsync(",
                StringComparison.Ordinal) <
                handler.IndexOf(
                    "\"LevelSealerVitalsStatus\"",
                    StringComparison.Ordinal),
            "a successful replay repairs live seal, wallet, and vitals projection");
    }

    private static void AssertReplayProjectionContract()
    {
        var replay = new FighterLevelSealChangeResult(
            FighterLevelSealChangeStatus.Sealed,
            LevelSealed: true,
            BindingGold: 25_000,
            SealRevision: 1,
            Replayed: true,
            ReplayProjection: new(
                LevelSealed: false,
                BindingGold: 15_000,
                SealRevision: 2));
        Check.True(
            replay.Replayed &&
            !replay.Changed &&
            replay.RequiresLiveProjection &&
            replay.LevelSealed &&
            replay.BindingGold == 25_000 &&
            replay.SealRevision == 1 &&
            !replay.Projection.LevelSealed &&
            replay.Projection.BindingGold == 15_000 &&
            replay.Projection.SealRevision == 2 &&
            replay.RequiresWalletRefresh(previousBindingGold: 25_000),
            "replay preserves its receipt while projecting current state");

        var unsealReplay = new FighterLevelSealChangeResult(
            FighterLevelSealChangeStatus.Unsealed,
            LevelSealed: false,
            BindingGold: 15_000,
            SealRevision: 2,
            Replayed: true,
            ReplayProjection: new(
                LevelSealed: false,
                BindingGold: 15_000,
                SealRevision: 2));
        Check.True(
            unsealReplay.RequiresWalletRefresh(
                previousBindingGold: 15_000),
            "a replayed paid unseal resends a lost wallet projection");

        var malformedReplay = new FighterLevelSealChangeResult(
            FighterLevelSealChangeStatus.Sealed,
            LevelSealed: true,
            BindingGold: 25_000,
            SealRevision: 1,
            Replayed: true);
        InvalidOperationException? malformedRejection = null;
        try
        {
            _ = malformedReplay.Projection;
        }
        catch (InvalidOperationException ex)
        {
            malformedRejection = ex;
        }
        Check.True(
            malformedReplay.RequiresLiveProjection &&
            malformedRejection is not null,
            "a replay cannot silently omit its authoritative projection");
    }

    private static void AssertOwnershipContract(
        string store,
        string handler)
    {
        Check.True(
            store.Contains("ownership.Validate();", StringComparison.Ordinal) &&
            store.Contains(
                "ownershipGuard.LockCurrentAsync",
                StringComparison.Ordinal) &&
            store.Contains(
                "ownershipResult.RequireCurrent();",
                StringComparison.Ordinal) &&
            store.Contains(
                "ownershipGuard.ValidateCurrentAsync",
                StringComparison.Ordinal),
            "the transaction and post-commit boundary enforce player ownership");
        Check.True(
            handler.Contains(
                "TryCaptureCurrentPlayerOwnership",
                StringComparison.Ordinal) &&
            handler.Contains(
                "catch (PlayerOwnershipValidationException)",
                StringComparison.Ordinal) &&
            handler.Contains(
                "RejectLostPlayerOwnership();",
                StringComparison.Ordinal),
            "the handler fails closed when its ownership fence is stale");
    }

    private static Task<string> ReadSourceAsync(
        string root,
        params string[] components) =>
        File.ReadAllTextAsync(Path.Combine([root, .. components]));

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
            "Could not locate the repository root.");
    }
}
