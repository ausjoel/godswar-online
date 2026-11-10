using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// A live connection a probe can inject into: the client's own authenticated
/// stream to the server, plus the server frames it has seen.
/// </summary>
internal sealed class ProbeEndpoint(
    string name,
    Guid connectionId,
    Func<byte[][], CancellationToken, Task<int>> injectAsync,
    Func<byte[][], CancellationToken, Task<int>>? injectToClientAsync = null)
{
    private readonly List<byte[]> _serverFrames = [];
    private readonly Lock _gate = new();
    private long _sequence;

    public string Name { get; } = name;

    public Guid ConnectionId { get; } = connectionId;

    /// <summary>Sequence number of the newest frame seen, for "show me what is new".</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public async Task<int> InjectAsync(
        byte[][] clearFrames,
        CancellationToken cancellationToken) =>
        await injectAsync(clearFrames, cancellationToken);

    /// <summary>
    /// Sends frames to the client instead of the server, which is how a teleport
    /// (S2C 10018) or any other server-authored frame can be replayed.
    /// </summary>
    public async Task<int> InjectToClientAsync(
        byte[][] clearFrames,
        CancellationToken cancellationToken) =>
        injectToClientAsync is null
            ? throw new InvalidOperationException("这个连接没有客户端方向")
            : await injectToClientAsync(clearFrames, cancellationToken);

    public void Observe(byte[] clearFrame)
    {
        lock (_gate)
        {
            _serverFrames.Add(clearFrame);
            _sequence++;
            if (_serverFrames.Count > 512)
            {
                _serverFrames.RemoveRange(0, _serverFrames.Count - 512);
            }
        }
    }

    /// <summary>Frames seen after a sequence number, oldest first.</summary>
    public IReadOnlyList<byte[]> Since(long sequence)
    {
        lock (_gate)
        {
            var skip = (int)Math.Max(0, sequence - (_sequence - _serverFrames.Count));
            return _serverFrames.Skip(skip).Select(static frame => frame).ToArray();
        }
    }
}

/// <summary>
/// The proxy's control channel: a probe posts frames here and they are sent to
/// the server over the client's own logged-in connection.
/// </summary>
/// <remarks>
/// Injecting at the proxy rather than inside the client process keeps the client
/// untouched - no DLL, no hook, nothing for the game to notice - and it reuses
/// the one thing the proxy already has: an authenticated stream whose framing and
/// cipher it already speaks.
/// <para>
/// The cipher is a rolling XOR whose pointer advances one step per byte and wraps
/// every 256, so extra bytes break the two sides apart unless the injected block
/// is a whole number of 256-byte periods. Every injected block is therefore
/// padded with harmless frames up to the next multiple of 256; after that the
/// pointer is congruent to where it was, and the client's own next frame still
/// decrypts on the server.
/// </para>
/// </remarks>
internal static class ProbeChannel
{
    private const int CipherPeriod = 256;

    /// <summary>The client's own heartbeat: eight bytes, echoed by the server.</summary>
    private static readonly byte[] Heartbeat = Convert.FromHexString("08001F275B168100");

    /// <summary>A twelve-byte world request, used to fix the residue modulo eight.</summary>
    private static readonly byte[] TwelveByteFiller =
        Convert.FromHexString("0C00D8270000000000000000");

    private static readonly List<ProbeEndpoint> Endpoints = [];
    private static readonly Lock Gate = new();

    public static void Register(ProbeEndpoint endpoint)
    {
        lock (Gate)
        {
            Endpoints.Add(endpoint);
        }
    }

    public static void Unregister(ProbeEndpoint endpoint)
    {
        lock (Gate)
        {
            Endpoints.Remove(endpoint);
        }
    }

    /// <summary>The connection to inject into: the newest one with this name.</summary>
    public static ProbeEndpoint? Find(string name)
    {
        lock (Gate)
        {
            return Endpoints.LastOrDefault(endpoint =>
                string.Equals(endpoint.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static async Task RunAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"探针通道：   127.0.0.1:{port}（INJECT / SEEN / STATUS）");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(
                    () => ServeAsync(client, cancellationToken),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// One command per line, one reply per line: <c>STATUS</c>, <c>SEEN &lt;seq&gt;</c>,
    /// <c>INJECT &lt;name&gt; &lt;hex&gt;</c> and <c>INJECT-RAW</c> (no padding, for
    /// measurements).
    /// </summary>
    private static async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        client.NoDelay = true;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                switch (parts[0].ToUpperInvariant())
                {
                    case "STATUS":
                        string status;
                        lock (Gate)
                        {
                            status =
                                $"OK {Endpoints.Count} " +
                                string.Join(
                                    ",",
                                    Endpoints.Select(endpoint =>
                                        $"{endpoint.Name}:{endpoint.Sequence}"));
                        }

                        await writer.WriteLineAsync(status);
                        break;
                    case "SEEN":
                        await SeenAsync(writer, parts);
                        break;
                    case "INJECT":
                        await InjectAsync(writer, parts, pad: true, cancellationToken);
                        break;
                    case "INJECT-RAW":
                        await InjectAsync(writer, parts, pad: false, cancellationToken);
                        break;
                    case "INJECT-CLIENT":
                        await InjectAsync(
                            writer,
                            parts,
                            pad: true,
                            cancellationToken,
                            toClient: true);
                        break;
                    case "INJECT-CLIENT-RAW":
                        await InjectAsync(
                            writer,
                            parts,
                            pad: false,
                            cancellationToken,
                            toClient: true);
                        break;
                    default:
                        await writer.WriteLineAsync($"ERR unknown command {parts[0]}");
                        break;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or OperationCanceledException or SocketException)
        {
        }
    }

    private static async Task SeenAsync(StreamWriter writer, string[] parts)
    {
        var name = parts.Length > 1 ? parts[1] : "GAME";
        var sequence = parts.Length > 2 && long.TryParse(parts[2], out var parsed)
            ? parsed
            : 0;
        var endpoint = Find(name);
        if (endpoint is null)
        {
            await writer.WriteLineAsync("ERR no such connection");
            return;
        }

        var frames = endpoint.Since(sequence);
        await writer.WriteLineAsync($"OK {endpoint.Sequence} {frames.Count}");
        foreach (var frame in frames)
        {
            await writer.WriteLineAsync($"F {Convert.ToHexString(frame)}");
        }
    }

    private static async Task InjectAsync(
        StreamWriter writer,
        string[] parts,
        bool pad,
        CancellationToken cancellationToken,
        bool toClient = false)
    {
        if (parts.Length < 3)
        {
            await writer.WriteLineAsync("ERR usage: INJECT <name> <hex>");
            return;
        }

        var endpoint = Find(parts[1]);
        if (endpoint is null)
        {
            await writer.WriteLineAsync("ERR no such connection");
            return;
        }

        byte[][] frames;
        try
        {
            frames = parts[2]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(Convert.FromHexString)
                .ToArray();
        }
        catch (FormatException)
        {
            await writer.WriteLineAsync("ERR bad hex");
            return;
        }

        var block = pad ? Pad(frames) : frames;
        var sent = toClient
            ? await endpoint.InjectToClientAsync(block, cancellationToken)
            : await endpoint.InjectAsync(block, cancellationToken);
        await writer.WriteLineAsync($"OK {sent} {block.Length}");
    }

    /// <summary>
    /// Appends harmless frames until the block is a whole number of cipher
    /// periods, which is what keeps the client's own stream aligned on the server.
    /// </summary>
    internal static byte[][] Pad(byte[][] frames)
    {
        var total = frames.Sum(static frame => frame.Length);
        var target = ((total + CipherPeriod - 1) / CipherPeriod) * CipherPeriod;
        var padded = frames.ToList();
        var residue = target - total;
        if (residue < 0)
        {
            return [.. padded];
        }

        // Twelve-byte fillers fix the residue modulo eight, heartbeats cover the
        // rest: gcd(8, 12) = 4, and every quest frame is a multiple of four.
        if (residue % 8 != 0)
        {
            padded.Add(TwelveByteFiller);
            residue -= TwelveByteFiller.Length;
        }

        while (residue > 0)
        {
            padded.Add(Heartbeat);
            residue -= Heartbeat.Length;
        }

        return [.. padded];
    }
}
