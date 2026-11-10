namespace Godswar.Server.ProtocolChecks;

internal static class IntonedCombatSkillCheckCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        (WonderlandCombatChecks.ReturnResetCheckName, WonderlandCombatChecks.RunReturnResetAsync),
        (PlayerCombatEcsParityChecks.PlayerPveCriticalCheckName,
            PlayerCombatEcsParityChecks.RunPlayerPveCriticalPolicyAsync),
        (IntonedCombatSkillHandlerChecks.VampiricDamageCheckName,
            IntonedCombatSkillHandlerChecks.RunVampiricDamageAsync),
        (MonsterCombatBalanceChecks.CheckName, MonsterCombatBalanceChecks.RunAsync),
        (GeneralPlayerSkillDamageFormulaChecks.CheckName, GeneralPlayerSkillDamageFormulaChecks.RunAsync),
        (PlayerSkillDamageFormulaChecks.CheckName, PlayerSkillDamageFormulaChecks.RunAsync),
        ("Ordinary intoned combat skill lifecycle", IntonedCombatSkillHandlerChecks.RunAsync),
        (IntonedCombatSkillHandlerChecks.SingleTargetMissCheckName,
            IntonedCombatSkillHandlerChecks.RunSingleTargetMissAsync),
        (IntonedCombatSkillHandlerChecks.SingleTargetPresentationCheckName,
            IntonedCombatSkillHandlerChecks.RunSingleTargetPresentationAsync),
        (IntonedCombatSkillHandlerChecks.LifeAbsorptionFeedbackCheckName,
            IntonedCombatSkillHandlerChecks.RunLifeAbsorptionFeedbackAsync),
        (IntonedCombatSkillHandlerChecks.LifeAbsorptionAreaCheckName,
            IntonedCombatSkillHandlerChecks.RunLifeAbsorptionAreaAsync),
        (IntonedCombatSkillHandlerChecks.FlameBlastPulsesCheckName,
            IntonedCombatSkillHandlerChecks.RunFlameBlastPulsesAsync),
        (IntonedCombatSkillHandlerChecks.FlameBlastLifetimeCheckName,
            IntonedCombatSkillHandlerChecks.RunFlameBlastLifetimeAsync)
    ];
}
