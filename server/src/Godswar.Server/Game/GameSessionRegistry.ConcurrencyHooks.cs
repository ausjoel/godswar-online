namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Action? RuntimeStatusSessionLookupHook { get; set; }
    internal Action? MonsterCombatTargetProjectionStartingHook { get; set; }
    internal Action? PveElementalVitalsLockedHook { get; set; }
}
