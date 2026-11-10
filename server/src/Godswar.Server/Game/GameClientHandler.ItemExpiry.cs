using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

// EXPIRY (b03, Polish patch): the online half of real-time item expiry.
// Login removal runs in RefreshCharacterSnapshotAsync ("login"). After world
// entry this loop, every ItemExpiryRuntime.SweepInterval and under the
// character-state gate:
//   1. removes expired kit-bag items (database first, then the same reload,
//      delete acknowledgement and bag refresh an operator edit uses), and
//   2. sends MSG_TIME_ITEM (10054) for every timed item worn or carried whose
//      slot or expiry changed since the last frame, and an all-zero frame for
//      a slot that no longer holds a timed item, so the client tooltip shows
//      the time left. A moved item therefore shows its new timer within one
//      interval.
// An expired item that is EQUIPPED stays on until the next login, where the
// login purge removes it (the server has no server-initiated unequip).
internal sealed partial class GameClientHandler
{
    private readonly Dictionary<(TimeItemContainer Container, int Page, int Index), DateTime>
        _sentItemTimers = new();
    private CancellationTokenSource? _itemExpiryCancellation;
    private Task? _itemExpiryTask;
    private long _itemExpiryGeneration;

    /// <summary>Called right after world entry; the caller holds the gate.</summary>
    private async Task BeginItemExpiryAfterEnterAsync(CancellationToken cancellationToken)
    {
        if (!ItemExpiryRuntime.Enabled || _character is null)
        {
            return;
        }

        // A new world entry is a fresh client: nothing it holds has a timer yet.
        _sentItemTimers.Clear();
        try
        {
            await SendItemTimersAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[expiry] timer sync failed character={_character?.Name}: {error.Message}");
        }

        StartItemExpiry();
    }

    private void StartItemExpiry()
    {
        if (_itemExpiryTask is { IsCompleted: false })
        {
            return;
        }

        var generation = Interlocked.Increment(ref _itemExpiryGeneration);
        _itemExpiryCancellation?.Dispose();
        _itemExpiryCancellation = new CancellationTokenSource();
        _itemExpiryTask = RunItemExpiryAsync(generation, _itemExpiryCancellation.Token);
    }

    private async Task RunItemExpiryAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ItemExpiryRuntime.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(ref _itemExpiryGeneration))
                    {
                        return;
                    }
                    if (_character is null || !TryCaptureCurrentPlayerOwnership(out _))
                    {
                        continue;
                    }

                    await SweepExpiredKitBagAsync(cancellationToken);
                    await SendItemTimersAsync(cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Never fatal: the next tick (or the next login) retries.
                    Console.WriteLine($"[expiry] online sweep failed character={_character?.Name}: {error.Message}");
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

    private async Task SweepExpiredKitBagAsync(CancellationToken cancellationToken)
    {
        var removedSlots = await ItemExpiryRuntime.PurgeKitBagAsync(_character!.Id, cancellationToken);
        if (removedSlots.Count == 0)
        {
            return;
        }

        var reload = await ReloadAfterOperatorChangeAsync("item_expiry", cancellationToken);
        Console.WriteLine(
            $"[expiry] character={_character?.Name} bag slots {string.Join(",", removedSlots)} expired; {reload}");
    }

    private async Task SendItemTimersAsync(CancellationToken cancellationToken)
    {
        var rows = await ItemExpiryRuntime.ReadTimedAsync(_character!.Id, cancellationToken);
        var offset = _realmCalendar.GetUtcOffset(DateTimeOffset.UtcNow);
        var desired = BuildItemTimerMap(rows, offset);
        foreach (var (address, expiry) in PlanItemTimerFrames(_sentItemTimers, desired))
        {
            await _session.SendAsync(
                PacketBuilder.TimeItem(address.Container, address.Page, address.Index, expiry),
                cancellationToken,
                expiry is null ? "TimeItemClear" : "TimeItem");
        }

        _sentItemTimers.Clear();
        foreach (var (address, expiry) in desired)
        {
            _sentItemTimers[address] = expiry;
        }
    }

    /// <summary>Realm wall-clock expiry per client address (equipment and kit bag).</summary>
    internal static Dictionary<(TimeItemContainer Container, int Page, int Index), DateTime> BuildItemTimerMap(
        IEnumerable<TimedItemSlot> rows,
        TimeSpan realmUtcOffset)
    {
        var map = new Dictionary<(TimeItemContainer, int, int), DateTime>();
        foreach (var row in rows)
        {
            (TimeItemContainer Container, int Page, int Index)? address = row.Location switch
            {
                ItemExpiryRuntime.LocationEquipment when row.Slot is >= 0 and < 24 =>
                    (TimeItemContainer.Equipment, 0, row.Slot),
                ItemExpiryRuntime.LocationKitBag when row.Slot is >= 0 and < 96 =>
                    (TimeItemContainer.KitBag,
                        PacketBuilder.TimeItemKitBagAddress(row.Slot).Page,
                        PacketBuilder.TimeItemKitBagAddress(row.Slot).Index),
                _ => null
            };
            if (address is { } key)
            {
                map[key] = row.ExpiresAtUtc.UtcDateTime.Add(realmUtcOffset);
            }
        }
        return map;
    }

    /// <summary>
    /// Frames to send: every new or changed timer, then a clear (null) for each
    /// address that had a timer and no longer has one.
    /// </summary>
    internal static List<((TimeItemContainer Container, int Page, int Index) Address, DateTime? Expiry)> PlanItemTimerFrames(
        IReadOnlyDictionary<(TimeItemContainer Container, int Page, int Index), DateTime> sent,
        IReadOnlyDictionary<(TimeItemContainer Container, int Page, int Index), DateTime> desired)
    {
        var frames = new List<((TimeItemContainer, int, int), DateTime?)>();
        foreach (var (address, expiry) in desired.OrderBy(static e => e.Key))
        {
            if (!sent.TryGetValue(address, out var previous) || previous != expiry)
            {
                frames.Add((address, expiry));
            }
        }
        foreach (var address in sent.Keys.Where(a => !desired.ContainsKey(a)).OrderBy(static a => a))
        {
            frames.Add((address, null));
        }
        return frames;
    }

    private async Task StopItemExpiryAsync()
    {
        Interlocked.Increment(ref _itemExpiryGeneration);
        var cancellation = _itemExpiryCancellation;
        var task = _itemExpiryTask;
        _itemExpiryCancellation = null;
        _itemExpiryTask = null;
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
