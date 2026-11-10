namespace Godswar.Server.Networking.Secure;

internal enum SecureLegacyCommandDisposition : byte
{
    Applied = 1,
    Replayed = 2,
    Rejected = 3,
    Conflict = 4
}

internal readonly record struct SecureFighterExperienceProjection
{
    public SecureFighterExperienceProjection(
        uint currentExperience,
        uint maximumExperience)
    {
        if (maximumExperience == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExperience),
                "The fighter EXP maximum must be nonzero.");
        }
        CurrentExperience = currentExperience;
        MaximumExperience = maximumExperience;
    }

    public uint CurrentExperience { get; }

    public uint MaximumExperience { get; }
}

internal readonly record struct SecureLegacyCommandResult
{
    public SecureLegacyCommandResult(
        SecureLegacyCommandDisposition disposition,
        ushort commandFamily,
        uint resultCode,
        ulong authoritativeRevision,
        Guid operationId)
        : this(
            disposition,
            commandFamily,
            resultCode,
            authoritativeRevision,
            operationId,
            fighterExperienceProjection: null)
    {
    }

    public SecureLegacyCommandResult(
        SecureLegacyCommandDisposition disposition,
        ushort commandFamily,
        uint resultCode,
        ulong authoritativeRevision,
        Guid operationId,
        SecureFighterExperienceProjection fighterExperienceProjection)
        : this(
            disposition,
            commandFamily,
            resultCode,
            authoritativeRevision,
            operationId,
            (SecureFighterExperienceProjection?)fighterExperienceProjection)
    {
    }

    private SecureLegacyCommandResult(
        SecureLegacyCommandDisposition disposition,
        ushort commandFamily,
        uint resultCode,
        ulong authoritativeRevision,
        Guid operationId,
        SecureFighterExperienceProjection? fighterExperienceProjection)
    {
        if (!SecureProtocolValidation.IsLegacyCommandDisposition(
                disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        if (commandFamily == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandFamily));
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The client operation ID must be nonzero.",
                nameof(operationId));
        }
        if (disposition == SecureLegacyCommandDisposition.Applied &&
            authoritativeRevision == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeRevision),
                "An applied durable command must identify its authoritative revision.");
        }
        if (fighterExperienceProjection.HasValue &&
            !CanCarryFighterExperienceProjection(
                disposition,
                commandFamily,
                resultCode,
                authoritativeRevision))
        {
            throw new ArgumentException(
                "A fighter EXP projection is valid only for a successful " +
                "applied or replayed Fighter Level Seal result.",
                nameof(fighterExperienceProjection));
        }

        Disposition = disposition;
        CommandFamily = commandFamily;
        ResultCode = resultCode;
        AuthoritativeRevision = authoritativeRevision;
        OperationId = operationId;
        FighterExperienceProjection = fighterExperienceProjection;
    }

    public SecureLegacyCommandDisposition Disposition { get; }

    public ushort CommandFamily { get; }

    public uint ResultCode { get; }

    public ulong AuthoritativeRevision { get; }

    // Compatibility alias for inventory command callers. The version-1 wire
    // field is aggregate-owned and is not limited to inventory aggregates.
    public ulong InventoryRevision => AuthoritativeRevision;

    public Guid OperationId { get; }

    public SecureFighterExperienceProjection?
        FighterExperienceProjection { get; }

    internal static bool CanCarryFighterExperienceProjection(
        SecureLegacyCommandDisposition disposition,
        ushort commandFamily,
        uint resultCode,
        ulong authoritativeRevision) =>
        commandFamily ==
            SecureProtocolConstants.FighterLevelSealCommandFamily &&
        (resultCode is
            SecureProtocolConstants.FighterLevelSealedResultCode or
            SecureProtocolConstants.FighterLevelUnsealedResultCode) &&
        (disposition is
            SecureLegacyCommandDisposition.Applied or
            SecureLegacyCommandDisposition.Replayed) &&
        authoritativeRevision != 0;
}
