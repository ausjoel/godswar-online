namespace Godswar.Server.ProtocolChecks;

internal static class WorldTravelCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (InstanceCallerHandlerChecks.TitleSelectionCheckName,
            InstanceCallerHandlerChecks.RunTitleSelectionAsync),
        (MedusaRewardProjectionRevisionChecks.CheckName,
            MedusaRewardProjectionRevisionChecks.RunAsync),
        (PostgresAtlantisCompletionRewardChecks.TitleSelectionCheckName,
            PostgresAtlantisCompletionRewardChecks.RunTitleSelectionAsync),
        (PostgresAtlantisCompletionRewardChecks.CheckName,
            PostgresAtlantisCompletionRewardChecks.RunAsync),
        (AtlantisCompletionRewardPolicyChecks.CheckName,
            AtlantisCompletionRewardPolicyChecks.RunAsync),
        (InstanceCallerHandlerChecks.AtlantisCompletionRewardsCheckName,
            InstanceCallerHandlerChecks.RunAtlantisCompletionRewardsAsync),
        (InstanceCallerHandlerChecks.AtlantisCompletionUiCheckName,
            InstanceCallerHandlerChecks.RunAtlantisCompletionUiAsync),
        (InstanceCallerHandlerChecks.AtlantisPetCaptureCheckName,
            InstanceCallerHandlerChecks.RunAtlantisPetCaptureAsync),
        (AtlantisPetSpawnChecks.CheckName,
            AtlantisPetSpawnChecks.RunAsync),
        (TalentExperienceProgressionChecks.CheckName,
            TalentExperienceProgressionChecks.RunAsync),
        (AtlantisMonsterCombatChecks.CheckName,
            AtlantisMonsterCombatChecks.RunAsync),
        (InstanceCallerHandlerChecks.AtlantisDepartureCheckName,
            InstanceCallerHandlerChecks.RunAtlantisDepartureAsync),
        (InstanceCallerHandlerChecks.AtlantisTerminationCheckName,
            InstanceCallerHandlerChecks.RunAtlantisTerminationAsync),
        (AtlantisSpawnBalanceChecks.CheckName, AtlantisSpawnBalanceChecks.RunAsync),
        (MonsterDeathRewardCommitObserverChecks.CheckName, MonsterDeathRewardCommitObserverChecks.RunAsync),
        (AtlantisLiveWaveChecks.CheckName, AtlantisLiveWaveChecks.RunAsync),
        (InstanceCallerHandlerChecks.AtlantisRetirementCheckName,
            InstanceCallerHandlerChecks.RunAtlantisRetirementAsync),
        (AtlantisWavePlanChecks.CheckName, AtlantisWavePlanChecks.RunAsync),
        (AtlantisWaveRuntimeChecks.CheckName, AtlantisWaveRuntimeChecks.RunAsync),
        (AtlantisMonsterTemplatePolicyChecks.CheckName, AtlantisMonsterTemplatePolicyChecks.RunAsync),
        (AtlantisEncounterPolicyChecks.CheckName, AtlantisEncounterPolicyChecks.RunAsync),
        (AtlantisRunRuntimeChecks.CheckName, AtlantisRunRuntimeChecks.RunAsync),
        (AtlantisMonsterKillScoringChecks.CheckName, AtlantisMonsterKillScoringChecks.RunAsync),
        (InstanceCallerHandlerChecks.AtlantisRunCheckName,
            InstanceCallerHandlerChecks.RunAtlantisRunAsync),
        (InstanceCallerHandlerChecks.HarborAttackCheckName,
            InstanceCallerHandlerChecks.RunHarborAttackAsync),
        (InstanceCallerHandlerChecks.ClosedInstancesCheckName,
            InstanceCallerHandlerChecks.RunClosedInstancesAsync),
        (HarborAttackContentChecks.CheckName, HarborAttackContentChecks.RunAsync),
        (HarborAttackCompletionRewardChecks.CheckName, HarborAttackCompletionRewardChecks.RunAsync), // BAY2
        (HarborAttackCompletionRewardChecks.PostgresCheckName, HarborAttackCompletionRewardChecks.RunPostgresCheckAsync), // BAY2
        (TrainingGroundChecks.CheckName, TrainingGroundChecks.RunAsync), // TRAINING
        (WarriorTrainingGroundChecks.CheckName, WarriorTrainingGroundChecks.RunAsync), // WARRIOR (F-51)
        (WarriorTrainingScrollChecks.CheckName, WarriorTrainingScrollChecks.RunAsync), // WARRIOR scrolls (F-146 b)
        (InstanceCallerHandlerChecks.WarriorTrainingPagesCheckName,
            InstanceCallerHandlerChecks.RunWarriorTrainingPagesAsync), // WARRIOR (F-51)
        ("Authoritative hidden live-map transfer", MapLiveTransferChecks.RunAsync),
        (MapLiveTransferChecks.MutationLifetimeCheckName,
            MapLiveTransferChecks.RunMutationLifetimeAsync),
        (BackhaulSkillHandlerChecks.TransitionCommitCheckName,
            BackhaulSkillHandlerChecks.RunTransitionCommitAsync),
        (MapLiveTransferChecks.TerminalEgressCheckName,
            MapLiveTransferChecks.RunTerminalEgressAsync),
        ("Native handler map-transition readiness", MapTransitionHandlerChecks.RunAsync),
        ("Native faction backhaul skill catalog", BackhaulSkillCatalogChecks.RunAsync),
        (FactionPortalSkillPolicyChecks.CheckName, FactionPortalSkillPolicyChecks.RunAsync),
        (
            FactionPortalSkillCreationArchitectureChecks.CheckName,
            FactionPortalSkillCreationArchitectureChecks.RunAsync),
        (
            BackhaulSkillHandlerChecks.NativeSuccessCheckName,
            BackhaulSkillHandlerChecks.RunNativeSuccessAsync),
        (
            BackhaulSkillHandlerChecks.SameMapCheckName,
            BackhaulSkillHandlerChecks.RunSameMapAsync),
        (
            BackhaulSkillHandlerChecks.FactionBoundaryCheckName,
            BackhaulSkillHandlerChecks.RunFactionBoundaryAsync),
        ("Authoritative faction backhaul casting", BackhaulSkillHandlerChecks.RunAsync),
        ("Authoritative map traversal catalog", MapTraversalCatalogChecks.RunAsync),
        (TransporterProtocolChecks.CheckName, TransporterProtocolChecks.RunAsync),
        (TransporterHandlerChecks.CheckName, TransporterHandlerChecks.RunAsync),
        (
            BattlefieldTransporterProtocolChecks.CheckName,
            BattlefieldTransporterProtocolChecks.RunAsync),
        (
            DuelArenaTransporterProtocolChecks.CheckName,
            DuelArenaTransporterProtocolChecks.RunAsync),
        (
            DuelArenaSpawnStreamChecks.CheckName,
            DuelArenaSpawnStreamChecks.RunAsync),
        (
            DuelArenaCapturedProtocolChecks.CheckName,
            DuelArenaCapturedProtocolChecks.RunAsync),
        (
            DuelArenaTransporterHandlerChecks.CheckName,
            DuelArenaTransporterHandlerChecks.RunAsync),
        (
            WarehouseHandlerChecks.ArenaSupportCheckName,
            WarehouseHandlerChecks.RunArenaSupportAsync),
        (
            BattlefieldSchedulePolicyChecks.CheckName,
            BattlefieldSchedulePolicyChecks.RunAsync),
        (
            LelantineFarmProtocolChecks.CheckName,
            LelantineFarmProtocolChecks.RunAsync),
        (
            LelantineFarmShopChecks.CheckName,
            LelantineFarmShopChecks.RunAsync),
        (
            Finalise2NorthChecks.CheckName, // FINALISE2
            Finalise2NorthChecks.RunAsync),
        (
            WorldRuntimeSpawnChecks.CheckName, // ROUND3 runtime spawns
            WorldRuntimeSpawnChecks.RunAsync)
    ];
}
