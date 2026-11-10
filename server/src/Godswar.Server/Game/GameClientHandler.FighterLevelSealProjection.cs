using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal static byte[] BuildFighterLevelSealVitalsProjection(
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        lock (character.VitalsSync)
        {
            return PacketBuilder.PlayerVitalsUpdate(
                LocalPlayerObjectId,
                character.CurrentHp,
                character.CurrentMp);
        }
    }

    private ValueTask SendSecureFighterLevelSealResultAsync(
        Guid operationId,
        int resultSubId,
        SecureLegacyCommandDisposition disposition,
        FighterLevelSealProjection projection,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            throw new InvalidOperationException(
                "A Fighter Level Seal result requires a live character.");
        }

        return _session.SendLegacyCommandResultAsync(
            BuildSecureFighterLevelSealResult(
                operationId,
                resultSubId,
                disposition,
                projection,
                _character.Level,
                _character.Experience),
            cancellationToken);
    }

    internal static SecureLegacyCommandResult
        BuildSecureFighterLevelSealResult(
            Guid operationId,
            int resultSubId,
            SecureLegacyCommandDisposition disposition,
            FighterLevelSealProjection projection,
            int currentLevel,
            long currentExperience)
    {
        return new SecureLegacyCommandResult(
            disposition,
            checked((ushort)CommandFamily.FighterLevelSeal),
            checked((uint)resultSubId),
            checked((ulong)projection.SealRevision),
            operationId,
            BuildFighterExperienceProjection(
                projection,
                currentLevel,
                currentExperience));
    }

    internal static SecureFighterExperienceProjection
        BuildFighterExperienceProjection(
            FighterLevelSealProjection projection,
            int currentLevel,
            long currentExperience) =>
        new(
            checked((uint)currentExperience),
            checked((uint)
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    currentLevel,
                    projection.LevelSealed)));
}
