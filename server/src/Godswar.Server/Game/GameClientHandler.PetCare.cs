using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private CancellationTokenSource? _petCareDecayCancellation;
    private Task? _petCareDecayTask;
    private long _petCareDecayGeneration;
    private long _petCareDecayTicks;

    private IPetCareDecayStore? PetCareDecay =>
        _petDurableCommands as IPetCareDecayStore;

    private void StartPetCareDecay()
    {
        if (PetCareDecay is null ||
            _petCareDecayTask is { IsCompleted: false })
        {
            return;
        }

        CancelPetCareDecay();
        var generation = Interlocked.Increment(
            ref _petCareDecayGeneration);
        _petCareDecayCancellation = new CancellationTokenSource();
        _petCareDecayTask = RunPetCareDecayAsync(
            generation,
            _petCareDecayCancellation.Token);
    }

    /// <summary>
    /// Dedicated care clock. It is deliberately independent of the Owner
    /// Merge drain and recharge loops, which stop whenever their own
    /// condition ends and therefore cannot accumulate summoned time.
    /// </summary>
    private async Task RunPetCareDecayAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            PetCareDecayPolicy.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(
                            ref _petCareDecayGeneration))
                    {
                        return;
                    }
                    if (!TryGetOwnerMergeLifecycleContext(
                            out var subject,
                            out var ownership))
                    {
                        continue;
                    }

                    // ROUND2: /petlock on - this GM's pet care does not decay.
                    if (_character is { } careOwner &&
                        Application.Operators.OperatorControls.HasPetCareLock(
                            careOwner.Id))
                    {
                        continue;
                    }

                    await AdvancePetCareDecayAsync(
                        subject,
                        ownership,
                        cancellationToken);
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[pet] care decay timer failed: {ex.Message}");
            _session.Disconnect();
        }
    }

    /// <summary>
    /// Charges one tick of summoned care time. Only a summoned, unmerged pet
    /// accrues; a recalled or merged pet resets the clock, exactly as the
    /// project rule requires. Nothing is written until ten whole minutes have
    /// accumulated.
    /// </summary>
    private async Task AdvancePetCareDecayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (PetCareDecay is not { } store)
        {
            return;
        }
        if (_characterLoadSnapshot?.Pets.SingleOrDefault(
                static pet => pet.IsSummoned) is not { } summoned ||
            summoned.ContributesToCharacter)
        {
            _petCareDecayTicks = 0;
            return;
        }

        _petCareDecayTicks++;
        var ticksPerInterval = checked((long)Math.Round(
            PetCareDecayPolicy.Interval /
                PetCareDecayPolicy.TickInterval,
            MidpointRounding.AwayFromZero));
        if (_petCareDecayTicks < ticksPerInterval)
        {
            return;
        }
        _petCareDecayTicks = 0;

        var result = await store.DrainSummonedCareAsync(
            subject,
            ownership,
            PetCareDecayPolicy.SatietyPointsPerInterval,
            PetCareDecayPolicy.LifetimePointsPerInterval,
            cancellationToken);
        if (!result.Changed ||
            result.PetId != summoned.PetId)
        {
            return;
        }

        ProjectPetCare(result);
        await SendPetCareStateAsync(
            summoned.PetId,
            result.Satiety,
            result.Amity,
            result.RemainingLifetime,
            "PetCareDecayState",
            cancellationToken);
        Console.WriteLine(
            $"[pet] care decay character={_character?.Name} " +
            $"pet={result.PetId} satiety={result.Satiety} " +
            $"lifetime={result.RemainingLifetime}");
    }

    private async Task SendPetCareStateAsync(
        long petId,
        int satiety,
        int amity,
        int remainingLifetime,
        string reason,
        CancellationToken cancellationToken) =>
        await _session.SendAsync(
            PacketBuilder.PetCareState(
                petId,
                satiety,
                amity,
                remainingLifetime),
            cancellationToken,
            reason);

    private void ProjectPetCare(PetCareDecayResult result)
    {
        if (_characterLoadSnapshot is not { } snapshot)
        {
            return;
        }

        var found = false;
        var pets = snapshot.Pets.Select(pet =>
        {
            if (pet.PetId != result.PetId)
            {
                return pet;
            }

            found = true;
            return pet with
            {
                Satiety = result.Satiety,
                Amity = result.Amity,
                RemainingLifetime = result.RemainingLifetime,
                Revision = result.PetRevision
            };
        }).ToArray();
        if (found)
        {
            _characterLoadSnapshot = snapshot with { Pets = pets };
        }
    }

    private async Task StopPetCareDecayAsync()
    {
        Interlocked.Increment(ref _petCareDecayGeneration);
        var cancellation = _petCareDecayCancellation;
        var task = _petCareDecayTask;
        _petCareDecayCancellation = null;
        _petCareDecayTask = null;
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

    private void CancelPetCareDecay()
    {
        Interlocked.Increment(ref _petCareDecayGeneration);
        var cancellation = _petCareDecayCancellation;
        var task = _petCareDecayTask;
        _petCareDecayCancellation = null;
        _petCareDecayTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _ = DisposeCancelledPetCareDecayTaskAsync(task, cancellation);
    }

    private static async Task DisposeCancelledPetCareDecayTaskAsync(
        Task? task,
        CancellationTokenSource cancellation)
    {
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
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[pet] cancelled care decay timer failed: {ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
