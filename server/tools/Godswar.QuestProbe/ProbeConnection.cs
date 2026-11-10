using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Godswar.QuestProbe;

/// <summary>
/// A live connection that speaks the wire format: one cipher per direction and a
/// frame queue fed by a receive loop.
/// </summary>
internal sealed class ProbeConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly PacketCipher _sendCipher = new();
    private readonly PacketCipher _receiveCipher = new();
    private readonly Channel<byte[]> _frames =
        Channel.CreateUnbounded<byte[]>();
    private readonly List<byte[]> _received = [];
    private readonly CancellationTokenSource _cts = new();

    private ProbeConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public IReadOnlyList<byte[]> Received => _received;

    public static async Task<ProbeConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        var connection = new ProbeConnection(client);
        _ = connection.ReceiveLoopAsync(connection._cts.Token);
        return connection;
    }

    public async Task SendAsync(byte[] clearFrame, CancellationToken cancellationToken)
    {
        var raw = clearFrame.ToArray();
        _sendCipher.Transform(raw);
        await _stream.WriteAsync(raw, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        Console.WriteLine(
            $"[probe] S-> {clearFrame.Length,5} {Opcode(clearFrame):00000} " +
            $"{Convert.ToHexString(clearFrame.AsSpan(0, Math.Min(24, clearFrame.Length)))}");
    }

    /// <summary>
    /// Waits for the next frame with this opcode, skipping the ones that arrive
    /// first (the world stream is constant during a probe).
    /// </summary>
    public async Task<byte[]?> WaitForAsync(
        ushort opcode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(timeoutSource.Token))
            {
                if (Opcode(frame) == opcode)
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _client.Close();
        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var pending = new List<byte>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var clear = buffer.AsSpan(0, read).ToArray();
                _receiveCipher.Transform(clear);
                pending.AddRange(clear);
                while (pending.Count >= 4)
                {
                    var length = pending[0] | (pending[1] << 8);
                    if (length < 4 || pending.Count < length)
                    {
                        break;
                    }

                    var frame = pending.Take(length).ToArray();
                    pending.RemoveRange(0, length);
                    _received.Add(frame);
                    await _frames.Writer.WriteAsync(frame, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or SocketException or ObjectDisposedException or
            OperationCanceledException)
        {
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    private static ushort Opcode(byte[] frame) =>
        frame.Length >= 4 ? (ushort)(frame[2] | (frame[3] << 8)) : (ushort)0;

    public static string Describe(byte[] frame)
    {
        var text = new StringBuilder();
        for (var offset = 4; offset < frame.Length; offset++)
        {
            var value = frame[offset];
            text.Append(value is >= 32 and < 127 ? (char)value : '.');
        }

        return text.ToString();
    }
}
