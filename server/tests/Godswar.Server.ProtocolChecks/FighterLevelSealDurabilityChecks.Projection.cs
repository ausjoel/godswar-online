using Godswar.Server.Application.Progression;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FighterLevelSealDurabilityChecks
{
    private static readonly Guid ProjectionOperationId =
        Guid.Parse("df1699ce-9c22-41f8-8f0d-fda8d0d57e49");

    private static void CheckSecureFighterExperienceProjection()
    {
        CheckLiveVitalsProjection();
        const int level = 160;
        const uint currentExperience = 4_000_000_000;

        var sealedResult =
            GameClientHandler.BuildSecureFighterLevelSealResult(
                ProjectionOperationId,
                resultSubId: 106,
                SecureLegacyCommandDisposition.Applied,
                new FighterLevelSealProjection(
                    LevelSealed: true,
                    BindingGold: 25_000,
                    SealRevision: 11),
                level,
                currentExperience);
        Check.True(
            sealedResult.AuthoritativeRevision == 11 &&
            sealedResult.FighterExperienceProjection is
            {
                CurrentExperience: currentExperience,
                MaximumExperience: uint.MaxValue
            },
            "applied seal projects current EXP with the UInt32 ceiling");
        AssertV2Length(sealedResult, "applied seal");

        var unsealedResult =
            GameClientHandler.BuildSecureFighterLevelSealResult(
                ProjectionOperationId,
                resultSubId: 107,
                SecureLegacyCommandDisposition.Applied,
                new FighterLevelSealProjection(
                    LevelSealed: false,
                    BindingGold: 15_000,
                    SealRevision: 12),
                level,
                currentExperience: uint.MaxValue);
        Check.True(
            unsealedResult.FighterExperienceProjection is
            {
                CurrentExperience: uint.MaxValue,
                MaximumExperience: 117_174_640
            },
            "applied unseal restores the level table cap without truncating EXP");

        var replay = new FighterLevelSealChangeResult(
            FighterLevelSealChangeStatus.Sealed,
            LevelSealed: true,
            BindingGold: 25_000,
            SealRevision: 1,
            Replayed: true,
            ReplayProjection: new FighterLevelSealProjection(
                LevelSealed: false,
                BindingGold: 15_000,
                SealRevision: 2));
        var replayedResult =
            GameClientHandler.BuildSecureFighterLevelSealResult(
                ProjectionOperationId,
                resultSubId: 106,
                SecureLegacyCommandDisposition.Replayed,
                replay.Projection,
                level,
                currentExperience);
        Check.True(
            replayedResult.AuthoritativeRevision == 2 &&
            replayedResult.FighterExperienceProjection is
            {
                CurrentExperience: currentExperience,
                MaximumExperience: 117_174_640
            },
            "old seal replay carries the current unsealed projection and revision");

        var noOp = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Rejected,
            SecureProtocolConstants.FighterLevelSealCommandFamily,
            resultCode: 109,
            authoritativeRevision: 12,
            ProjectionOperationId);
        Check.True(
            noOp.FighterExperienceProjection is null,
            "Level Sealer no-op result has no unsafe EXP projection");
        var v1 = new byte[
            SecureProtocolConstants.LegacyCommandResultV2Bytes];
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                noOp,
                v1,
                out var noOpLength) &&
            noOpLength == SecureProtocolConstants.LegacyCommandResultBytes,
            "Level Sealer no-op remains the compatible v1 result");
        Check.Throws<ArgumentException>(
            () => _ = GameClientHandler.BuildSecureFighterLevelSealResult(
                ProjectionOperationId,
                resultSubId: 109,
                SecureLegacyCommandDisposition.Rejected,
                replay.Projection,
                level,
                currentExperience),
            "rejected Level Sealer result cannot acquire an EXP projection");
    }

    private static void CheckLiveVitalsProjection()
    {
        var character = new GameCharacter
        {
            CurrentHp = 196_614,
            CurrentMp = 2_559,
            VitalsRevision = 77
        };
        var packet =
            GameClientHandler.BuildFighterLevelSealVitalsProjection(
                character);
        Check.True(
            packet.Length == 16 &&
            ReadUInt16(packet, 0) == packet.Length &&
            ReadUInt16(packet, 2) == 0x2771 &&
            ReadUInt32(packet, 4) == 0x0000_1448 &&
            ReadUInt32(packet, 8) == 196_614 &&
            ReadUInt32(packet, 12) == 2_559 &&
            character.CurrentHp == 196_614 &&
            character.CurrentMp == 2_559 &&
            character.VitalsRevision == 77,
            "Level Sealer postcondition reprojects exact live HP and MP " +
            "without mutating authoritative vitals");
    }

    private static void AssertV2Length(
        SecureLegacyCommandResult result,
        string context)
    {
        var bytes = new byte[
            SecureProtocolConstants.LegacyCommandResultV2Bytes];
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                result,
                bytes,
                out var written) &&
            written == SecureProtocolConstants.LegacyCommandResultV2Bytes,
            $"{context} uses the bounded v2 result");
    }
}
