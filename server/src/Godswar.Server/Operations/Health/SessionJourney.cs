using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Godswar.Server.Operations.Health;

// W6A (the admin 2026-11-13, "login health checking for every player"): what one connection did, from accept to close.
// One object per connection, filled by the login handler (port 6999) or the game handler (port 7000) at the places
// that already write a log line, and handed to SessionHealthRecorder once, when the connection ends.
// Holds NO packet payloads, passwords, MAC strings or full addresses: the address is cut to its network
// (a.b.c.0 / first 48 bits of IPv6) before it is kept.
internal sealed class SessionJourney
{
    internal const int MaximumStages = 16;
    internal const int MaximumErrorTextLength = 300;

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<KeyValuePair<string, long>> _stages = new(8);
    private readonly object _gate = new();

    public SessionJourney(string endpoint, int realmId, string? remoteEndPoint)
    {
        Endpoint = endpoint;
        RealmId = realmId;
        StartedAt = DateTimeOffset.UtcNow;
        Network = TruncateAddress(remoteEndPoint);
    }

    public string Endpoint { get; }
    public int RealmId { get; }
    public DateTimeOffset StartedAt { get; }
    public string? Network { get; }

    /// <summary>True once a login packet arrived. Connections that never send one (port scans) are not stored.</summary>
    public bool Attempted { get; set; }
    public int? AccountId { get; set; }
    public int? CharacterId { get; set; }

    /// <summary>The name typed at a refused login. Never stored: the writer swaps it for the account id, or drops it.</summary>
    public string? AttemptedName { get; set; }
    public long? ClientBuild { get; set; }
    public string Outcome { get; set; } = "open";
    public string? Reason { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorText { get; private set; }
    public string? TransportError { get; set; }

    /// <summary>What world entry had to hide from the client so the login could go on (bad item or pet rows).</summary>
    public string? Quarantined { get; set; }
    public int LastOpcode { get; set; }
    public int LastSendOpcode { get; set; }
    public long DurationMilliseconds { get; private set; }

    public long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    /// <summary>Records the first time a stage is reached, as milliseconds since the connection opened.</summary>
    public void Mark(string stage)
    {
        var at = ElapsedMilliseconds;
        lock (_gate)
        {
            if (_stages.Count >= MaximumStages)
            {
                return;
            }
            foreach (var existing in _stages)
            {
                if (existing.Key == stage)
                {
                    return;
                }
            }
            _stages.Add(new(stage, at));
        }
    }

    public bool Reached(string stage)
    {
        lock (_gate)
        {
            foreach (var existing in _stages)
            {
                if (existing.Key == stage)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void Refuse(string reason)
    {
        Outcome = "refused";
        Reason ??= reason;
    }

    public void SetError(Exception error)
    {
        ErrorType = error.GetType().Name;
        ErrorText = Clip(error.Message);
    }

    public void SetFault(ushort opcode, Exception error)
    {
        SetError(error);
        Reason = "opcode " + opcode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Close() => DurationMilliseconds = ElapsedMilliseconds;

    /// <summary>The stages as a small JSON object, e.g. {"login":3,"server_list":40}. Keys are code constants.</summary>
    public string StagesJson()
    {
        var text = new StringBuilder("{");
        lock (_gate)
        {
            for (var index = 0; index < _stages.Count; index++)
            {
                if (index > 0)
                {
                    text.Append(',');
                }
                text.Append('"').Append(_stages[index].Key).Append("\":")
                    .Append(_stages[index].Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return text.Append('}').ToString();
    }

    internal static string? Clip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var clean = new StringBuilder(Math.Min(text.Length, MaximumErrorTextLength));
        foreach (var character in text)
        {
            if (clean.Length >= MaximumErrorTextLength)
            {
                break;
            }
            clean.Append(char.IsControl(character) ? ' ' : character);
        }
        return clean.ToString();
    }

    /// <summary>
    /// "203.0.113.57:50112" -> "203.0.113.0"; IPv6 keeps its first 48 bits. Enough to see "same household /
    /// same provider", not enough to single out a device. Null for fixtures and unknown endpoints.
    /// </summary>
    internal static string? TruncateAddress(string? remoteEndPoint)
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
        else if (!IPAddress.TryParse(remoteEndPoint, out address))
        {
            return null;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return new IPAddress(bytes).ToString();
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Array.Clear(bytes, 6, 10);
            return new IPAddress(bytes).ToString();
        }
        return null;
    }
}
