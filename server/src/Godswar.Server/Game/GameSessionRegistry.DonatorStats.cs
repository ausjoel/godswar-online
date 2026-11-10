using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static bool HasDonatorStatProjectionChanged(
        ExperienceBoostState previous,
        ExperienceBoostState current) =>
        ResolveDonatorStatIdentity(previous) !=
        ResolveDonatorStatIdentity(current);

    private async Task<bool> RefreshDonatorCalculatedStatsAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        // Compatibility-only registries may have no durable projection
        // authority. PostgreSQL composition requires this reader at startup.
        if (_characterRuntimeProjections is null)
        {
            return true;
        }

        if (!_sessions.TryGetValue(session, out var context))
        {
            return false;
        }

        var projection = await _characterRuntimeProjections
            .ReadCalculatedStatsAsync(
                context.AccountId,
                context.CharacterId,
                cancellationToken);
        if (projection is null)
        {
            throw new InvalidDataException(
                "A donator status change requires a calculated-stat " +
                $"projection for character {context.CharacterId}.");
        }
        if (projection.AccountId != context.AccountId ||
            projection.CharacterId != context.CharacterId)
        {
            throw new InvalidDataException(
                "The calculated-stat projection returned a different " +
                "account or character during a donator status change.");
        }
        if (!_sessions.TryGetValue(session, out var currentContext) ||
            !ReferenceEquals(currentContext, context))
        {
            return false;
        }

        var stats = CharacterLoadSnapshotHydrator.MapCalculatedStats(
            projection);
        CharacterCalculatedStatsProjectionApplier.Apply(
            context.Character,
            stats,
            CharacterHealthProjectionMode.PreservePercentage);
        UpdateCharacter(
            session,
            context.Character,
            advanceWorldRevision: false);

        return _sessions.TryGetValue(session, out currentContext) &&
               ReferenceEquals(currentContext, context);
    }

    private static DonatorStatIdentity? ResolveDonatorStatIdentity(
        ExperienceBoostState state)
    {
        var boost = state.ActiveBoosts.FirstOrDefault(
            static candidate =>
                candidate.Kind == ExperienceBoostKinds.Donator);
        return boost is null
            ? null
            : new DonatorStatIdentity(
                boost.StatusId,
                boost.Priority);
    }

    private readonly record struct DonatorStatIdentity(
        int StatusId,
        int Tier);
}
