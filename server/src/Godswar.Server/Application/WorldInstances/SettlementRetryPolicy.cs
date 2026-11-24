using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;

namespace Godswar.Server.Application.WorldInstances;

// S5b (5 Nov party run b87f263a): instance reward settlements (Wonderland titles and medals, Atlantis completion)
// used to retry every failure forever. A rejection that can never succeed held the run open and logged until the
// instance was forced out. Failures are now classified: a transient one (I/O, database unavailable) keeps retrying;
// a deterministic one is retried a few times for at least a minute (so a read-after-write race can clear), then the
// entry is parked: logged once with its full evidence, recorded in ParkedSettlements (management GET
// /settlement/parked, which the VPS health check turns into an ntfy alert) and no longer blocks the run's
// retirement. A transient failure that lasts longer than TransientHoldLimit is parked the same way but keeps
// retrying in the background until TransientGiveUp, so a long database outage does not trap players either.

internal enum SettlementFailureKind : byte { Transient = 1, Deterministic = 2 }

internal enum SettlementRetryDecision : byte
{
    /// <summary>Keep the entry pending; it still holds the run.</summary>
    Retry = 1,
    /// <summary>Deterministic: stop retrying, release the run.</summary>
    Park = 2,
    /// <summary>Transient for too long: release the run but keep retrying in the background.</summary>
    ParkAndRetryInBackground = 3,
    /// <summary>A background retry ran out of time; stop retrying.</summary>
    GiveUp = 4
}

internal static class SettlementRetryPolicy
{
    public const int DeterministicAttempts = 3;
    public static readonly TimeSpan DeterministicWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TransientHoldLimit = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BackgroundRetryInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TransientGiveUp = TimeSpan.FromHours(6);
    public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Deterministic: the store's own rejection (receipt status), a ledger mismatch
    /// (<see cref="InvalidDataException"/>, e.g. "admissions did not match the reserved party members exactly"),
    /// bad arguments, and SQL states for bad data (22), integrity violations (23, e.g. a CHECK constraint) and
    /// schema errors (42). Everything else, including connection loss, timeouts, serialization failures and
    /// deadlocks, is transient.
    /// </summary>
    public static SettlementFailureKind Classify(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SettlementRejectedException:
                case InvalidDataException:
                case ArgumentException:
                case InvalidCastException:
                case FormatException:
                    return SettlementFailureKind.Deterministic;
                case IOException or SocketException or TimeoutException:
                    return SettlementFailureKind.Transient;
                case DbException db:
                    if (db.IsTransient) return SettlementFailureKind.Transient;
                    var state = db.SqlState;
                    if (state is { Length: 5 } && (state.StartsWith("22", StringComparison.Ordinal) ||
                        state.StartsWith("23", StringComparison.Ordinal) || state.StartsWith("42", StringComparison.Ordinal)))
                        return SettlementFailureKind.Deterministic;
                    return SettlementFailureKind.Transient;
            }
        }
        return SettlementFailureKind.Transient;
    }
}

/// <summary>A durable store answered with a rejection status (RequestConflict, AdmissionConflict, ...).</summary>
internal sealed class SettlementRejectedException(string status, string message) : InvalidOperationException(message)
{
    public string Status { get; } = status;
}

/// <summary>Per-entry failure bookkeeping for one settlement queue. Thread-safe per key.</summary>
internal sealed class SettlementRetryTracker<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, State> _states = [];

    private sealed class State
    {
        public DateTimeOffset FirstFailureAt;
        public DateTimeOffset LastAttemptAt;
        public DateTimeOffset LastLogAt = DateTimeOffset.MinValue;
        public int Attempts;
        public int DeterministicAttempts;
        public bool InBackground;
    }

    public int AttemptsFor(TKey key) => _states.TryGetValue(key, out var state) ? state.Attempts : 0;
    public bool IsInBackground(TKey key) => _states.TryGetValue(key, out var state) && state.InBackground;

    /// <summary>True when a background (already parked, transient) entry may be tried again.</summary>
    public bool BackgroundRetryDue(TKey key, DateTimeOffset now) =>
        _states.TryGetValue(key, out var state) && state.InBackground &&
        now - state.LastAttemptAt >= SettlementRetryPolicy.BackgroundRetryInterval;

    public SettlementRetryDecision RecordFailure(TKey key, DateTimeOffset now, SettlementFailureKind kind,
        out bool shouldLog)
    {
        var state = _states.GetOrAdd(key, _ => new State { FirstFailureAt = now });
        lock (state)
        {
            state.Attempts++;
            state.LastAttemptAt = now;
            if (kind == SettlementFailureKind.Deterministic) state.DeterministicAttempts++;
            shouldLog = now - state.LastLogAt >= SettlementRetryPolicy.LogInterval;
            if (shouldLog) state.LastLogAt = now;
            var elapsed = now - state.FirstFailureAt;
            if (state.InBackground)
            {
                if (kind == SettlementFailureKind.Deterministic && state.DeterministicAttempts >= 1 ||
                    elapsed >= SettlementRetryPolicy.TransientGiveUp)
                    return SettlementRetryDecision.GiveUp;
                return SettlementRetryDecision.Retry;
            }
            if (kind == SettlementFailureKind.Deterministic &&
                state.DeterministicAttempts >= SettlementRetryPolicy.DeterministicAttempts &&
                elapsed >= SettlementRetryPolicy.DeterministicWindow)
                return SettlementRetryDecision.Park;
            if (elapsed >= SettlementRetryPolicy.TransientHoldLimit)
            {
                state.InBackground = true;
                return SettlementRetryDecision.ParkAndRetryInBackground;
            }
            return SettlementRetryDecision.Retry;
        }
    }

    public void Forget(TKey key) => _states.TryRemove(key, out _);
}

/// <summary>
/// Process-wide record of parked settlements since start. Kept until restart (an instance may retire between two
/// health-check runs), read by management GET /settlement/parked. Holds no secrets: ids and statuses only.
/// </summary>
internal static class ParkedSettlements
{
    private const int Retained = 200;
    private static readonly ConcurrentQueue<ParkedSettlement> Entries = new();
    private static long _total;

    public static long Total => Interlocked.Read(ref _total);
    public static IReadOnlyList<ParkedSettlement> Snapshot() => Entries.ToArray();

    public static void Record(ParkedSettlement entry)
    {
        Interlocked.Increment(ref _total);
        Entries.Enqueue(entry);
        while (Entries.Count > Retained && Entries.TryDequeue(out _)) { }
    }

    public static void MarkRecovered(string kind, string key, DateTimeOffset at)
    {
        foreach (var entry in Entries)
            if (entry.Kind == kind && entry.Key == key && entry.RecoveredAt is null) entry.RecoveredAt = at;
    }

    public static string DescribeJson() => JsonSerializer.Serialize(new
    {
        total = Total,
        entries = Snapshot().Select(entry => new
        {
            kind = entry.Kind,
            key = entry.Key,
            failure = entry.Failure.ToString(),
            parkedAt = entry.ParkedAt,
            attempts = entry.Attempts,
            retryingInBackground = entry.RetryingInBackground,
            recoveredAt = entry.RecoveredAt,
            error = entry.Error,
            evidence = entry.Evidence
        })
    }) + "\n";
}

internal sealed class ParkedSettlement(string kind, string key, SettlementFailureKind failure, DateTimeOffset parkedAt,
    int attempts, bool retryingInBackground, string error, string evidence)
{
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public SettlementFailureKind Failure { get; } = failure;
    public DateTimeOffset ParkedAt { get; } = parkedAt;
    public int Attempts { get; } = attempts;
    public bool RetryingInBackground { get; } = retryingInBackground;
    public string Error { get; } = error;
    public string Evidence { get; } = evidence;
    public DateTimeOffset? RecoveredAt { get; set; }
}
