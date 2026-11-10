using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendCapturedCapitalNpcInitialMenuAsync(
        uint npcId,
        NpcDialogueRouteDefinition route,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(
                route.NpcKey,
                npcId,
                out var service) ||
            !CapitalNpcServiceProtocol.TryGetInitialDialogueReply(
                service,
                out var responseDialogIndex,
                out var responseSubIds))
        {
            Console.WriteLine(
                $"[npc] captured capital dialogue has no active menu " +
                $"npc={npcId} dialog={route.DialogIndex}");
            return;
        }

        await _session.SendAsync(
            BuildCapturedCapitalNpcReply(
                service,
                npcId,
                responseDialogIndex,
                responseSubIds),
            cancellationToken,
            "CapturedCapitalNpcInitialMenu");
    }

    private async Task HandleCapturedCapitalNpcFunctionAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(
                route.NpcKey,
                npcId,
                out var service))
        {
            return;
        }

        if (CapitalNpcServiceProtocol.TryGetLevelSealerChange(
                service,
                dialogIndex,
                subId,
                arguments,
                out var desiredSealed))
        {
            uint? rawProjectionCapabilityToken = null;
            if (!_session.IsSecure &&
                RawFighterLevelSealProjectionProtocol.
                    TryReadCapabilityToken(packet, out var capabilityToken))
            {
                rawProjectionCapabilityToken = capabilityToken;
            }
            await HandleFighterLevelSealChangeAsync(
                packet,
                npcId,
                desiredSealed,
                rawProjectionCapabilityToken,
                cancellationToken);
            return;
        }

        if (CapitalNpcServiceProtocol.IsWeekendExperienceClaim(
                service,
                dialogIndex,
                subId,
                arguments))
        {
            await HandleWeekendExperienceClaimAsync(
                npcId,
                cancellationToken);
            return;
        }

        if (CapitalNpcServiceProtocol.TryGetDialogueReply(
                service,
                dialogIndex,
                subId,
                arguments,
                out var responseDialogIndex,
                out var responseSubIds))
        {
            await _session.SendAsync(
                BuildCapturedCapitalNpcReply(
                    service,
                    npcId,
                    responseDialogIndex,
                    responseSubIds),
                cancellationToken,
                "CapturedCapitalNpcDialogueResponse");
            return;
        }

        // The reference server returned no packet for these event claims.
        // Preserve the dialogue click without fabricating items or rewards.
        Console.WriteLine(
            $"[npc] captured capital action unavailable npc={npcId} " +
            $"service={service} dialog={dialogIndex} subId={subId}");
    }

    private async Task HandleFighterLevelSealChangeAsync(
        GamePacket packet,
        uint npcId,
        bool desiredSealed,
        uint? rawProjectionCapabilityToken,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            _fighterLevelSeals is not { } levelSeals)
        {
            Console.Error.WriteLine(
                "[level-sealer] durable character authority unavailable");
            return;
        }
        if (packet.Length != 92 || packet.Buffer.Length != 92)
        {
            await SendFighterLevelSealRejectedAsync(
                npcId,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }
        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.FighterLevelSeal);
            await SendFighterLevelSealRejectedAsync(
                npcId,
                clientOperationId: null,
                cancellationToken);
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("fighter_level_seal"))
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        var operationId = packet.ClientOperationId ?? Guid.NewGuid();

        FighterLevelSealChangeResult result;
        try
        {
            result = await levelSeals.ChangeFighterLevelSealAsync(
                _account.Id,
                _character.Id,
                _processRealmId,
                ownership,
                operationId,
                desiredSealed,
                cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[level-sealer] change failed " +
                $"character={_character.Name}: {ex.Message}");
            return;
        }

        if (result.Status ==
            FighterLevelSealChangeStatus.CharacterNotFound)
        {
            Console.Error.WriteLine(
                $"[level-sealer] character disappeared " +
                $"character={_character.Name}");
            return;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var responseSubId = result.Status switch
        {
            FighterLevelSealChangeStatus.Sealed =>
                CapitalNpcServiceProtocol.LevelSealerSealedSubId,
            FighterLevelSealChangeStatus.Unsealed =>
                CapitalNpcServiceProtocol.LevelSealerUnsealedSubId,
            FighterLevelSealChangeStatus.AlreadySealed =>
                CapitalNpcServiceProtocol.LevelSealerAlreadySealedSubId,
            FighterLevelSealChangeStatus.AlreadyUnsealed =>
                CapitalNpcServiceProtocol.LevelSealerAlreadyUnsealedSubId,
            FighterLevelSealChangeStatus.InsufficientBoundGold =>
                CapitalNpcServiceProtocol.LevelSealerInsufficientFundsSubId,
            FighterLevelSealChangeStatus.RequestHashConflict =>
                CapitalNpcServiceProtocol.LevelSealerUnavailableSubId,
            _ => throw new InvalidDataException(
                "The level-seal result is invalid.")
        };

        var previousBindingGold = _character.BindingGold;
        if (result.RequiresLiveProjection)
        {
            var projection = result.Projection;
            _character.FighterLevelSealed = projection.LevelSealed;
            _character.BindingGold = projection.BindingGold;
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
        }

        var resultPacket = PacketBuilder.NpcFunctionActionResponse(
            npcId,
            CapitalNpcServiceProtocol.LevelSealerDialogIndex,
            responseSubId);
        if (rawProjectionCapabilityToken.HasValue)
        {
            if (result.Status is
                FighterLevelSealChangeStatus.Sealed or
                FighterLevelSealChangeStatus.Unsealed)
            {
                var fighterExperience = BuildFighterExperienceProjection(
                    result.Projection,
                    _character.Level,
                    _character.Experience);
                resultPacket =
                    PacketBuilder.RawFighterLevelSealProjectionResult(
                        npcId,
                        responseSubId,
                        rawProjectionCapabilityToken.Value,
                        fighterExperience.CurrentExperience,
                        fighterExperience.MaximumExperience);
            }
            else
            {
                resultPacket =
                    PacketBuilder.RawFighterLevelSealProjectionResult(
                        npcId,
                        responseSubId,
                        rawProjectionCapabilityToken.Value);
            }
        }
        await _session.SendAsync(
            resultPacket,
            cancellationToken,
            "LevelSealerResult");
        if (result.RequiresWalletRefresh(previousBindingGold))
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "LevelSealerWalletStatus");
        }
        if (_session.IsSecure)
        {
            var disposition = result.Status switch
            {
                FighterLevelSealChangeStatus.RequestHashConflict =>
                    SecureLegacyCommandDisposition.Conflict,
                FighterLevelSealChangeStatus.Sealed or
                FighterLevelSealChangeStatus.Unsealed =>
                    result.Replayed
                        ? SecureLegacyCommandDisposition.Replayed
                        : SecureLegacyCommandDisposition.Applied,
                _ => SecureLegacyCommandDisposition.Rejected
            };
            if (result.Status is
                FighterLevelSealChangeStatus.Sealed or
                FighterLevelSealChangeStatus.Unsealed)
            {
                await SendSecureFighterLevelSealResultAsync(
                    operationId,
                    responseSubId,
                    disposition,
                    result.Projection,
                    cancellationToken);
            }
            else
            {
                await SendSecureGearMentorResultAsync(
                    operationId,
                    CommandFamily.FighterLevelSeal,
                    responseSubId,
                    disposition,
                    result.SealRevision,
                    cancellationToken);
            }
        }
        if (result.RequiresLiveProjection)
        {
            await _session.SendAsync(
                BuildFighterLevelSealVitalsProjection(_character),
                cancellationToken,
                "LevelSealerVitalsStatus");
        }
        Console.WriteLine(
            $"[level-sealer] character={_character.Name} " +
            $"desired={desiredSealed} status={result.Status} " +
            $"level={_character.Level} " +
            $"bindingGold={_character.BindingGold}");
    }

    private async Task SendFighterLevelSealRejectedAsync(
        uint npcId,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                CapitalNpcServiceProtocol.LevelSealerDialogIndex,
                CapitalNpcServiceProtocol.LevelSealerUnavailableSubId),
            cancellationToken,
            "LevelSealerRejected");
        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureGearMentorResultAsync(
                clientOperationId.Value,
                CommandFamily.FighterLevelSeal,
                CapitalNpcServiceProtocol.LevelSealerUnavailableSubId,
                SecureLegacyCommandDisposition.Rejected,
                inventoryRevision: 0,
                cancellationToken);
        }
    }

    private async Task HandleWeekendExperienceClaimAsync(
        uint npcId,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            _weekendExperienceClaims is not { } claims)
        {
            Console.Error.WriteLine(
                "[npc] weekend EXP claim has no durable character authority");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var claimDay = _realmCalendar.GetDay(now);
        if (!WeekendExperienceClaimRules.IsWeekend(claimDay))
        {
            await SendWeekendExperienceClaimReplyAsync(
                npcId,
                responseSubId: 513,
                cancellationToken);
            return;
        }

        WeekendExperienceClaimStatus status;
        try
        {
            status = await claims.ClaimWeekendExperienceAsync(
                _account.Id,
                _character.Id,
                _processRealmId,
                claimDay,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[npc] weekend EXP claim failed " +
                $"character={_character.Name}: {ex.Message}");
            return;
        }

        if (status == WeekendExperienceClaimStatus.CharacterNotFound)
        {
            Console.Error.WriteLine(
                $"[npc] weekend EXP claimant disappeared " +
                $"character={_character.Name}");
            return;
        }

        await SendExperienceBoostStatusAsync(
            "weekend-claim",
            cancellationToken);
        var responseSubId = status == WeekendExperienceClaimStatus.Granted
            ? 511
            : 512;
        await SendWeekendExperienceClaimReplyAsync(
            npcId,
            responseSubId,
            cancellationToken);
        Console.WriteLine(
            $"[npc] weekend EXP claim character={_character.Name} " +
            $"day={claimDay:yyyy-MM-dd} status={status}");
    }

    private Task SendWeekendExperienceClaimReplyAsync(
        uint npcId,
        int responseSubId,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.CapturedNpcFunctionActionResponse(
                npcId,
                28,
                responseSubId,
                3,
                4,
                5),
            cancellationToken,
            "WeekendExperienceClaimResponse");

    private static byte[] BuildCapturedCapitalNpcReply(
        CapitalNpcServiceKind service,
        uint npcId,
        int dialogIndex,
        int[] subIds) =>
        service == CapitalNpcServiceKind.LevelSealer
            ? PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                subIds)
            : PacketBuilder.CapturedNpcFunctionActionResponse(
                npcId,
                dialogIndex,
                subIds);
}
