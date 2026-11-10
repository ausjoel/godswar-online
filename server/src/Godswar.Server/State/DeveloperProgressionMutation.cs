using Godswar.Server.Application.Progression;

namespace Godswar.Server.State;

internal static class DeveloperProgressionMutation
{
    public static DeveloperProgressionMutationResult Apply(
        DeveloperProgressionProjection before,
        DeveloperProgressionCommand command)
    {
        ValidateProjection(before);
        if (!command.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var after = before;
        switch (command.Operation)
        {
            case DeveloperProgressionOperation.SetFighterLevel:
                if (before.FighterLevel == command.Value &&
                    before.FighterExperience == 0)
                {
                    return Result(
                        DeveloperProgressionMutationStatus.Unchanged,
                        before,
                        before);
                }

                after = after with
                {
                    FighterLevel = command.Value,
                    FighterExperience = 0
                };
                break;

            case DeveloperProgressionOperation.AdjustFighterLevel:
            {
                var target = (long)before.FighterLevel + command.Value;
                if (target is < 1 or > PlayerExperienceCatalog.MaximumLevel)
                {
                    return Rejected(
                        DeveloperProgressionMutationStatus.TargetOutOfRange,
                        before);
                }

                after = after with
                {
                    FighterLevel = checked((int)target),
                    FighterExperience = 0
                };
                break;
            }

            case DeveloperProgressionOperation.AddTalentPoints:
            {
                var target = (long)before.TalentPoints + command.Value;
                if (target > int.MaxValue)
                {
                    return Rejected(
                        DeveloperProgressionMutationStatus.ArithmeticOverflow,
                        before);
                }

                after = after with { TalentPoints = checked((int)target) };
                break;
            }

            case DeveloperProgressionOperation.AddZodiacEnergy:
            {
                var capacityX100 = checked(
                    (long)ZodiacEnergyCatalog.GetStorageLimit(
                        before.ZodiacLevel) * 100L);
                var currentX100 = checked(
                    (long)before.ZodiacEnergy * 100L +
                    before.ZodiacEnergyRemainderX100);
                var targetX100 = checked(
                    currentX100 + (long)command.Value * 100L);
                if (targetX100 > capacityX100)
                {
                    return Rejected(
                        DeveloperProgressionMutationStatus
                            .ZodiacStorageLimitExceeded,
                        before);
                }

                after = after with
                {
                    ZodiacEnergy = checked((int)(targetX100 / 100L)),
                    ZodiacEnergyRemainderX100 =
                        checked((int)(targetX100 % 100L))
                };
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (before.ProgressionRevision == long.MaxValue)
        {
            return Rejected(
                DeveloperProgressionMutationStatus.RevisionExhausted,
                before);
        }

        after = after with
        {
            ProgressionRevision = checked(
                before.ProgressionRevision + 1)
        };
        ValidateProjection(after);
        return Result(
            DeveloperProgressionMutationStatus.Committed,
            before,
            after);
    }

    internal static void ValidateProjection(
        DeveloperProgressionProjection projection)
    {
        if (projection.FighterLevel is < 1 or >
                PlayerExperienceCatalog.MaximumLevel ||
            projection.FighterExperience is < 0 or >
                PlayerExperienceCatalog.MaximumStoredExperience ||
            projection.TalentPoints < 0 ||
            projection.ZodiacLevel is < 1 or > 30 ||
            projection.ZodiacEnergy < 0 ||
            projection.ZodiacEnergyRemainderX100 is < 0 or > 99 ||
            projection.ProgressionRevision < 0)
        {
            throw new InvalidDataException(
                "Developer progression authority is outside persisted bounds.");
        }

        var storedX100 = checked(
            (long)projection.ZodiacEnergy * 100L +
            projection.ZodiacEnergyRemainderX100);
        var capacityX100 = checked(
            (long)ZodiacEnergyCatalog.GetStorageLimit(
                projection.ZodiacLevel) * 100L);
        if (storedX100 > capacityX100)
        {
            throw new InvalidDataException(
                "Developer progression Zodiac energy exceeds its storage cap.");
        }
    }

    private static DeveloperProgressionMutationResult Rejected(
        DeveloperProgressionMutationStatus status,
        DeveloperProgressionProjection current) =>
        Result(status, current, current);

    private static DeveloperProgressionMutationResult Result(
        DeveloperProgressionMutationStatus status,
        DeveloperProgressionProjection previous,
        DeveloperProgressionProjection current) =>
        new(status, previous, current);
}
