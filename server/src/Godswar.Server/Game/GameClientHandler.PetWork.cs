using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// PETWORK (F-137, the admin "Build it from Pet_Job.xml"): the pet window's Work button (CPetWorkUI, PetWork.xml).
/// Read from GodsWar.exe (client 1.4.4):
/// - Confirm (0x5c8df0 -> dialog 0x26 -> 0x5c9200) first runs the client's own checks 0x6a0850 (pet state 3/4 =
///   PetCodeReturn75, not summoned = 76, no Work talent bit 4 = 82, not enough Silver/Gold = 77), then sends
///   C2S 10265: u8 pay type (1 Silver, 2 Gold) at +4, u8 hours (1/3/8/24) at +5. No pet id: it is the summoned pet.
/// - The server answers 10249 code 78 (0x6a0a10: the client removes the companion, sets its pet state to 4 =
///   working and takes the cost off its own Silver/Gold display), or a refusal code.
/// - While the pet works, opening the window sends C2S 10290 (header only); the answer is S2C 10291.
/// - "Stop Work" (dialog 0x28 -> 0x6a1ea0) sends C2S 10293 (header only); the answer is S2C 10294 (20% reward;
///   its handler puts the pet back to carried).
/// - At the end the server sends S2C 10266 (reward) and 10249 code 79 (pet back to carried).
/// The pet is recalled through the durable presence path when it starts, so its world model, summon passives
/// and care clock stop like a normal recall. Prices and rewards: Application/Pets/PetWorkPolicy.cs.
/// Stored in public.pet_work_state (runtime DDL); the reward is fixed when the work starts.
/// </summary>
internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan PetWorkTick = TimeSpan.FromSeconds(30);
    private PetWorkJob? _petWork;
    private CancellationTokenSource? _petWorkCancellation;
    private Task? _petWorkTask;
    private long _petWorkGeneration;

    private sealed record PetWorkJob(
        long PetId,
        byte Pay,
        byte Hours,
        int Cost,
        long Experience,
        int TalentPoints,
        DateTimeOffset EndsAt);

    /// <summary>Login: before the owned-pet list is built, so a working pet goes out with record byte 4.</summary>
    private async Task LoadPetWorkAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var row = await RunEventsNpcAsync(async tx => await tx.ScalarAsync("""
            SELECT concat_ws(',', pet_id, pay_type, hours, cost, experience, talent_points,
                             extract(epoch FROM ends_at)::bigint)
            FROM public.pet_work_state WHERE character_id = @c;
            """), (object?)null, cancellationToken);
        _petWork = row is string text ? ParsePetWorkJob(text) : null;
        if (_petWork is { } job)
        {
            PetWorkRegistry.Mark(job.PetId);
            Console.WriteLine(
                $"[petwork] loaded character={_character.Name} pet={job.PetId} ends={job.EndsAt:O}");
        }
    }

    private static PetWorkJob? ParsePetWorkJob(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 7)
        {
            return null;
        }

        return new PetWorkJob(long.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]),
            int.Parse(parts[3]), long.Parse(parts[4]), int.Parse(parts[5]),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[6])));
    }

    /// <summary>After EnterUiReady: pays work that ended while the player was away, then watches the rest.</summary>
    private async Task ResumePetWorkAfterEnterAsync(CancellationToken cancellationToken)
    {
        if (_petWork is null)
        {
            return;
        }

        if (_petWork.EndsAt <= DateTimeOffset.UtcNow)
        {
            await FinishPetWorkAsync(early: false, cancellationToken);
            return;
        }

        StartPetWorkTimer();
    }

    private async Task HandlePetWorkStartAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is not { } character || packet.Length != 6 || packet.Payload.Length < 2)
        {
            Console.WriteLine($"[petwork] rejected malformed start length={packet.Length}");
            return;
        }

        var pay = packet.Payload[0];
        var hours = packet.Payload[1];
        if (!Finalise2Rates.On(Finalise2RateKeys.PetWork))
        {
            Console.WriteLine($"[petwork] start ignored character={character.Name}: switch {Finalise2RateKeys.PetWork} is 0");
            return;
        }

        if (_petWork is not null)
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.AlreadyAway, "PetWorkBusy", cancellationToken);
            return;
        }

        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(static candidate => candidate.IsCarried);
        if (pet is not { IsSummoned: true, ContributesToCharacter: false })
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.SummonFirst, "PetWorkNotSummoned", cancellationToken);
            return;
        }

        if ((pet.TalentMask & 4) == 0) // PetTalentCatalog: Work = bit 4
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.NoWorkTalent, "PetWorkNoTalent", cancellationToken);
            return;
        }

        if (!PetWorkTable.TryQuote(character.Level, pay, hours, out var quote))
        {
            Console.WriteLine($"[petwork] rejected start character={character.Name} pay={pay} hours={hours}");
            return;
        }

        // The wallet is checked again inside the transaction; this only avoids recalling the pet for nothing.
        if ((quote.Pay == PetWorkPayType.Silver ? character.Silver : character.Gold) < quote.Cost)
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.NotEnoughMoney, "PetWorkNoMoney", cancellationToken);
            return;
        }

        // Recall first, through the normal durable path (world model, summon passives, care clock). Its own
        // recall result reaches the client before code 78, which then sets the client's pet state to working.
        var receipt = await HandleDurablePetPresenceAsync(
            PetCommandOperationIdentity.ServerSessionLifecycle(Guid.NewGuid(), _commandConnectionId),
            pet.PetId,
            PetPresenceOperation.Recall,
            cancellationToken);
        if (receipt is not { Succeeded: true })
        {
            Console.WriteLine($"[petwork] recall failed character={character.Name} pet={pet.PetId} status={receipt?.Status}");
            await SendPetWorkCodeAsync(PetWorkResultCode.SummonFirst, "PetWorkRecallFailed", cancellationToken);
            return;
        }

        await SendPetSkillOwnerStatRefreshAsync("PetWorkRecall", cancellationToken);
        var endsAt = DateTimeOffset.UtcNow + quote.Duration;
        var result = await RunEventsNpcAsync<PetWorkResultCode?>(async tx =>
        {
            var left = quote.Pay == PetWorkPayType.Silver
                ? await tx.TrySpendSilverAsync(quote.Cost)
                : await tx.TrySpendGoldAsync(quote.Cost);
            if (left is null)
            {
                return PetWorkResultCode.NotEnoughMoney;
            }

            var inserted = await tx.ScalarAsync("""
                INSERT INTO public.pet_work_state
                    (character_id, pet_id, pay_type, hours, cost, experience, talent_points, ends_at)
                VALUES (@c, @p, @y, @h, @k, @e, @t, @z)
                ON CONFLICT (character_id) DO NOTHING
                RETURNING pet_id;
                """, ("p", pet.PetId), ("y", (short)pay), ("h", (short)hours), ("k", quote.Cost),
                ("e", quote.Experience), ("t", quote.TalentPoints), ("z", endsAt));
            if (inserted is null)
            {
                tx.Rollback();
                return PetWorkResultCode.AlreadyAway;
            }

            return PetWorkResultCode.Started;
        }, (PetWorkResultCode?)null, cancellationToken);

        Console.WriteLine(
            $"[petwork] start character={character.Name} pet={pet.PetId} level={character.Level} pay={quote.Pay} " +
            $"hours={hours} cost={quote.Cost} exp={quote.Experience} talent={quote.TalentPoints} result={result?.ToString() ?? "store unavailable"}");
        if (result is null)
        {
            return;
        }

        if (result != PetWorkResultCode.Started)
        {
            await SendPetWorkCodeAsync(result.Value, "PetWorkRefused", cancellationToken);
            return;
        }

        _petWork = new PetWorkJob(pet.PetId, pay, hours, quote.Cost, quote.Experience, quote.TalentPoints, endsAt);
        PetWorkRegistry.Mark(pet.PetId);
        // 78 first: the client takes the cost off its own display, then the absolute wallet refresh settles it.
        await SendPetWorkCodeAsync(PetWorkResultCode.Started, "PetWorkStarted", cancellationToken);
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        StartPetWorkTimer();
    }

    private async Task HandlePetWorkStatusRequestAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (_petWork is not { } job)
        {
            // The client thinks a pet works but nothing is stored: bring its pet back (GENERATED).
            Console.WriteLine($"[petwork] status asked with no work character={_character.Name}");
            await SendPetWorkCodeAsync(PetWorkResultCode.Finished, "PetWorkStale", cancellationToken);
            return;
        }

        var left = job.EndsAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            await FinishPetWorkAsync(early: false, cancellationToken);
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PetWorkStatus(job.Experience, job.TalentPoints, (int)Math.Ceiling(left.TotalMinutes),
                job.Cost, job.Pay, job.Hours),
            cancellationToken,
            "PetWorkStatus");
    }

    private async Task HandlePetWorkStopAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (_petWork is not { } job)
        {
            Console.WriteLine($"[petwork] stop asked with no work character={_character.Name}");
            await SendPetWorkCodeAsync(PetWorkResultCode.Finished, "PetWorkStale", cancellationToken);
            return;
        }

        await FinishPetWorkAsync(early: job.EndsAt > DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>
    /// Ends the stored work once (the DELETE decides), credits the Talent Points in the same transaction, then
    /// tells the client (10266 / 10294 add locally) and pays the EXP through the shared progression path, as is
    /// (not multiplied by exp.events, so it matches the window's "Obtained Exp").
    /// </summary>
    private async Task FinishPetWorkAsync(bool early, CancellationToken cancellationToken)
    {
        if (_character is not { } character || _petWork is not { } job)
        {
            return;
        }

        var (experience, talent) = early
            ? PetWorkTable.StopShare(job.Experience, job.TalentPoints)
            : (job.Experience, job.TalentPoints);
        var ended = await RunEventsNpcAsync<bool?>(async tx =>
        {
            var deleted = await tx.ScalarAsync(
                "DELETE FROM public.pet_work_state WHERE character_id = @c RETURNING pet_id;");
            if (deleted is null)
            {
                return false;
            }

            if (talent > 0)
            {
                await tx.AddTalentPointsAsync(talent);
            }

            return true;
        }, (bool?)null, cancellationToken);
        if (ended is null)
        {
            return; // store unavailable: try again on the next tick or login
        }

        _petWork = null;
        PetWorkRegistry.Clear(job.PetId);
        // The login restore skipped these for the working pet; both only start when not already running.
        StartPetOwnerMergeEnergyRecharge();
        StartPetCareDecay();
        Console.WriteLine(
            $"[petwork] {(early ? "stopped" : "finished")} character={character.Name} pet={job.PetId} " +
            $"exp={experience} talent={talent} paid={ended}");
        if (ended != true)
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.Finished, "PetWorkGone", cancellationToken);
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PetWorkReward(early ? Opcodes.PetWorkStopped : Opcodes.PetWorkFinished, experience, talent),
            cancellationToken,
            early ? "PetWorkStopped" : "PetWorkFinished");
        if (experience > 0)
        {
            await PayEventsNpcExperienceAsync(experience, "PetWork", cancellationToken, eventRate: false);
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken);
        if (!early)
        {
            await SendPetWorkCodeAsync(PetWorkResultCode.Finished, "PetWorkReturned", cancellationToken);
        }
    }

    /// <summary>Refuses a presence change that would touch or displace a working pet (GENERATED).</summary>
    private Task<bool> RefusePetPresenceWhileWorkingAsync(
        long petId,
        PetPresenceOperation operation,
        CancellationToken cancellationToken) =>
        RefusePetActionWhileWorkingAsync(
            operation is PetPresenceOperation.Take or PetPresenceOperation.CallOut ? null : petId,
            $"presence_{operation}",
            cancellationToken);

    /// <summary>
    /// True (and PetCodeReturn75 sent) when a pet is working and the action concerns it: <paramref name="petId"/>
    /// null means any action while a pet works.
    /// </summary>
    private async Task<bool> RefusePetActionWhileWorkingAsync(
        long? petId,
        string action,
        CancellationToken cancellationToken)
    {
        if (_petWork is not { } job || petId is { } id && id != job.PetId)
        {
            return false;
        }

        Console.WriteLine($"[petwork] refused {action} character={_character?.Name} working pet={job.PetId}");
        await SendPetWorkCodeAsync(PetWorkResultCode.AlreadyAway, "PetWorkLocked", cancellationToken);
        return true;
    }

    private Task SendPetWorkCodeAsync(PetWorkResultCode code, string reason, CancellationToken cancellationToken) =>
        _session.SendAsync(PacketBuilder.PetWorkCodeReturn(code), cancellationToken, reason);

    private void StartPetWorkTimer()
    {
        if (_petWorkTask is { IsCompleted: false })
        {
            return;
        }

        var generation = Interlocked.Increment(ref _petWorkGeneration);
        _petWorkCancellation?.Dispose();
        _petWorkCancellation = new CancellationTokenSource();
        _petWorkTask = RunPetWorkTimerAsync(generation, _petWorkCancellation.Token);
    }

    private async Task RunPetWorkTimerAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PetWorkTick);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(ref _petWorkGeneration))
                    {
                        return;
                    }

                    // Idle while nothing works; the same loop serves the next job of this session.
                    if (_petWork is null || _character is null || !TryCaptureCurrentPlayerOwnership(out _) ||
                        _petWork.EndsAt > DateTimeOffset.UtcNow)
                    {
                        continue;
                    }

                    await FinishPetWorkAsync(early: false, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Never fatal: the next tick (or the next login) retries.
                    Console.WriteLine($"[petwork] timer failed character={_character?.Name}: {error.Message}");
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopPetWorkTimerAsync()
    {
        Interlocked.Increment(ref _petWorkGeneration);
        var cancellation = _petWorkCancellation;
        var task = _petWorkTask;
        _petWorkCancellation = null;
        _petWorkTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
