using System.Collections.Concurrent;
using System.Net;

namespace Godswar.Server.Networking;

/// <summary>
/// LOGINFIX (2026-11-05): binds the plaintext legacy game login (port 7000)
/// to a password login that just succeeded on the login port (6999).
///
/// The legacy game-login packet carries no password and no ticket, so on its
/// own it would admit anyone who knows a user name. When the login server
/// sends a verified account its game-server redirect, it records
/// (account id, source IP) here for a short time. The game endpoint admits a
/// legacy login only when a matching, unexpired record exists, and uses it up.
///
/// Login and game run in one process by default, so an in-memory table is enough; with
/// GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres the table is shared by every process (ROUND-SHAREDHANDOFF).
/// Set GODSWAR_LEGACY_GAME_LOGIN_HANDOFF=0 to switch the check off (old
/// behaviour) without a rebuild; it is read once at start-up.
/// </summary>
// ROUND-SHAREDHANDOFF (issue #34): where handoffs live. "memory" is this process only (the original behaviour);
// "postgres" (PostgresLegacyGameLoginHandoffStore) is shared by every process of a realm set, which a second realm
// served by another process needs.
internal interface ILegacyGameLoginHandoffStore
{
    string Name { get; }

    Task<bool> IssueAsync(long accountId, int realmId, string address, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<bool> TryConsumeAsync(long accountId, int realmId, string address, CancellationToken cancellationToken);
}

internal static class LegacyGameLoginHandoff
{
    /// <summary>Version of the handoff contract advertised in /admin/capabilities (account + realm + address, one-time).</summary>
    internal const int CapabilityVersion = 1;

    private static ILegacyGameLoginHandoffStore? _shared;

    /// <summary>The realm this process serves as a game worker (set at start-up; advertised with the capability).</summary>
    internal static int ProcessRealmId { get; set; } = 1;

    /// <summary>"memory" or "postgres": what this process issues to and consumes from.</summary>
    internal static string StoreName => _shared?.Name ?? "memory";

    /// <summary>Set once at start-up when GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres.</summary>
    internal static void UseSharedStore(ILegacyGameLoginHandoffStore store) =>
        _shared = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Reads GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE: null/"memory" = in-process, "postgres" = shared; anything else refuses to start.</summary>
    internal static bool ReadWantsSharedStore(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "memory" => false,
            "postgres" => true,
            _ => throw new InvalidOperationException(
                "GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE must be memory or postgres")
        };

    internal static async Task IssueAsync(
        long accountId, int realmId, string? remoteEndPoint, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        var address = AddressOf(remoteEndPoint);
        if (address is null)
        {
            return;
        }

        if (_shared is null)
        {
            Issue(accountId, remoteEndPoint, lifetime: lifetime, realmId: realmId);
            return;
        }

        if (!await _shared.IssueAsync(accountId, realmId, address, lifetime ?? Lifetime, cancellationToken))
        {
            Console.Error.WriteLine("[security] legacy game login handoff refused: shared store is full");
        }
    }

    internal static Task<bool> TryConsumeAsync(
        long accountId, int realmId, string? remoteEndPoint, CancellationToken cancellationToken)
    {
        var address = AddressOf(remoteEndPoint);
        if (address is null)
        {
            return Task.FromResult(false);
        }

        return _shared is null
            ? Task.FromResult(TryConsume(accountId, remoteEndPoint, realmId: realmId))
            : _shared.TryConsumeAsync(accountId, realmId, address, cancellationToken);
    }

    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

    /// <summary>
    /// After an admitted legacy session ends, the client may reconnect and resend the game login without a new
    /// password login (GodsWar.exe 0x5f5b60 "re-login after reconnect"). The same account from the same address
    /// gets this long to do so.
    /// </summary>
    internal static readonly TimeSpan ReconnectGrace = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(long AccountId, int RealmId, string Address), long> Pending = new();

    internal static bool Enabled { get; } = ReadEnabled(
        Environment.GetEnvironmentVariable("GODSWAR_LEGACY_GAME_LOGIN_HANDOFF"));

    internal static bool ReadEnabled(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        !(value.Trim() is "0" ||
          value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) ||
          value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalises "1.2.3.4:5678" / "[::ffff:1.2.3.4]:5678" to "1.2.3.4". Null when not an IP endpoint.</summary>
    internal static string? AddressOf(string? remoteEndPoint)
    {
        if (string.IsNullOrWhiteSpace(remoteEndPoint))
        {
            return null;
        }

        IPAddress? address = null;
        if (IPEndPoint.TryParse(remoteEndPoint, out var endPoint))
        {
            address = endPoint.Address;
        }
        else if (IPAddress.TryParse(remoteEndPoint, out var bare))
        {
            address = bare;
        }

        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    internal static void Issue(long accountId, string? remoteEndPoint, long? nowMs = null, TimeSpan? lifetime = null, int realmId = 1)
    {
        var address = AddressOf(remoteEndPoint);
        if (address is null)
        {
            return;
        }

        var now = nowMs ?? Environment.TickCount64;
        foreach (var entry in Pending)
        {
            if (entry.Value <= now)
            {
                Pending.TryRemove(entry.Key, out _);
            }
        }

        Pending[(accountId, realmId, address)] = now + (long)(lifetime ?? Lifetime).TotalMilliseconds;
    }

    /// <summary>True (and the record is used up) when this account passed a password login from this address in the last <see cref="Lifetime"/>.</summary>
    internal static bool TryConsume(long accountId, string? remoteEndPoint, long? nowMs = null, int realmId = 1)
    {
        var address = AddressOf(remoteEndPoint);
        if (address is null)
        {
            return false;
        }

        var now = nowMs ?? Environment.TickCount64;
        return Pending.TryRemove((accountId, realmId, address), out var expires) && expires > now;
    }

    internal static void ClearForTests() => Pending.Clear();
}
