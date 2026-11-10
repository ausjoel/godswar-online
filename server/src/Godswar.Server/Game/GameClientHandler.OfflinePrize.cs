using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Offline prize (the admin F-131, "per hour away"; see <see cref="TimePrizeCatalog.OfflineAmounts"/>). "Away since" is the
/// character's own last-online time (zodiac_last_online_at = end of its last online interval), captured when the
/// character is first loaded on this connection: the progression settlement moves that column forward while the
/// character plays. The prize is paid once per connection at the first EnterUiReady (the client is in the world then;
/// it ignores 10325 anywhere else), after a claim row in offline_prize_state, so a reconnect or a second session cannot
/// pay the same absence twice (at most once: a failed grant after the claim is lost, not repeated).
/// </summary>
internal sealed partial class GameClientHandler
{
    private DateTimeOffset? _offlinePrizeAwaySince;
    private int _offlinePrizeCharacterId;
    private bool _offlinePrizeChecked;

    /// <summary>
    /// Remembers a character's last-online time the first time it is loaded on this connection, before play moves it.
    /// Later reloads of the same character keep the first value.
    /// </summary>
    private void CaptureOfflinePrizeBaseline(GameCharacter character)
    {
        if (_offlinePrizeCharacterId == character.Id) return;
        _offlinePrizeCharacterId = character.Id;
        _offlinePrizeAwaySince = character.ZodiacLastOnlineAt;
        _offlinePrizeChecked = false;
    }

    private async Task TrySendOfflinePrizeAsync(CancellationToken cancellationToken)
    {
        if (_offlinePrizeChecked || _character is not { } character || _account is null ||
            character.Id != _offlinePrizeCharacterId) return;
        _offlinePrizeChecked = true;
        if (!Finalise2Rates.On(Finalise2RateKeys.OfflinePrize) || _offlinePrizeAwaySince is not { } awaySince ||
            EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // GENERATED: EXP and TP take the event rate exp.events (the F-131 question quoted the live x10 for both),
        // applied before the single floor; Stored Energy and B-Gold are paid as the table says.
        var eventRate = ServerRates.Current.Get(ServerRateKeys.EventExperience);
        var raw = TimePrizeCatalog.OfflineAmounts(character.Level, now - awaySince, eventRate, eventRate);
        if (raw.IsEmpty) return;

        var experience = (int)Math.Min(raw.Experience, MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);
        var talentPoints = (int)raw.TalentPoints;
        var bindingGold = (int)raw.BindingGold;
        // GENERATED: energy above the Zodiac storage limit is cut to what still fits (the storage rule refuses more).
        var energyRoom = Math.Max(0L, (long)ZodiacEnergyCatalog.GetStorageLimit(character.ZodiacLevel) -
            character.ZodiacEnergy - (character.ZodiacEnergyRemainderX100 > 0 ? 1 : 0));
        var energy = (int)Math.Min(raw.Energy, energyRoom);

        object? claimed;
        try
        {
            claimed = await store.ExecuteScalarAsync("""
                INSERT INTO public.offline_prize_state (character_id, paid_for) VALUES (@c, @a)
                ON CONFLICT (character_id) DO UPDATE SET paid_for = @a, updated_at = now()
                WHERE public.offline_prize_state.paid_for < @a
                RETURNING character_id;
                """, cancellationToken, ("c", character.Id), ("a", awaySince.UtcDateTime));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[offline-prize] claim skipped character={character.Name}: {error.GetType().Name}");
            return;
        }

        if (claimed is null)
        {
            Console.WriteLine($"[offline-prize] already paid character={character.Name} away_since={awaySince:O}");
            return;
        }

        if (experience > 0)
        {
            await PayZeusGiftProgressionAsync(experience, 0, "OfflinePrize", cancellationToken, applyEventRate: false);
        }
        if (talentPoints > 0 &&
            !await AddOfflinePrizeProgressionAsync(DeveloperProgressionOperation.AddTalentPoints, talentPoints,
                cancellationToken))
        {
            talentPoints = 0;
        }
        if (energy > 0 &&
            !await AddOfflinePrizeProgressionAsync(DeveloperProgressionOperation.AddZodiacEnergy, energy,
                cancellationToken))
        {
            energy = 0;
        }
        if (bindingGold > 0)
        {
            // Every client LvGain row has BindingGolds="0"; paid in case a later table sets it.
            await RunEventsNpcAsync(async tx => await tx.AddBindingGoldAsync(bindingGold), 0, cancellationToken);
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        Console.WriteLine($"[offline-prize] character={character.Name} level={character.Level} " +
            $"away_since={awaySince:O} hours={(int)Math.Floor((now - awaySince).TotalHours)} exp={experience} " +
            $"tp={talentPoints} bgold={bindingGold} energy={energy} (rate={eventRate} table energy={raw.Energy})");
        if (experience <= 0 && talentPoints <= 0 && bindingGold <= 0 && energy <= 0) return;
        await _session.SendAsync(TimePrizeCatalog.OfflinePrize(experience, talentPoints, bindingGold, energy),
            cancellationToken, "OfflinePrize");
    }

    /// <summary>
    /// Talent Points and Stored Energy go through the durable progression command (the path the GM /talent and
    /// /zodiac commands use), so the settlement's in-memory projection stays right. No chat feedback.
    /// </summary>
    private async Task<bool> AddOfflinePrizeProgressionAsync(DeveloperProgressionOperation operation, int value,
        CancellationToken cancellationToken)
    {
        if (_developerProgressionCommands is null || _account is null || _character is null ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            Console.WriteLine($"[offline-prize] {operation} unavailable");
            return false;
        }

        DeveloperProgressionMutationResult result;
        try
        {
            result = await _registry.ExecuteDeveloperProgressionAsync(_session, _developerProgressionCommands,
                new DeveloperProgressionCommandRequest(new CommandSubject(_account.Id, _character.Id), _processRealmId,
                    ownership, new DeveloperProgressionCommand(operation, value)), _character, cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return false;
        }

        if (!result.IsSuccess || result.Previous is not { } previous || result.Current is not { } current)
        {
            Console.WriteLine($"[offline-prize] {operation} {value} refused: {result.Status}");
            return false;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        if (operation == DeveloperProgressionOperation.AddTalentPoints)
        {
            await _session.SendAsync(PacketBuilder.TalentPointGain(current.TalentPoints - previous.TalentPoints),
                cancellationToken, "OfflinePrizeTalentPointGain");
        }
        else
        {
            var gainedX100 = checked((int)(((long)current.ZodiacEnergy * 100L + current.ZodiacEnergyRemainderX100) -
                ((long)previous.ZodiacEnergy * 100L + previous.ZodiacEnergyRemainderX100)));
            await _session.SendAsync(PacketBuilder.ZodiacEnergyIncrease(current.ZodiacEnergy, gainedX100),
                cancellationToken, "OfflinePrizeZodiacEnergyGain");
            await SendZodiacFullSyncAsync(cancellationToken);
        }
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "OfflinePrizeStatus");
        return true;
    }
}
