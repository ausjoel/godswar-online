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
/// Login and game run in one process, so an in-memory table is enough.
/// Set GODSWAR_LEGACY_GAME_LOGIN_HANDOFF=0 to switch the check off (old
/// behaviour) without a rebuild; it is read once at start-up.
/// </summary>
internal static class LegacyGameLoginHandoff
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

    /// <summary>
    /// After an admitted legacy session ends, the client may reconnect and resend the game login without a new
    /// password login (GodsWar.exe 0x5f5b60 "re-login after reconnect"). The same account from the same address
    /// gets this long to do so.
    /// </summary>
    internal static readonly TimeSpan ReconnectGrace = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(long AccountId, string Address), long> Pending = new();

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

    internal static void Issue(long accountId, string? remoteEndPoint, long? nowMs = null, TimeSpan? lifetime = null)
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

        Pending[(accountId, address)] = now + (long)(lifetime ?? Lifetime).TotalMilliseconds;
    }

    /// <summary>True (and the record is used up) when this account passed a password login from this address in the last <see cref="Lifetime"/>.</summary>
    internal static bool TryConsume(long accountId, string? remoteEndPoint, long? nowMs = null)
    {
        var address = AddressOf(remoteEndPoint);
        if (address is null)
        {
            return false;
        }

        var now = nowMs ?? Environment.TickCount64;
        return Pending.TryRemove((accountId, address), out var expires) && expires > now;
    }

    internal static void ClearForTests() => Pending.Clear();
}
