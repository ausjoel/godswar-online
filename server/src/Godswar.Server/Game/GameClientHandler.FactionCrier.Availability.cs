using Godswar.Server.Application.Characters;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal static FactionCrierExecutionDisposition?
        EvaluateFactionCrierAvailability(
        int? characterLevel,
        FactionCrierOperation operation,
        DateTimeOffset receivedAt,
        FactionCrierBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        out DateOnly realmDay)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(realmCalendar);
        realmDay = realmCalendar.GetDay(receivedAt);
        if (characterLevel is null or < 1 ||
            characterLevel >
                CharacterProgressionSnapshotRules.MaximumCharacterLevel)
        {
            return FactionCrierExecutionDisposition.PreconditionFailed;
        }
        if (characterLevel < balance.MinimumLevel)
        {
            return FactionCrierExecutionDisposition.BelowMinimumLevel;
        }
        if (operation == FactionCrierOperation.DailyClaim &&
            realmDay.DayOfWeek == DayOfWeek.Sunday)
        {
            return FactionCrierExecutionDisposition.ClosedToday;
        }

        return null;
    }
}
