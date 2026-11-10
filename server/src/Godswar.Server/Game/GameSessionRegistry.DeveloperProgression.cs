using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Progression;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task<DeveloperProgressionMutationResult>
        ExecuteDeveloperProgressionAsync(
            ClientSession session,
            IDeveloperProgressionCommandExecutor executor,
            DeveloperProgressionCommandRequest request,
            GameCharacter character,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(character);
        request.Validate();
        RequireCurrentDeveloperProgressionOwner(
            session,
            request,
            character);

        if (!_zodiacOnlineSessions.TryGetValue(
                session,
                out var zodiacState))
        {
            var untracked = await executor.ExecuteAsync(
                request,
                cancellationToken);
            RequireCurrentDeveloperProgressionOwner(
                session,
                request,
                character);
            ApplyDeveloperResult(character, untracked);
            return untracked;
        }

        if (zodiacState.AccountId != request.Subject.AccountId ||
            zodiacState.CharacterId != request.Subject.CharacterId)
        {
            throw new PlayerOwnershipValidationException(
                PlayerOwnershipValidationStatus.OwnershipLost);
        }

        if (_progressionIntervalSettlementCommands is null)
        {
            await zodiacState.Gate.WaitAsync(cancellationToken);
            try
            {
                return await ExecuteUnderDeveloperProgressionGateAsync(
                    session,
                    executor,
                    request,
                    character,
                    zodiacState,
                    durableState: null,
                    cancellationToken);
            }
            finally
            {
                zodiacState.Gate.Release();
            }
        }

        DateTimeOffset onlineAnchor;
        await zodiacState.Gate.WaitAsync(cancellationToken);
        try
        {
            onlineAnchor = zodiacState.LastAccountedAt;
        }
        finally
        {
            zodiacState.Gate.Release();
        }

        // Resolve an unknown prior online-interval result before changing the
        // same durable Zodiac energy row.
        _ = await SettleDurableProgressionIntervalAsync(
            session,
            request.Subject.AccountId,
            request.Subject.CharacterId,
            onlineAnchor,
            onlineAnchor,
            sendNotification: false,
            cancellationToken);
        var durableState = GetOrCreateDurableProgressionSession(
            session,
            request.Subject.AccountId,
            request.Subject.CharacterId,
            onlineAnchor,
            request.Ownership);

        // Match the online-accrual lock order: Zodiac gate, then durable
        // interval gate. The live projection cannot be overwritten by a stale
        // interval snapshot while the developer mutation is being installed.
        await zodiacState.Gate.WaitAsync(cancellationToken);
        try
        {
            await durableState.Gate.WaitAsync(cancellationToken);
            try
            {
                if (durableState.Superseded)
                {
                    throw new PlayerOwnershipValidationException(
                        PlayerOwnershipValidationStatus.OwnershipLost);
                }

                if (durableState.LastProjection is { } latest &&
                    latest.LastIntervalEndUtc >=
                        zodiacState.LastAccountedAt)
                {
                    zodiacState.LastAccountedAt =
                        latest.LastIntervalEndUtc;
                    ApplyDurableProgressionProjection(
                        zodiacState.Character,
                        latest);
                }

                return await ExecuteUnderDeveloperProgressionGateAsync(
                    session,
                    executor,
                    request,
                    character,
                    zodiacState,
                    durableState,
                    cancellationToken);
            }
            finally
            {
                durableState.Gate.Release();
            }
        }
        finally
        {
            zodiacState.Gate.Release();
        }
    }

    private async Task<DeveloperProgressionMutationResult>
        ExecuteUnderDeveloperProgressionGateAsync(
            ClientSession session,
            IDeveloperProgressionCommandExecutor executor,
            DeveloperProgressionCommandRequest request,
            GameCharacter character,
            ZodiacOnlineSessionState zodiacState,
            DurableProgressionOnlineSessionState? durableState,
            CancellationToken cancellationToken)
    {
        RequireCurrentDeveloperProgressionOwner(
            session,
            request,
            character);
        var result = await executor.ExecuteAsync(
            request,
            cancellationToken);
        RequireCurrentDeveloperProgressionOwner(
            session,
            request,
            character);

        ApplyDeveloperResult(zodiacState.Character, result);
        if (!ReferenceEquals(zodiacState.Character, character))
        {
            ApplyDeveloperResult(character, result);
        }

        if (durableState?.LastProjection is { } latest &&
            result.Current is { } current)
        {
            durableState.LastProjection = latest with
            {
                ZodiacEnergy = current.ZodiacEnergy,
                ZodiacEnergyRemainderX100 =
                    current.ZodiacEnergyRemainderX100
            };
        }

        return result;
    }

    private void RequireCurrentDeveloperProgressionOwner(
        ClientSession session,
        DeveloperProgressionCommandRequest request,
        GameCharacter character)
    {
        if (character.Id != request.Subject.CharacterId ||
            character.AccountId != request.Subject.AccountId ||
            character.RealmId != request.RealmId ||
            !IsCurrentWorldOwnership(
                session,
                request.Subject.AccountId,
                request.Subject.CharacterId,
                request.Ownership))
        {
            throw new PlayerOwnershipValidationException(
                PlayerOwnershipValidationStatus.OwnershipLost);
        }
    }

    private static void ApplyDeveloperResult(
        GameCharacter character,
        DeveloperProgressionMutationResult result)
    {
        if (result.Current is { } current)
        {
            ApplyDeveloperProgressionProjection(character, current);
        }
    }

    internal static void ApplyDeveloperProgressionProjection(
        GameCharacter character,
        DeveloperProgressionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(character);
        DeveloperProgressionMutation.ValidateProjection(projection);
        character.Level = projection.FighterLevel;
        character.Experience = projection.FighterExperience;
        character.TalentPoints = projection.TalentPoints;
        lock (character.ZodiacSync)
        {
            character.ZodiacLevel = projection.ZodiacLevel;
            character.ZodiacEnergy = projection.ZodiacEnergy;
            character.ZodiacEnergyRemainderX100 =
                projection.ZodiacEnergyRemainderX100;
        }
    }
}
