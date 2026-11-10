namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// INSTANCES: writes the Bay Under Attack run ledger off the game thread, in
/// order per run (open, members, close), with retries, and closes a run once.
/// </summary>
/// <remarks>
/// Every write of one run waits for the previous write of that run, so a close
/// can never land before the open it closes. A failed write is retried
/// <see cref="Attempts"/> times before it is only logged.
/// </remarks>
internal sealed class HarborAttackRunLedgerWriter
{
    public const int Attempts = 3;

    private readonly IHarborAttackRunLedger _ledger;
    private readonly Func<int, TimeSpan> _retryDelay;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task> _tails = [];
    private readonly HashSet<Guid> _closed = [];

    public HarborAttackRunLedgerWriter(
        IHarborAttackRunLedger ledger,
        Func<int, TimeSpan>? retryDelay = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _retryDelay = retryDelay ?? (static attempt => TimeSpan.FromSeconds(attempt * 2));
    }

    public IHarborAttackRunLedger Ledger => _ledger;

    public void Open(
        Guid runReservationId,
        short realmId,
        byte mapId,
        DateTimeOffset startedAtUtc,
        IReadOnlyCollection<int> characterIds)
    {
        var characters = characterIds.Distinct().ToArray();
        Enqueue(runReservationId, "open", cancellationToken => _ledger.OpenAsync(
            runReservationId, realmId, mapId, startedAtUtc, characters, cancellationToken));
    }

    public void AddMember(Guid runReservationId, Guid memberReservationId, int characterId) =>
        Enqueue(runReservationId, "member", cancellationToken => _ledger.AddMemberAsync(
            runReservationId, memberReservationId, characterId, cancellationToken));

    /// <summary>Closes the run once; later calls return false and write nothing.</summary>
    public bool Close(Guid runReservationId, string reason, DateTimeOffset closedAtUtc)
    {
        lock (_gate)
        {
            if (!_closed.Add(runReservationId))
            {
                return false;
            }
        }
        Enqueue(runReservationId, "close:" + reason, cancellationToken => _ledger.CloseAsync(
            runReservationId, reason, closedAtUtc, cancellationToken));
        return true;
    }

    public bool IsClosed(Guid runReservationId)
    {
        lock (_gate)
        {
            return _closed.Contains(runReservationId);
        }
    }

    /// <summary>Completes when every write queued so far for this run has finished.</summary>
    public Task WhenIdleAsync(Guid runReservationId)
    {
        lock (_gate)
        {
            return _tails.TryGetValue(runReservationId, out var tail) ? tail : Task.CompletedTask;
        }
    }

    private void Enqueue(Guid runReservationId, string action, Func<CancellationToken, Task> write)
    {
        lock (_gate)
        {
            var previous = _tails.TryGetValue(runReservationId, out var tail)
                ? tail
                : Task.CompletedTask;
            var next = RunAfterAsync(previous, runReservationId, action, write);
            _tails[runReservationId] = next;
            _ = next.ContinueWith(
                finished =>
                {
                    lock (_gate)
                    {
                        if (_tails.TryGetValue(runReservationId, out var current) &&
                            ReferenceEquals(current, finished))
                        {
                            _tails.Remove(runReservationId);
                        }
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task RunAfterAsync(
        Task previous,
        Guid runReservationId,
        string action,
        Func<CancellationToken, Task> write)
    {
        await Task.Yield(); // never run the write under the caller's lock
        await previous; // never throws: every queued write catches its own error
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write(CancellationToken.None);
                return;
            }
            catch (Exception error) when (attempt < Attempts)
            {
                Console.Error.WriteLine(
                    $"[harbor] run ledger {action} failed reservation={runReservationId} " +
                    $"attempt={attempt}/{Attempts}, retrying: {error.Message}");
                await Task.Delay(_retryDelay(attempt));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[harbor] run ledger {action} FAILED reservation={runReservationId} " +
                    $"after {Attempts} attempts: {error.Message}");
                return;
            }
        }
    }
}
