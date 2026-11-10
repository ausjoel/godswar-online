using System.Collections.Concurrent;
using Godswar.Server.Application.Guilds;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task RunMonsterRoamingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonsterMapRuntime.TickInterval);
        using var observation = new SimulationLoopObservation(
            SimulationLoopKind.MonsterWorld,
            MonsterMapRuntime.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var tick = observation.BeginTick();
                await AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
                tick.Complete();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observation.MarkCancelled();
        }
        catch
        {
            observation.MarkFaulted();
            throw;
        }
    }

    public async Task RunPlayerRecoveryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlayerRecoveryPollInterval);
        using var observation = new SimulationLoopObservation(
            SimulationLoopKind.PlayerRecovery,
            PlayerRecoveryPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var tick = observation.BeginTick();
                await AdvancePlayerRecoveryOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
                tick.Complete();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observation.MarkCancelled();
        }
        catch
        {
            observation.MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// How often the altar drain is settled server-side.
    /// </summary>
    /// <remarks>
    /// The drain is expressed per hour, so the timer only has to be fine enough
    /// that an hour is not missed; the settlement charges whole hours from its own
    /// mark, so running this more often than hourly changes nothing but how
    /// timely the stored value is.
    /// </remarks>
    private static readonly TimeSpan GuildAltarSettlementInterval =
        TimeSpan.FromMinutes(5);

    /// <summary>
    /// Drains every online member's altars and refreshes the clients whose altars
    /// moved.
    /// </summary>
    /// <remarks>
    /// The drain has to be applied whether or not the member is online, but only a
    /// member with a live session can be told, so this settles the sessions that
    /// are present and leaves the others to the lazy settlement on their next read.
    /// </remarks>
    public async Task RunGuildAltarSettlementAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GuildAltarSettlementInterval);
        using var observation = new SimulationLoopObservation(
            SimulationLoopKind.PlayerRecovery,
            GuildAltarSettlementInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var tick = observation.BeginTick();
                await SettleGuildAltarsOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                tick.Complete();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observation.MarkCancelled();
        }
        catch
        {
            observation.MarkFaulted();
            throw;
        }
    }

    internal async Task SettleGuildAltarsOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_guildAltarSettlements is not { } altars)
        {
            return;
        }

        var contexts = new List<GameSessionContext>();
        var characterIds = new List<int>();
        foreach (var snapshot in _sessions.Values)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                    !current.WorldReady)
                {
                    continue;
                }

                contexts.Add(current);
                characterIds.Add(current.CharacterId);
            }
        }

        if (characterIds.Count == 0)
        {
            return;
        }

        var settlements = await altars.TrySettleAltarWorshipAsync(
            characterIds,
            now,
            cancellationToken);
        if (settlements.Count == 0)
        {
            return;
        }

        var moved = settlements
            .Select(static settlement => settlement.CharacterId)
            .ToHashSet();
        foreach (var context in contexts)
        {
            if (!moved.Contains(context.CharacterId))
            {
                continue;
            }

            try
            {
                // The drained points change what the altars pay, so the member's
                // cached bonus is recomputed from the balances the settlement left
                // before the new ceilings are sent.
                await RefreshAltarBonusAsync(context, altars, now, cancellationToken);
                await context.Session.SendAsync(
                    BuildRuntimeStatusUpdate(context, now),
                    cancellationToken,
                    "GuildAltarDecayStatus");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }
    }

    private async Task RefreshAltarBonusAsync(
        GameSessionContext context,
        GuildAltarSettlementStore altars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var balances = await altars.TryReadAltarWorshipAsync(
            context.CharacterId,
            now,
            cancellationToken);
        var bonus = GuildAltarWorshipBonusPolicy.Resolve(
            balances,
            altars.AltarContent.ContentOf);
        var character = context.Character;
        // Recorded against the id, not the object: the object is replaced by a fresh
        // hydration on pet owner-Merge, un-merge and login.
        if (!GuildAltarBonusCache.Set(character.Id, bonus))
        {
            return;
        }

        CharacterCalculatedStatsProjectionApplier.Apply(
            character,
            CharacterStats.FromCharacterBase(character),
            CharacterHealthProjectionMode.PreserveAbsolute);
        Console.WriteLine(
            $"[guild] altar bonus recomputed after decay " +
            $"character={character.Name} hp={bonus.MaxHp} mp={bonus.MaxMp} " +
            $"patk={bonus.PhysicalAttack} matk={bonus.MagicAttack}");
    }

    private byte[] BuildRuntimeStatusUpdate(
        GameSessionContext context,
        DateTimeOffset now)
    {
        var aggregate = GetRuntimeStatusAggregate(context.Session, now);
        return PacketBuilder.PlayerStatusUpdate(context.Character, aggregate);
    }

    internal async Task AdvancePlayerRecoveryOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        PruneHostileSkillCooldowns(now);
        await AdvanceElementalPeriodicDamageOnceAsync(
            now,
            cancellationToken);
        await AdvanceStatusPeriodicDamageOnceAsync(now, cancellationToken); // ROUND3: independent authored harmful pulses.
        await AdvanceStatusPeriodicRecoveryOnceAsync(now, cancellationToken);

        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await AdvancePlayerRuntimeEcsOnceAsync(
                now,
                cancellationToken);
            return;
        }

        var recovered = new List<GameSessionContext>();
        foreach (var snapshot in _sessions.Values)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                    !current.WorldReady ||
                    !_nextPlayerRecoveryAt.TryGetValue(
                        current.CharacterId,
                        out var recoveryDeadline) ||
                    now < recoveryDeadline.Read())
                {
                    continue;
                }

                recoveryDeadline.Write(
                    now + PlayerRecoveryInterval);
                var recovery = ApplyAuthoritativeRecoveryPulseLocked(
                    current,
                    now,
                    PlayerRecoveryCatalog.GetTotalHp(current.Character),
                    PlayerRecoveryCatalog.GetTotalMp(current.Character));
                if (recovery.VitalsChanged)
                {
                    recovered.Add(current);
                }
            }
        }

        foreach (var context in recovered)
        {
            var character = context.Character;
            int currentHp;
            int currentMp;
            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
            }

            try
            {
                await context.Session.SendAsync(
                    PacketBuilder.PlayerVitalsUpdate(
                        LocalPlayerObjectId,
                        currentHp,
                        currentMp),
                    cancellationToken,
                    "PlayerPassiveRecoverySelf");
                await BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.PlayerVitalsUpdate(
                        context.ObjectId,
                        currentHp,
                        currentMp),
                    cancellationToken,
                    context.Session,
                    "PlayerPassiveRecoveryWorld");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }

            try
            {
                await PersistRoutineVitalsAsync(
                    context,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[recovery] vitals persistence deferred character={context.DisplayName}: {ex.Message}");
            }

            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
            }

            Console.WriteLine(
                $"[recovery] character={context.DisplayName} hp={currentHp}/{character.MaxHp} mp={currentMp}/{character.MaxMp}");
        }
    }

}
