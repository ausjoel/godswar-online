using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static void ApplyElementalPassiveStats(
        GameCharacter character,
        CharacterStats baseStats)
    {
        var maximumHealth = CharacterCalculatedStatsProjectionApplier
            .ResolveEffectiveMaximumHealth(
            character,
            baseStats);
        lock (character.VitalsSync)
        {
            character.MaxHp = maximumHealth;
            character.CurrentHp = Math.Clamp(
                character.CurrentHp,
                0,
                character.MaxHp);
        }
    }
}
