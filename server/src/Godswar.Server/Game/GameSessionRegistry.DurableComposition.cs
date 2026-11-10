namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly bool _requiresDurablePlayerPersistence;

    private void ValidateDurablePlayerPersistenceComposition()
    {
        if (!_requiresDurablePlayerPersistence)
        {
            return;
        }

        if (_checkpointCoordinator is null ||
            _progressionIntervalSettlementCommands is null ||
            _zodiacLevelStore is null ||
            _experienceBoosts is null ||
            _characterRuntimeProjections is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL player persistence requires character " +
                "checkpoints, progression interval settlement, focused " +
                "experience-boost and calculated-stat reads, and focused " +
                "Zodiac-level writes.");
        }
    }

    private void RequireLegacyRegistryMutationAllowed(string operation)
    {
        if (!_requiresDurablePlayerPersistence)
        {
            return;
        }

        throw new InvalidOperationException(
            "PostgreSQL player persistence cannot use a broad legacy store " +
            $"mutation: {operation}.");
    }
}
