using System.Text.RegularExpressions;

namespace Godswar.QuestProbe;

/// <summary>
/// One direction's captured byte stream: the ciphertext the wire carried and the
/// plaintext the proxy decoded, in the order the proxy read them.
/// </summary>
internal sealed class CapturedStream(string connection, string direction)
{
    public string Connection { get; } = connection;

    public string Direction { get; } = direction;

    public List<byte> Raw { get; } = [];

    public List<byte> Clear { get; } = [];

    public List<byte[]> ClearChunks { get; } = [];
}

/// <summary>
/// Reads a capture-proxy text log back into per-connection, per-direction byte
/// streams, and slices those streams into frames.
/// </summary>
/// <remarks>
/// The log writes one <c>CLEAR</c>/<c>RAW</c> pair per socket read, so a stream's
/// chunks concatenate into exactly what the cipher saw - which is what makes it
/// useful twice: to verify this tool's cipher against the client's own bytes
/// before any packet is sent, and to replay a captured login.
/// </remarks>
internal static class CaptureLog
{
    private static readonly Regex HeaderPattern = new(
        @"^(?<stamp>\S+)\s+(?<connection>LOGIN|GAME)\s+(?<what>connected|closed|C->S|S->C)\b(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ClientPattern = new(
        @"client=(?<endpoint>\S+)",
        RegexOptions.Compiled);

    public static IReadOnlyList<CapturedStream> ReadStreams(string path)
    {
        var streams = new List<CapturedStream>();
        var open = new Dictionary<string, CapturedStream>(StringComparer.Ordinal);
        // The C->S/S->C lines do not repeat the peer endpoint, so a connection is
        // identified by the order of its own "connected" line: two logins in one
        // log - a relog is common - are two streams, and merging them would restart
        // the cipher halfway and fail the self-test on a byte offset that is exactly
        // the first connection's length.
        var connectionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        CapturedStream? pendingStream = null;

        foreach (var line in File.ReadLines(path))
        {
            var header = HeaderPattern.Match(line);
            if (header.Success)
            {
                var connection = header.Groups["connection"].Value;
                var what = header.Groups["what"].Value;
                switch (what)
                {
                    case "connected":
                        connectionIndex[connection] =
                            connectionIndex.GetValueOrDefault(connection) + 1;
                        current[connection] = connectionIndex[connection];
                        pendingStream = null;
                        continue;
                    case "closed":
                        current.Remove(connection);
                        pendingStream = null;
                        continue;
                    case "C->S":
                    case "S->C":
                        var key =
                            $"{connection}|{current.GetValueOrDefault(connection)}|{what}";
                        if (!open.TryGetValue(key, out var stream))
                        {
                            stream = new CapturedStream(connection, what);
                            open[key] = stream;
                            streams.Add(stream);
                        }

                        pendingStream = stream;
                        continue;
                    default:
                        pendingStream = null;
                        continue;
                }
            }

            if (pendingStream is null)
            {
                continue;
            }

            if (line.StartsWith("CLEAR ", StringComparison.Ordinal))
            {
                pendingStream.Clear.AddRange(Convert.FromHexString(line[6..].Trim()));
                continue;
            }

            if (line.StartsWith("RAW ", StringComparison.Ordinal))
            {
                var raw = Convert.FromHexString(line[4..].Trim());
                pendingStream.Raw.AddRange(raw);
                var clearLength = pendingStream.Clear.Count -
                    pendingStream.ClearChunks.Sum(static chunk => chunk.Length);
                if (clearLength == raw.Length && clearLength > 0)
                {
                    pendingStream.ClearChunks.Add(
                        pendingStream.Clear
                            .Skip(pendingStream.Clear.Count - clearLength)
                            .ToArray());
                }
            }
        }

        return streams;
    }

    /// <summary>
    /// Cuts a clear stream into frames by the length field each frame carries,
    /// which is what turns a captured login back into sent frames.
    /// </summary>
    public static List<byte[]> Frames(IReadOnlyList<byte> clear)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset + 4 <= clear.Count)
        {
            var length = clear[offset] | (clear[offset + 1] << 8);
            if (length < 4 || offset + length > clear.Count)
            {
                break;
            }

            frames.Add(clear.Skip(offset).Take(length).ToArray());
            offset += length;
        }

        return frames;
    }
}
