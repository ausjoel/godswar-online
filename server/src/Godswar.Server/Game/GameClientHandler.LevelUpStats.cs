using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Re-reads the derived statistics a level-up has changed, keeping the vitals
    /// the character is actually at.
    /// </summary>
    /// <remarks>
    /// The maxima come from the database after the level was written, while the
    /// current hit points and mana are clamped into the new range instead of being
    /// restored: a reward that lands mid-fight must not refund the mana the killing
    /// blow cost. Failures are logged and skipped because the persisted level stands
    /// on its own - the client simply keeps its previous maxima until the next
    /// status frame.
    /// </remarks>
    private async Task RefreshLevelUpStatsAsync(
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        try
        {
            var refreshedProjection =
                await _characterRuntimeProjections.ReadCalculatedStatsAsync(
                    _account.Id,
                    character.Id,
                    cancellationToken);
            if (refreshedProjection is null)
            {
                return;
            }

            var refreshedStats =
                CharacterLoadSnapshotHydrator.MapCalculatedStats(
                    refreshedProjection);
            lock (character.VitalsSync)
            {
                var currentHp = character.CurrentHp;
                var currentMp = character.CurrentMp;
                refreshedStats.ApplyTo(character);
                ApplyElementalPassiveStats(character, refreshedStats);
                character.CurrentHp =
                    Math.Clamp(currentHp, 0, character.MaxHp);
                character.CurrentMp =
                    Math.Clamp(currentMp, 0, character.MaxMp);
                character.MarkVitalsChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[progression] level-up stat refresh deferred " +
                $"character={character.Name}: {ex.Message}");
        }
    }
}
