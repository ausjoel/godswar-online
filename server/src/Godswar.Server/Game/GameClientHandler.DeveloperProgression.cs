using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IDeveloperProgressionCommandExecutor?
        _developerProgressionCommands;

    private async Task<bool> HandleDeveloperProgressionCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text))
        {
            return false;
        }

        // ROUND2: "/level set 50 @Name" runs the command on another online
        // character (GM only). The "@Name" token is stripped before parsing.
        var targetName = ExtractOperatorTargetSuffix(ref text);
        if (!DeveloperProgressionChatCommand.TryParse(
                text,
                out var command,
                out var error))
        {
            return false;
        }

        // Recognized developer commands are consumed even when denied. They
        // must never become public map chat or disclose access policy.
        if (_account is null ||
            _character is null ||
            !await HasOperatorAuthorityAsync(cancellationToken))
        {
            Console.WriteLine(
                "[developer-progression] denied " +
                $"account={_account?.Id ?? 0} " +
                $"character={_character?.Name ?? "none"}");
            return true;
        }

        if (command is null)
        {
            await SendDeveloperItemFeedbackAsync(
                packet,
                $"[progression] {error}",
                cancellationToken);
            return true;
        }

        if (targetName is not null)
        {
            await RunProgressionOnOtherCharacterAsync(
                packet,
                command.Value,
                targetName,
                cancellationToken);
            return true;
        }

        await ExecuteDeveloperProgressionCommandAsync(
            packet,
            command.Value,
            cancellationToken);
        return true;
    }

    // ROUND2: the execution half of the command, unchanged, so a GM can run it
    // on another character's handler (OperatorProgressionAction).
    private async Task<string> ExecuteDeveloperProgressionCommandAsync(
        GamePacket packet,
        DeveloperProgressionCommand commandValue,
        CancellationToken cancellationToken)
    {
        DeveloperProgressionCommand? command = commandValue;
        if (_account is null || _character is null)
        {
            return "the character is not in the world";
        }

        if (_developerProgressionCommands is null)
        {
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[progression] Durable progression commands are unavailable.",
                cancellationToken);
            return "durable progression commands are unavailable";
        }

        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return "the character's ownership changed";
        }

        var request = new DeveloperProgressionCommandRequest(
            new CommandSubject(_account.Id, _character.Id),
            _processRealmId,
            ownership,
            command.Value);
        DeveloperProgressionMutationResult result;
        try
        {
            result = await _registry.ExecuteDeveloperProgressionAsync(
                _session,
                _developerProgressionCommands,
                request,
                _character,
                cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return "the character's ownership changed";
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return "the character's ownership changed";
        }

        if (!result.IsSuccess)
        {
            await SendDeveloperProgressionRejectionAsync(
                packet,
                command.Value,
                result,
                cancellationToken);
            return $"refused: {result.Status}";
        }

        var previous = result.Previous ??
            throw new InvalidDataException(
                "A successful developer progression command has no prior projection.");
        var current = result.Current ??
            throw new InvalidDataException(
                "A successful developer progression command has no current projection.");
        if (result.Changed && command.Value.Operation is
                DeveloperProgressionOperation.SetFighterLevel or
                DeveloperProgressionOperation.AdjustFighterLevel)
        {
            try
            {
                await RefreshActiveCharacterStatsAsync(
                    "developer-level-adjustment",
                    cancellationToken);
                if (!RevalidateCurrentPlayerOwnership(ownership))
                {
                    return "the character's ownership changed";
                }

                await _registry.PersistPlayerVitalsAsync(
                    _account.Id,
                    _character,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The progression commit remains authoritative. The refresh
                // path imports derived maxima without restoring database HP/MP.
                Console.WriteLine(
                    "[developer-progression] level stat refresh deferred " +
                    $"character={_character.Name}: {ex.Message}");
            }
        }

        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision:
                command.Value.Operation is
                    DeveloperProgressionOperation.SetFighterLevel or
                    DeveloperProgressionOperation.AdjustFighterLevel);
        await PublishDeveloperProgressionProjectionAsync(
            packet,
            command.Value,
            previous,
            current,
            result.Changed,
            cancellationToken);
        Console.WriteLine(
            "[developer-progression] completed " +
            $"account={_account.Id} character={_character.Name} " +
            $"operation={command.Value.Operation} value={command.Value.Value} " +
            $"status={result.Status} revision={current.ProgressionRevision}");
        return $"{command.Value.Operation} {command.Value.Value}: {result.Status}";
    }

    private async Task PublishDeveloperProgressionProjectionAsync(
        GamePacket packet,
        DeveloperProgressionCommand command,
        DeveloperProgressionProjection previous,
        DeveloperProgressionProjection current,
        bool changed,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        switch (command.Operation)
        {
            case DeveloperProgressionOperation.SetFighterLevel:
            case DeveloperProgressionOperation.AdjustFighterLevel:
                await _session.SendAsync(
                    PacketBuilder.PlayerLevelUp(
                        LocalPlayerObjectId,
                        current.FighterLevel,
                        PlayerExperienceCatalog.GetClientExperienceMaximum(
                            current.FighterLevel,
                            _character.FighterLevelSealed),
                        current.FighterExperience,
                        _character.MaxHp,
                        _character.CurrentHp,
                        _character.MaxMp,
                        _character.CurrentMp),
                    cancellationToken,
                    "DeveloperFighterLevelRefresh");
                if (changed)
                {
                    await _registry.BroadcastToMapAsync(
                        _character.CurrentMap,
                        PacketBuilder.PlayerLevelUp(
                            CurrentPlayerObjectId,
                            current.FighterLevel,
                            PlayerExperienceCatalog
                                .GetClientExperienceMaximum(
                                    current.FighterLevel,
                                    _character.FighterLevelSealed),
                            current.FighterExperience,
                            _character.MaxHp,
                            _character.CurrentHp,
                            _character.MaxMp,
                            _character.CurrentMp),
                        cancellationToken,
                        _session,
                        "DeveloperFighterLevelWorldRefresh");
                }

                break;

            case DeveloperProgressionOperation.AddTalentPoints:
                if (changed)
                {
                    await _session.SendAsync(
                        PacketBuilder.TalentPointGain(
                            checked(
                                current.TalentPoints -
                                previous.TalentPoints)),
                        cancellationToken,
                        "DeveloperTalentPointGain");
                }

                break;

            case DeveloperProgressionOperation.AddZodiacEnergy:
                if (changed)
                {
                    var gainedX100 = checked((int)(
                        ((long)current.ZodiacEnergy * 100L +
                         current.ZodiacEnergyRemainderX100) -
                        ((long)previous.ZodiacEnergy * 100L +
                         previous.ZodiacEnergyRemainderX100)));
                    await _session.SendAsync(
                        PacketBuilder.ZodiacEnergyIncrease(
                            current.ZodiacEnergy,
                            gainedX100),
                        cancellationToken,
                        "DeveloperZodiacEnergyGain");
                }

                await SendZodiacFullSyncAsync(cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "DeveloperProgressionStatusRefresh");
        await SendDeveloperItemFeedbackAsync(
            packet,
            SuccessFeedback(command, current, changed),
            cancellationToken);
    }

    private async Task SendDeveloperProgressionRejectionAsync(
        GamePacket packet,
        DeveloperProgressionCommand command,
        DeveloperProgressionMutationResult result,
        CancellationToken cancellationToken)
    {
        var message = result.Status switch
        {
            DeveloperProgressionMutationStatus.CharacterNotFound =>
                "The active character is no longer available.",
            DeveloperProgressionMutationStatus.TargetOutOfRange =>
                $"That adjustment would put fighter level outside 1-" +
                $"{PlayerExperienceCatalog.MaximumLevel}.",
            DeveloperProgressionMutationStatus.ArithmeticOverflow =>
                "That addition would exceed the signed 32-bit storage limit.",
            DeveloperProgressionMutationStatus
                .ZodiacStorageLimitExceeded =>
                ZodiacCapacityFeedback(result.Current),
            DeveloperProgressionMutationStatus.RevisionExhausted =>
                "The progression revision is exhausted; no value was changed.",
            DeveloperProgressionMutationStatus.ProviderUnavailable =>
                "Durable progression commands are unavailable.",
            _ => "The progression command was rejected."
        };
        if (command.Operation ==
                DeveloperProgressionOperation.AddZodiacEnergy &&
            result.Current is not null)
        {
            GameSessionRegistry.ApplyDeveloperProgressionProjection(
                _character!,
                result.Current.Value);
            await SendZodiacFullSyncAsync(cancellationToken);
        }

        await SendDeveloperItemFeedbackAsync(
            packet,
            $"[progression] {message}",
            cancellationToken);
    }

    private static string SuccessFeedback(
        DeveloperProgressionCommand command,
        DeveloperProgressionProjection current,
        bool changed) => command.Operation switch
    {
        DeveloperProgressionOperation.SetFighterLevel or
            DeveloperProgressionOperation.AdjustFighterLevel =>
            changed
                ? $"[level] Fighter level is now {current.FighterLevel}; " +
                  "fighter EXP was reset to 0."
                : $"[level] Fighter level is already {current.FighterLevel}.",
        DeveloperProgressionOperation.AddTalentPoints =>
            $"[talent] Talent points are now {current.TalentPoints}.",
        DeveloperProgressionOperation.AddZodiacEnergy =>
            $"[zodiac] Energy is now {current.ZodiacEnergy}." +
            $"{current.ZodiacEnergyRemainderX100:00}.",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static string ZodiacCapacityFeedback(
        DeveloperProgressionProjection? current)
    {
        if (current is null)
        {
            return "That addition exceeds the Zodiac energy storage limit.";
        }

        var value = current.Value;
        return "That addition exceeds the current Zodiac storage limit " +
            $"of {ZodiacEnergyCatalog.GetStorageLimit(value.ZodiacLevel)}; " +
            $"energy remains {value.ZodiacEnergy}." +
            $"{value.ZodiacEnergyRemainderX100:00}.";
    }
}
