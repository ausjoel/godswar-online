using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Godswar.Server.Packets;
using Npgsql;

// 控制台默认使用系统 ANSI 代码页，中文提示会显示成乱码。
// 在打印任何内容之前把输出编码切成 UTF-8。
try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.Title = "GodsWar 抓包代理";
}
catch (IOException)
{
    // 输出被重定向到文件或管道时无法设置编码，忽略即可。
}

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("参数错误：" + ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("用法：");
    Console.Error.WriteLine("  Godswar.CaptureProxy.exe --login-host <参考服地址> [选项]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("必填：");
    Console.Error.WriteLine("  --login-host <地址>        参考服（要被抓包的那台服务器）的 IP 或域名");
    Console.Error.WriteLine();
    Console.Error.WriteLine("可选：");
    Console.Error.WriteLine("  --login-port <端口>        参考服登录端口，默认 5999");
    Console.Error.WriteLine("  --local-login-port <端口>  本机监听的登录端口，默认 5999");
    Console.Error.WriteLine("  --local-game-port <端口>   本机监听的游戏端口，默认 7000");
    Console.Error.WriteLine("  --local-advertised-host <地址>");
    Console.Error.WriteLine("                             回写给客户端的目标地址，默认 127.1.1.110");
    Console.Error.WriteLine("  --out <文件>               抓包日志文件，默认 captures\\godswar-proxy-<时间>.log");
    Console.Error.WriteLine("  --monster-map-id <地图号>  记录怪物刷怪点时必须显式指定地图号（NPC 不需要）");
    Console.Error.WriteLine("  --disable-db               不写数据库，只写文本日志");
    Console.Error.WriteLine("  --postgres-connection-string <连接串>");
    Console.Error.WriteLine("                             数据库连接串，默认连本机 godswar 库");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

await using var log = new CaptureLog(options.OutputPath);
await using var packetLog = await PacketTransactionLog.CreateAsync(options, cts.Token);
var state = new ProxyState(options.DefaultGameHost, options.DefaultGamePort);

Console.WriteLine("========================================");
Console.WriteLine("  GodsWar 抓包代理（Capture Proxy）");
Console.WriteLine("========================================");
Console.WriteLine($"登录端口： 0.0.0.0:{options.LocalLoginPort}  ->  {options.LoginHost}:{options.LoginPort}");
Console.WriteLine($"游戏端口： 0.0.0.0:{options.LocalGamePort}  ->  由登录回包决定的目标");
Console.WriteLine($"重写游戏跳转： #{options.LocalAdvertisedHost}:{options.LocalGamePort}");
Console.WriteLine($"日志文件： {Path.GetFullPath(options.OutputPath)}");
Console.WriteLine(packetLog is null
    ? "数据库：   已禁用（--disable-db）"
    : $"数据库：   写入 packet_transactions，会话 {packetLog.SessionId}");
if (packetLog is not null)
{
    Console.WriteLine("刷怪记录： NPC 刷怪点全地图写入 npc_spawn_packets");
    Console.WriteLine(options.MonsterMapId is short monsterMapId
        ? $"          怪物刷怪点写入 monster_spawn_packets，已指定地图 {monsterMapId}"
        : "          怪物刷怪点需加 --monster-map-id <地图号> 才记录");
}
Console.WriteLine();
Console.WriteLine("请把游戏客户端连接到上面的登录端口，然后在游戏里操作需要抓取的功能。");
Console.WriteLine("按 Ctrl+C 停止。");

var login = RunListenerAsync(
    "LOGIN",
    options.LocalLoginPort,
    _ => ValueTask.FromResult((options.LoginHost, options.LoginPort)),
    bytes => RewriteLoginRedirect(bytes, options, state, log),
    log,
    packetLog,
    cts.Token);

var game = RunListenerAsync(
    "GAME",
    options.LocalGamePort,
    state.WaitForGameTargetAsync,
    null,
    log,
    packetLog,
    cts.Token);

var probeChannel = options.ProbePort > 0
    ? ProbeChannel.RunAsync(options.ProbePort, cts.Token)
    : Task.CompletedTask;

await Task.WhenAll(login, game, probeChannel);

// 顶层语句中只要出现 return 带值，就必须保证所有路径都返回值。
return 0;

static async Task RunListenerAsync(
    string name,
    int localPort,
    Func<CancellationToken, ValueTask<(string Host, int Port)>> targetResolver,
    Func<byte[], byte[]>? serverToClientTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Any, localPort);
    listener.Start();

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(
                () => HandleConnectionAsync(name, client, targetResolver, serverToClientTransform, log, packetLog, cancellationToken),
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

static async Task HandleConnectionAsync(
    string name,
    TcpClient client,
    Func<CancellationToken, ValueTask<(string Host, int Port)>> targetResolver,
    Func<byte[], byte[]>? serverToClientTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    CancellationToken cancellationToken)
{
    using var _ = client;
    client.NoDelay = true;

    var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    var target = await targetResolver(cancellationToken);
    var targetEndPoint = $"{target.Host}:{target.Port}";
    var connectionId = Guid.NewGuid();

    using var server = new TcpClient { NoDelay = true };
    await server.ConnectAsync(target.Host, target.Port, cancellationToken);

    log.Line($"{name} connected client={clientEndPoint} target={target.Host}:{target.Port}");

    await using var clientStream = client.GetStream();
    await using var serverStream = server.GetStream();
    var clientToServerCipher = new PacketCipher();
    var serverToClientCipher = new PacketCipher();

    // One gate serialises every write to the server - the client's own frames and
    // anything a probe injects - and it also protects the shared cipher, so the
    // stream the server decrypts can never be interleaved mid-frame.
    using var serverWriteGate = new SemaphoreSlim(1, 1);
    using var clientWriteGate = new SemaphoreSlim(1, 1);
    ProbeEndpoint? probe = null;
    if (string.Equals(name, "GAME", StringComparison.Ordinal))
    {
        probe = new ProbeEndpoint(
            name,
            connectionId,
            async (frames, token) =>
            {
                await serverWriteGate.WaitAsync(token);
                try
                {
                    var total = 0;
                    foreach (var frame in frames)
                    {
                        var raw = frame.ToArray();
                        clientToServerCipher.Transform(raw);
                        await serverStream.WriteAsync(raw, token);
                        total += raw.Length;
                    }

                    await serverStream.FlushAsync(token);
                    return total;
                }
                finally
                {
                    serverWriteGate.Release();
                }
            },
            async (frames, token) =>
            {
                // The client direction carries server-authored frames - a teleport
                // (10018), a notice, a status sync - and it is encrypted with the
                // server-to-client cipher, whose pointer the receiving pump keeps
                // in step. Padding keeps that pointer congruent modulo the cipher
                // period, so the client's own stream still decrypts afterwards.
                await clientWriteGate.WaitAsync(token);
                try
                {
                    var total = 0;
                    foreach (var frame in frames)
                    {
                        var raw = frame.ToArray();
                        serverToClientCipher.Transform(raw);
                        await clientStream.WriteAsync(raw, token);
                        total += raw.Length;
                    }

                    await clientStream.FlushAsync(token);
                    return total;
                }
                finally
                {
                    clientWriteGate.Release();
                }
            });
        ProbeChannel.Register(probe);
        log.Line($"{name} probe attached id={connectionId}");
    }

    var clientToServer = PumpAsync(
        $"{name} C->S",
        clientStream,
        serverStream,
        clientToServerCipher,
        clearTransform: null,
        log,
        packetLog,
        connectionId,
        name,
        "C2S",
        clientEndPoint,
        targetEndPoint,
        cancellationToken,
        serverWriteGate,
        probe: null);

    var serverToClient = PumpAsync(
        $"{name} S->C",
        serverStream,
        clientStream,
        serverToClientCipher,
        serverToClientTransform,
        log,
        packetLog,
        connectionId,
        name,
        "S2C",
        targetEndPoint,
        clientEndPoint,
        cancellationToken,
        clientWriteGate,
        probe);

    await Task.WhenAny(clientToServer, serverToClient);
    if (probe is not null)
    {
        ProbeChannel.Unregister(probe);
    }

    client.Close();
    server.Close();
    log.Line($"{name} closed client={clientEndPoint}");
}

static async Task PumpAsync(
    string direction,
    NetworkStream input,
    NetworkStream output,
    PacketCipher decodeCipher,
    Func<byte[], byte[]>? clearTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    Guid connectionId,
    string connectionName,
    string packetDirection,
    string sourceEndPoint,
    string destinationEndPoint,
    CancellationToken cancellationToken,
    SemaphoreSlim? writeGate = null,
    ProbeEndpoint? probe = null)
{
    var buffer = new byte[64 * 1024];
    var packetAccumulator = new PacketFrameAccumulator();

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            var raw = buffer.AsSpan(0, read).ToArray();
            byte[] outgoing;
            if (writeGate is not null)
            {
                await writeGate.WaitAsync(cancellationToken);
            }

            try
            {
                var clear = raw.ToArray();
                decodeCipher.Transform(clear);

                var patchedClear = clearTransform?.Invoke(clear) ?? clear;
                outgoing = ReapplyPatchToRaw(raw, clear, patchedClear);
                log.Chunk(direction, clear, raw);
                if (packetLog is not null || probe is not null)
                {
                    var chunkSequence = packetLog?.NextChunkSequence() ?? 0;
                    var packets = packetAccumulator.Append(patchedClear, outgoing);
                    if (packetLog is not null)
                    {
                        packetLog.Enqueue(
                            packets,
                            connectionId,
                            connectionName,
                            packetDirection,
                            sourceEndPoint,
                            destinationEndPoint,
                            chunkSequence);
                    }

                    if (probe is not null)
                    {
                        foreach (var packet in packets)
                        {
                            probe.Observe(packet.ClearBytes);
                        }
                    }
                }

                await output.WriteAsync(outgoing, cancellationToken);
            }
            finally
            {
                if (writeGate is not null)
                {
                    writeGate.Release();
                }
            }
        }
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
    {
    }
}

static byte[] RewriteLoginRedirect(
    byte[] bytes,
    Options options,
    ProxyState state,
    CaptureLog log)
{
    var output = bytes.ToArray();

    for (var offset = 0; offset <= output.Length - 44; offset++)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(offset, 2));
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(offset + 2, 2));
        if (opcode != 10001 || length < 44)
        {
            continue;
        }

        var hostField = ReadNullTerminated(output, offset + 5, 35);
        var originalHost = hostField.TrimStart('#');
        var originalPort = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(offset + 40, 4));

        // Follow the server's advertised game endpoint only when it points at
        // a different host. A single-host deployment advertises its own host
        // with the game port, and following that here would make the proxy
        // dial its own game listener instead of the server's real port; in
        // that case the explicit --default-game-host/--default-game-port pair
        // is the correct target.
        if (!string.IsNullOrWhiteSpace(originalHost) &&
            originalPort > 0 &&
            !string.Equals(
                originalHost,
                options.LoginHost,
                StringComparison.OrdinalIgnoreCase))
        {
            state.SetGameTarget(originalHost, originalPort);
            log.Line($"LOGIN redirect original=#{originalHost}:{originalPort}");
        }
        else
        {
            log.Line(
                $"LOGIN redirect same-host=#{originalHost}:{originalPort} " +
                "-> keeping the configured game target");
        }

        output.AsSpan(offset + 5, 35).Clear();
        Encoding.ASCII.GetBytes(options.LocalAdvertisedHost, output.AsSpan(offset + 5, Math.Min(35, options.LocalAdvertisedHost.Length)));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset + 40, 4), options.LocalGamePort);

        log.Line($"LOGIN redirect rewritten=#{options.LocalAdvertisedHost}:{options.LocalGamePort}");
    }

    return output;
}

static byte[] ReapplyPatchToRaw(byte[] raw, byte[] clear, byte[] patchedClear)
{
    if (clear.AsSpan().SequenceEqual(patchedClear))
    {
        return raw;
    }

    if (patchedClear.Length != raw.Length)
    {
        throw new InvalidOperationException("重写明文数据包时不允许改变字节数。");
    }

    var output = raw.ToArray();
    for (var i = 0; i < output.Length; i++)
    {
        output[i] = (byte)(raw[i] ^ clear[i] ^ patchedClear[i]);
    }

    return output;
}

static string ReadNullTerminated(byte[] bytes, int offset, int maxLength)
{
    var end = offset;
    var limit = Math.Min(bytes.Length, offset + maxLength);
    while (end < limit && bytes[end] != 0)
    {
        end++;
    }

    return Encoding.ASCII.GetString(bytes, offset, end - offset);
}
