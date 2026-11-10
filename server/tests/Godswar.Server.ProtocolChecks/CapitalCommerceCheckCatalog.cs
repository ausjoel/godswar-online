namespace Godswar.Server.ProtocolChecks;

internal static class CapitalCommerceCheckCatalog
{
    public static (string Name, Func<Task> Run)[] All { get; } =
    [
        (FactionCrierProtocolChecks.CheckName,
            FactionCrierProtocolChecks.RunAsync),
        (WarehouseContractChecks.CheckName,
            WarehouseContractChecks.RunAsync),
        (CapitalNpcServiceProtocolChecks.CheckName,
            CapitalNpcServiceProtocolChecks.RunAsync),
        (CapitalShopInventoryPlannerChecks.CheckName,
            CapitalShopInventoryPlannerChecks.RunAsync),
        (PostgresCapitalShopPurchaseIntegrationChecks.CheckName,
            PostgresCapitalShopPurchaseIntegrationChecks.RunAsync),
        (PostgresGameplayFeatureAdapterChecks.CheckName,
            PostgresGameplayFeatureAdapterChecks.RunAsync),
        (QuestProtocolChecks.CheckName,
            QuestProtocolChecks.RunAsync),
        (QuestEligibilityHandlerChecks.CheckName,
            QuestEligibilityHandlerChecks.RunAsync),
        (QuestObjectiveSlotChecks.CheckName,
            QuestObjectiveSlotChecks.RunAsync),
        (QuestLoginSnapshotGoldenChecks.CheckName,
            QuestLoginSnapshotGoldenChecks.RunAsync),
        (QuestAnswerDumpChecks.CheckName,
            QuestAnswerDumpChecks.RunAsync),
        (QuestMultiObjectiveCoverageChecks.CheckName,
            QuestMultiObjectiveCoverageChecks.RunAsync),
        (QuestCollectObjectiveChecks.CheckName,
            QuestCollectObjectiveChecks.RunAsync),
        (QuestRuntimeRequirementsChecks.CheckName,
            QuestRuntimeRequirementsChecks.RunAsync),
        (QuestCarryLimitChecks.CheckName,
            QuestCarryLimitChecks.RunAsync),
        (QuestClientFrameLimitChecks.CheckName,
            QuestClientFrameLimitChecks.RunAsync),
        (QuestRelease202611Checks.CheckName,
            QuestRelease202611Checks.RunAsync),
        (SpringFestivalChecks.CheckName, // ROUND3 W3 Spring Festival
            SpringFestivalChecks.RunAsync),
        (SeasonalEventsChecks.CheckName, // W3 seasons
            SeasonalEventsChecks.RunAsync),
        (OfflinePrizeChecks.CheckName, // F-131 offline prize
            OfflinePrizeChecks.RunAsync),
        (EasterAnniversaryChecks.CheckName, // W3 seasons easter/igg
            EasterAnniversaryChecks.RunAsync),
        (TroyEventChecks.CheckName, // W2 F-27 TROY
            TroyEventChecks.RunAsync),
        (TroyBossChecks.CheckName, // W2 F-27 TROY slice 2
            TroyBossChecks.RunAsync),
        (SkillDotChecks.CheckName, // W2 skill statuses
            SkillDotChecks.RunAsync),
        (MidAutumnChecks.CheckName, // W3 seasons mid-autumn
            MidAutumnChecks.RunAsync),
        (RookieGrowthChecks.CheckName, // F-138 newbie growth rewards
            RookieGrowthChecks.RunAsync),
        (PetQuestDispatchChecks.CheckName, // W3 Magic Bell
            PetQuestDispatchChecks.RunAsync),
        (HermesHoodPetRenameChecks.CheckName, // ITEMS-HERMES F-55 / F-56
            HermesHoodPetRenameChecks.RunAsync),
        (QuestLevelCapRaiseChecks.CheckName, // FINALISE2b
            QuestLevelCapRaiseChecks.RunAsync),
        (QuestLevelCapRaise120Checks.CheckName, // ROUND3 F-123
            QuestLevelCapRaise120Checks.RunAsync),
        (OrientalCarnivalChecks.CheckName, // W3 seasons oriental/carnival
            OrientalCarnivalChecks.RunAsync),
        (NorthQuestGiverChecks.CheckName,
            NorthQuestGiverChecks.RunAsync),
        (HalloweenMayDayChecks.CheckName, // W3 seasons halloween/may day
            HalloweenMayDayChecks.RunAsync),
        (QuestNpcMarkListChecks.CheckName,
            QuestNpcMarkListChecks.RunAsync),
        (QuestAppraisalPacketChecks.CheckName,
            QuestAppraisalPacketChecks.RunAsync),
        (SkillBookBroadcastChecks.CheckName,
            SkillBookBroadcastChecks.RunAsync),
        (QuestRewardItemChannelChecks.CheckName,
            QuestRewardItemChannelChecks.RunAsync),
        (PostgresQuestRewardItemIntegrationChecks.CheckName,
            PostgresQuestRewardItemIntegrationChecks.RunAsync),
        (PostgresMigrationHistoryDiagnosticChecks.CheckName,
            PostgresMigrationHistoryDiagnosticChecks.RunAsync),
        (PostgresItemAcquisitionPolicyDiagnosticChecks.CheckName,
            PostgresItemAcquisitionPolicyDiagnosticChecks.RunAsync)
    ];
}
