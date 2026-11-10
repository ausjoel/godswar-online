using System.Net.Sockets;
using System.Text;

namespace Godswar.QuestProbe;

/// <summary>
/// A console probe that talks to a GodsWar server the way the installed client
/// does, so a question about the wire format can be answered by sending one frame
/// instead of clicking through the game.
/// </summary>
/// <remarks>
/// It exists because the quest objective encoding had to be learned by hand:
/// accepting a quest in the client, reading the capture, and repeating for every
/// quest shape. One probe answers the same question in one round trip.
/// <para>
/// Modes that open a socket are marked below. <c>--self-test</c> and
/// <c>--dump</c> are offline and only read a capture-proxy log.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Usage();
                return 1;
            }

            return args[0] switch
            {
                "--self-test" => SelfTest(Require(args, 1, "--self-test <capture.log>")),
                "--dump" => Dump(Require(args, 1, "--dump <capture.log>")),
                "--login" => await LoginAsync(args),
                "--enter" => await EnterAsync(args),
                "--status" => await ChannelAsync(args, "STATUS", trailing: 0),
                "--seen" => await ChannelAsync(
                    args,
                    $"SEEN GAME {Option(args, "--since") ?? "0"}",
                    trailing: -1),
                "--inject" => await ChannelAsync(
                    args,
                    $"INJECT GAME {Require(args, 1, "--inject <hex>[,<hex>]")}",
                    trailing: 0),
                "--inject-raw" => await ChannelAsync(
                    args,
                    $"INJECT-RAW GAME {Require(args, 1, "--inject-raw <hex>")}",
                    trailing: 0),
                "--inject-client" => await ChannelAsync(
                    args,
                    $"INJECT-CLIENT GAME {Require(args, 1, "--inject-client <hex>")}",
                    trailing: 0),
                "--ask-quest" => await AskQuestAsync(args),
                "--ask-npc" => await AskNpcAsync(args),
                "--sweep" => await SweepAsync(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Offline: runs the cipher over the ciphertext a capture holds and compares
    /// the result with the plaintext the proxy decoded. If this passes, this
    /// tool's wire format is the client's, before anything is sent anywhere.
    /// </summary>
    private static int SelfTest(string path)
    {
        var streams = CaptureLog.ReadStreams(path);
        var checkedStreams = 0;
        var checkedBytes = 0;
        var failures = 0;
        foreach (var stream in streams)
        {
            if (stream.Raw.Count == 0 || stream.Raw.Count != stream.Clear.Count)
            {
                continue;
            }

            var raw = stream.Raw.ToArray();
            new PacketCipher().Transform(raw);
            var matches = raw.AsSpan().SequenceEqual(stream.Clear.ToArray());
            checkedStreams++;
            checkedBytes += raw.Length;
            if (!matches)
            {
                failures++;
                var firstDiff = 0;
                while (firstDiff < raw.Length && raw[firstDiff] == stream.Clear[firstDiff])
                {
                    firstDiff++;
                }

                Console.Error.WriteLine(
                    $"FAIL {stream.Connection} {stream.Direction}: first difference " +
                    $"at byte {firstDiff} of {raw.Length}");
            }
        }

        Console.WriteLine(
            $"cipher self-test: {checkedStreams} streams, {checkedBytes} bytes, " +
            $"{failures} mismatches");
        return failures == 0 && checkedStreams > 0 ? 0 : 1;
    }

    /// <summary>Offline: lists what a capture holds, per connection and direction.</summary>
    private static int Dump(string path)
    {
        foreach (var stream in CaptureLog.ReadStreams(path))
        {
            var frames = CaptureLog.Frames(stream.Clear);
            Console.WriteLine(
                $"{stream.Connection,-5} {stream.Direction,-3} " +
                $"{stream.Raw.Count,7} bytes  {frames.Count,4} frames");
            foreach (var frame in frames.Take(12))
            {
                var opcode = frame[2] | (frame[3] << 8);
                Console.WriteLine(
                    $"    {frame.Length,5} opcode {opcode,5}  " +
                    $"{Convert.ToHexString(frame.AsSpan(0, Math.Min(20, frame.Length)))}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Opens a socket: replays the login frames a capture holds, prints what comes
    /// back and lifts the session token out of the 10001 redirect.
    /// </summary>
    /// <remarks>
    /// This logs the account in. On a shared server that can end the session of a
    /// client that is already playing on the same account, so it is never run
    /// implicitly - and the login frames carry per-session values, which is why
    /// they are replayed from a capture instead of being built here.
    /// </remarks>
    private static async Task<int> LoginAsync(string[] args)
    {
        var gateway = Option(args, "--gateway") ?? "127.1.1.110:5999";
        var from = Option(args, "--from") ??
            @"captures\godswar-proxy-20260924-203209.log";
        var (host, port) = Split(gateway);
        var login = CaptureLog.ReadStreams(from)
            .FirstOrDefault(stream =>
                stream.Connection == "LOGIN" && stream.Direction == "C->S");
        if (login is null)
        {
            Console.Error.WriteLine(
                $"[probe] no LOGIN C->S stream in {from}");
            return 1;
        }

        var frames = CaptureLog.Frames(login.Clear);
        Console.WriteLine(
            $"[probe] replaying {frames.Count} login frames to {host}:{port}");
        await using var connection = await ProbeConnection.ConnectAsync(
            host,
            port,
            CancellationToken.None);
        foreach (var frame in frames)
        {
            await connection.SendAsync(frame, CancellationToken.None);
            await Task.Delay(250);
        }

        await Task.Delay(1000);
        var redirect = connection.Received.FirstOrDefault(frame =>
            (frame[2] | (frame[3] << 8)) == 10001);
        if (redirect is null)
        {
            Console.Error.WriteLine(
                "[probe] no 10001 redirect came back; the login was not accepted");
            return 1;
        }

        Console.WriteLine(
            $"[probe] redirect: {Encoding.ASCII.GetString(redirect)}");
        Console.WriteLine(
            $"[probe] session token: {SessionToken(redirect) ?? "(not found)"}");
        return 0;
    }

    /// <summary>
    /// Opens a socket: reuses a live session's token by replaying the newest
    /// captured game-login frames, then walks the captured enter sequence.
    /// </summary>
    /// <remarks>
    /// The 10000 frame already carries the session token the gateway issued, so
    /// replaying the newest captured connection reuses that session instead of
    /// logging in again. The cost is the one this tool warns about: a second
    /// connection on the same token can end the session a playing client holds.
    /// </remarks>
    private static async Task<int> EnterAsync(string[] args)
    {
        var from = Option(args, "--from") ??
            @"captures\godswar-proxy-20260924-203209.log";
        var game = Option(args, "--game") ?? "127.1.1.110:13333";
        var frameCount = int.TryParse(Option(args, "--frames"), out var parsed)
            ? parsed
            : 8;
        var (host, port) = Split(game);

        var streams = CaptureLog.ReadStreams(from);
        var gameStreams = streams
            .Where(stream =>
                stream.Connection == "GAME" && stream.Direction == "C->S")
            .ToList();
        var fromEnd = int.TryParse(Option(args, "--from-end"), out var back)
            ? Math.Max(1, back)
            : 1;
        var gameStream = gameStreams.Count >= fromEnd
            ? gameStreams[^fromEnd]
            : null;
        if (gameStream is null)
        {
            Console.Error.WriteLine($"[probe] no GAME C->S stream in {from}");
            return 1;
        }

        var captured = CaptureLog.Frames(gameStream.Clear);
        var token = TokenOf(captured.FirstOrDefault());
        Console.WriteLine(
            $"[probe] session token in that capture: {token ?? "(none)"}");
        Console.WriteLine(
            $"[probe] connecting {host}:{port} and replaying {frameCount} frames");

        await using var connection = await ProbeConnection.ConnectAsync(
            host,
            port,
            CancellationToken.None);
        foreach (var frame in captured.Take(frameCount))
        {
            await connection.SendAsync(frame, CancellationToken.None);
            await Task.Delay(300);
            foreach (var reply in connection.Received.Skip(_reported))
            {
                Report(reply);
            }

            _reported = connection.Received.Count;
        }

        await Task.Delay(3000);
        foreach (var reply in connection.Received.Skip(_reported))
        {
            Report(reply);
        }

        _reported = connection.Received.Count;
        var opcodes = connection.Received
            .Select(frame => frame[2] | (frame[3] << 8))
            .Distinct()
            .Order()
            .ToArray();
        Console.WriteLine(
            $"[probe] {connection.Received.Count} frames back; opcodes: " +
            string.Join(", ", opcodes.Take(24)));
        var entered = opcodes.Any(opcode => opcode is 10019 or 10043 or 10090 or 10237);
        Console.WriteLine(entered
            ? "[probe] 看起来进到世界里了（收到进场景/快照帧）"
            : "[probe] 没看到进场景帧——令牌可能已被顶掉或失效");
        return entered ? 0 : 1;
    }

    private static int _reported;

    private static void Report(byte[] frame)
    {
        var opcode = frame[2] | (frame[3] << 8);
        Console.WriteLine(
            $"[probe] <-  {frame.Length,5} {opcode,5}  " +
            $"{Convert.ToHexString(frame.AsSpan(0, Math.Min(24, frame.Length)))}");
    }

    /// <summary>
    /// Lifts the session token out of a captured game-login frame the same way the
    /// client does: the printable run it carries after the account field.
    /// </summary>
    private static string? TokenOf(byte[]? frame)
    {
        if (frame is null)
        {
            return null;
        }

        var token = LongestPrintableRun(frame, 4);
        // The account field precedes the token; only a 32-byte run is the token.
        return token is null ? null : token.Length >= 32 ? token[^32..] : token;
    }

    /// <summary>
    /// The longest run of printable ASCII in a frame. A fresh builder is handed
    /// back on every improvement: assigning the running builder and then clearing
    /// it would hand back the cleared instance.
    /// </summary>
    private static string? LongestPrintableRun(byte[] frame, int from)
    {
        string? best = null;
        var current = new StringBuilder();
        for (var offset = from; offset < frame.Length; offset++)
        {
            var value = frame[offset];
            if (value is >= 32 and < 127)
            {
                current.Append((char)value);
                continue;
            }

            if (best is null || current.Length > best.Length)
            {
                best = current.ToString();
            }

            current.Clear();
        }

        if (best is null || current.Length > best.Length)
        {
            best = current.ToString();
        }

        return best;
    }

    /// <summary>
    /// Talks to the capture proxy's probe channel: frames posted here travel to
    /// the server over the client's own logged-in connection, which is what makes
    /// a question answerable without logging in again.
    /// </summary>
    /// <param name="trailing">
    /// Lines to read after the status line: 0 for one reply, -1 to read the count
    /// the reply announces.
    /// </param>
    private static async Task<int> ChannelAsync(
        string[] args,
        string command,
        int trailing)
    {
        var (host, port) = Split(Option(args, "--via") ?? "127.0.0.1:7099");
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, Encoding.UTF8)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(command);
        var status = await reader.ReadLineAsync();
        if (status is null)
        {
            Console.Error.WriteLine("[probe] 通道没有回话");
            return 1;
        }

        Console.WriteLine(status);
        if (trailing < 0)
        {
            var parts = status.Split(' ');
            var count = parts.Length > 2 && int.TryParse(parts[2], out var parsed)
                ? parsed
                : 0;
            for (var index = 0; index < count; index++)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("F ", StringComparison.Ordinal))
                {
                    var frame = Convert.FromHexString(line[2..]);
                    var opcode = frame.Length >= 4 ? frame[2] | (frame[3] << 8) : 0;
                    Console.WriteLine(
                        $"  {frame.Length,5} opcode {opcode,5}  " +
                        $"{Convert.ToHexString(frame.AsSpan(0, Math.Min(28, frame.Length)))}");
                    continue;
                }

                Console.WriteLine($"  {line}");
            }
        }
        else
        {
            for (var index = 0; index < trailing; index++)
            {
                Console.WriteLine(await reader.ReadLineAsync());
            }
        }

        return status.StartsWith("OK", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// Walks a range of quest ids and records what the reference server says about
    /// each one.
    /// </summary>
    /// <remarks>
    /// One quest id is enough to make the server publish the whole table of the npc
    /// that quest belongs to: 10077 lists the quests that npc gives together with
    /// the "acceptable now" flag - which is where the reference's own level gate is
    /// visible - and 10080 lists the quests it receives. Sweeping the ids therefore
    /// rebuilds the reference's quest chain npc by npc, without clicking anything
    /// and without the client's own gate.
    /// </remarks>
    private static async Task<int> SweepAsync(string[] args)
    {
        var from = int.Parse(Require(args, 1, "--sweep <起始任务号> <结束任务号>"));
        var to = args.Length > 2 ? int.Parse(args[2]) : from;
        var waitMs = int.TryParse(Option(args, "--wait-ms"), out var parsed) ? parsed : 600;
        var outPath = Option(args, "--out") ??
            $@"captures\ref-quest-chain-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var captureLog = Option(args, "--from") ??
            @"captures\godswar-proxy-20260924-203209.log";
        var via = Option(args, "--via") ?? "127.0.0.1:7099";

        var template = CaptureLog.ReadStreams(captureLog)
            .Where(stream =>
                stream.Connection == "GAME" && stream.Direction == "C->S")
            .SelectMany(stream => CaptureLog.Frames(stream.Clear))
            .LastOrDefault(frame =>
                (frame[2] | (frame[3] << 8)) == 10082 && frame.Length >= 648);
        var confirmation = CaptureLog.ReadStreams(captureLog)
            .Where(stream =>
                stream.Connection == "GAME" && stream.Direction == "C->S")
            .SelectMany(stream => CaptureLog.Frames(stream.Clear))
            .LastOrDefault(frame => (frame[2] | (frame[3] << 8)) == 10091);
        if (template is null || confirmation is null)
        {
            Console.Error.WriteLine("[probe] 抓包里缺 C2S 10082 / 10091 模板帧");
            return 1;
        }

        var gives = new SortedDictionary<uint, List<(uint Quest, uint Available)>>();
        var receives = new SortedDictionary<uint, List<uint>>();
        var lines = new List<string>
        {
            $"# 参考服任务链扫描 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  任务 {from}..{to}"
        };
        await using var channel = await ChannelConnection.OpenAsync(via);
        for (var questId = from; questId <= to; questId++)
        {
            var request = template.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                request.AsSpan(12, 4),
                (uint)questId);
            var (status, _) = await channel.SendAsync("STATUS", readCount: false);
            var mark = ParseSequence(status);
            var (injected, _) = await channel.SendAsync(
                $"INJECT GAME {Convert.ToHexString(request)}," +
                Convert.ToHexString(confirmation),
                readCount: false);
            if (!injected.StartsWith("OK", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[probe] {questId}: {injected.Trim()}");
                break;
            }

            await Task.Delay(waitMs);
            var (_, seen) = await channel.SendAsync($"SEEN GAME {mark}", readCount: true);
            var frames = seen
                .Where(line => line.StartsWith("F ", StringComparison.Ordinal))
                .Select(line => Convert.FromHexString(line[2..]))
                .ToArray();
            var summary = new List<string>();
            foreach (var frame in frames)
            {
                if (frame.Length < 12)
                {
                    continue;
                }

                var opcode = frame[2] | (frame[3] << 8);
                var npc = System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(frame.AsSpan(4, 4));
                if (opcode == 10077)
                {
                    var count = System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32LittleEndian(frame.AsSpan(8, 4));
                    var list = gives.TryGetValue(npc, out var existing)
                        ? existing
                        : gives[npc] = [];
                    for (var index = 0;
                         index < count && 12 + (index * 8) + 8 <= frame.Length;
                         index++)
                    {
                        var quest = System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt32LittleEndian(frame.AsSpan(12 + (index * 8), 4));
                        var available = System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt32LittleEndian(frame.AsSpan(16 + (index * 8), 4));
                        if (!list.Any(entry => entry.Quest == quest))
                        {
                            list.Add((quest, available));
                        }

                        summary.Add($"给{npc}:{quest}(可接={available})");
                    }
                }
                else if (opcode == 10080)
                {
                    var count = System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32LittleEndian(frame.AsSpan(8, 4));
                    var list = receives.TryGetValue(npc, out var existing)
                        ? existing
                        : receives[npc] = [];
                    for (var index = 0;
                         index < count && 12 + (index * 4) + 4 <= frame.Length;
                         index++)
                    {
                        var quest = System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt32LittleEndian(frame.AsSpan(12 + (index * 4), 4));
                        if (!list.Contains(quest))
                        {
                            list.Add(quest);
                        }

                        summary.Add($"收{npc}:{quest}");
                    }
                }
                else if (opcode == 10083 && frame.Length >= 20)
                {
                    summary.Add(
                        $"场景可接:{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4))}");
                }
            }

            var line = $"{questId}: " + (summary.Count == 0 ? "（无任务相关回包）" : string.Join(" ", summary));
            lines.Add(line);
            Console.WriteLine(line);
        }

        lines.Add(string.Empty);
        lines.Add($"# NPC 给任务表（{gives.Count} 个 NPC）");
        foreach (var (npc, list) in gives)
        {
            lines.Add(
                $"npc {npc} 给: " +
                string.Join(", ", list.Select(entry => $"{entry.Quest}(可接={entry.Available})")));
        }

        lines.Add(string.Empty);
        lines.Add($"# NPC 收任务表（{receives.Count} 个 NPC）");
        foreach (var (npc, list) in receives)
        {
            lines.Add($"npc {npc} 收: {string.Join(", ", list)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllLinesAsync(outPath, lines);
        Console.WriteLine($"[probe] 结果写入 {Path.GetFullPath(outPath)}");
        Console.WriteLine(
            $"[probe] 共 {gives.Count} 个 NPC 给任务、{receives.Count} 个 NPC 收任务");
        return 0;
    }

    /// <summary>
    /// Clicks an npc from the proxy: injects the captured C2S 10067 with this
    /// npc's id and prints everything the server answers with.
    /// </summary>
    /// <remarks>
    /// This is the primitive the whole chain walk needs. Clicking an npc is what
    /// makes the reference server open its dialogue, report which quests that npc
    /// gives and receives (10077/10080) and which functions it carries (10067) -
    /// the skeleton of the quest chain, npc by npc, without the client's own
    /// level gate or any clicking.
    /// </remarks>
    private static async Task<int> AskNpcAsync(string[] args)
    {
        var npcId = uint.Parse(Require(args, 1, "--ask-npc <NPC交互号>"));
        var from = Option(args, "--from") ??
            @"captures\godswar-proxy-20260924-203209.log";
        var via = Option(args, "--via") ?? "127.0.0.1:7099";

        var template = CaptureLog.ReadStreams(from)
            .Where(stream =>
                stream.Connection == "GAME" && stream.Direction == "C->S")
            .SelectMany(stream => CaptureLog.Frames(stream.Clear))
            .LastOrDefault(frame =>
                (frame[2] | (frame[3] << 8)) == 10067 && frame.Length >= 48);
        if (template is null)
        {
            Console.Error.WriteLine($"[probe] {from} 里没有 C2S 10067 模板帧");
            return 1;
        }

        var request = template.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(4, 4),
            npcId);
        // +24 is the one field the reference server actually validates on an npc
        // click: the captured 464.0 is answered, while the player's own
        // coordinates and the npc's are all ignored. Whatever the field means, it
        // has to be the value a real click carried.
        var at = Option(args, "--at");
        if (at is not null)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                request.AsSpan(24, 4),
                float.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                request.AsSpan(24, 4),
                464.0f);
        }

        await using var channel = await ChannelConnection.OpenAsync(via);
        var (status, _) = await channel.SendAsync("STATUS", readCount: false);
        var mark = ParseSequence(status);
        Console.WriteLine(
            $"[probe] 通道 {status.Trim()}；注入 C2S 10067(点 NPC {npcId})");
        var (injected, _) = await channel.SendAsync(
            $"INJECT GAME {Convert.ToHexString(request)}",
            readCount: false);
        Console.WriteLine($"[probe] 注入结果 {injected.Trim()}");
        if (!injected.StartsWith("OK", StringComparison.Ordinal))
        {
            return 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        var (seen, lines) = await channel.SendAsync($"SEEN GAME {mark}", readCount: true);
        var frames = lines
            .Where(line => line.StartsWith("F ", StringComparison.Ordinal))
            .Select(line => Convert.FromHexString(line[2..]))
            .ToArray();
        Console.WriteLine(
            $"[probe] {seen.Trim()} 帧新回包；号分布: " +
            string.Join(
                ", ",
                frames
                    .Where(frame => frame.Length >= 4)
                    .GroupBy(frame => frame[2] | (frame[3] << 8))
                    .OrderByDescending(group => group.Count())
                    .Select(group => $"{group.Key}×{group.Count()}")));

        foreach (var frame in frames)
        {
            DescribeQuestTraffic(frame);
        }

        return 0;
    }

    /// <summary>
    /// Prints the frames a quest walk cares about: the npc's function list, the
    /// quests it gives and receives, the dialogue numbers and the quest list.
    /// </summary>
    private static void DescribeQuestTraffic(byte[] frame)
    {
        if (frame.Length < 4)
        {
            return;
        }

        var opcode = frame[2] | (frame[3] << 8);
        uint U32(int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                frame.AsSpan(offset, 4));
        switch (opcode)
        {
            case 10067 when frame.Length >= 48:
            {
                var script = LongestPrintableRun(frame, 16) ?? string.Empty;
                Console.WriteLine(
                    $"  10067 npc={U32(4)} flags=0x{U32(8):X} 功能表=0x{U32(12):X} " +
                    $"脚本=\"{script}\"");
                break;
            }
            case 10069 when frame.Length >= 12:
                Console.WriteLine($"  10069 npc={U32(4)} 功能={U32(8)} 页={U32(12)}");
                break;
            case 10070 when frame.Length >= 16:
            {
                var numbers = new List<uint>();
                for (var offset = 12; offset + 4 <= frame.Length; offset += 4)
                {
                    numbers.Add(U32(offset));
                }

                Console.WriteLine(
                    $"  10070 npc={U32(4)} 功能={U32(8)} 号={string.Join(",", numbers)}");
                break;
            }
            case 10077 when frame.Length >= 12:
            {
                var count = U32(8);
                var entries = new List<string>();
                for (var index = 0; index < count && 12 + (index * 8) + 8 <= frame.Length; index++)
                {
                    entries.Add($"{U32(12 + (index * 8))}(可接={U32(16 + (index * 8))})");
                }

                Console.WriteLine(
                    $"  10077 npc={U32(4)} 给任务×{count}: {string.Join(", ", entries)}");
                break;
            }
            case 10080 when frame.Length >= 12:
            {
                var count = U32(8);
                var entries = new List<uint>();
                for (var index = 0; index < count && 12 + (index * 4) + 4 <= frame.Length; index++)
                {
                    entries.Add(U32(12 + (index * 4)));
                }

                Console.WriteLine(
                    $"  10080 npc={U32(4)} 收任务×{count}: {string.Join(", ", entries)}");
                break;
            }
            case 10092 when frame.Length >= 8:
            {
                var count = U32(4);
                var entries = new List<int>();
                for (var index = 0; index < count && 8 + (index * 2) + 2 <= frame.Length; index++)
                {
                    entries.Add(System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt16LittleEndian(frame.AsSpan(8 + (index * 2), 2)));
                }

                Console.WriteLine($"  10092 任务×{count}: {string.Join(", ", entries)}");
                break;
            }
        }
    }

    /// <summary>
    /// Asks the server for one quest's own definition and decodes the answer.
    /// </summary>
    /// <remarks>
    /// The client asks with a single 648-byte C2S 10082 whose quest id sits at
    /// +12 - captured 2026-09-24 20:57:26 for quest 1533, answered a moment later
    /// by the matching S2C 10082. Sending that frame for any quest id therefore
    /// reads that quest out of the server without touching the client's own level
    /// gate, which is what makes the whole chain reachable.
    /// </remarks>
    private static async Task<int> AskQuestAsync(string[] args)
    {
        var questId = uint.Parse(Require(args, 1, "--ask-quest <任务号>"));
        var from = Option(args, "--from") ??
            @"captures\godswar-proxy-20260924-203209.log";
        var via = Option(args, "--via") ?? "127.0.0.1:7099";

        var template = CaptureLog.ReadStreams(from)
            .Where(stream =>
                stream.Connection == "GAME" && stream.Direction == "C->S")
            .SelectMany(stream => CaptureLog.Frames(stream.Clear))
            .LastOrDefault(frame =>
                (frame[2] | (frame[3] << 8)) == 10082 && frame.Length >= 648);
        if (template is null)
        {
            Console.Error.WriteLine(
                $"[probe] {from} 里没有 C2S 10082 模板帧（需要一份含接任务的抓包）");
            return 1;
        }

        var request = template.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(12, 4),
            questId);

        // The accept is two frames sent in the same millisecond: the 648-byte
        // 10082 carrying the quest, then an eight-byte 10091 confirmation. The
        // server answered both only when both arrived, so the probe sends both
        // unless --query-only asks for the detail on its own.
        var queryOnly = args.Contains("--query-only", StringComparer.Ordinal);
        var confirmation = queryOnly
            ? null
            : CaptureLog.ReadStreams(from)
                .Where(stream =>
                    stream.Connection == "GAME" && stream.Direction == "C->S")
                .SelectMany(stream => CaptureLog.Frames(stream.Clear))
                .LastOrDefault(frame => (frame[2] | (frame[3] << 8)) == 10091);
        var block = confirmation is null
            ? Convert.ToHexString(request)
            : $"{Convert.ToHexString(request)},{Convert.ToHexString(confirmation)}";

        await using var channel = await ChannelConnection.OpenAsync(via);
        var (status, _) = await channel.SendAsync("STATUS", readCount: false);
        var mark = ParseSequence(status);
        Console.WriteLine(
            $"[probe] 通道 {via} {status.Trim()}；注入 C2S 10082(任务 {questId})" +
            (confirmation is null ? "（仅查询）" : " + C2S 10091（确认）"));

        var (injected, _) = await channel.SendAsync(
            $"INJECT GAME {block}",
            readCount: false);
        Console.WriteLine($"[probe] 注入结果 {injected.Trim()}");
        if (!injected.StartsWith("OK", StringComparison.Ordinal))
        {
            return 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(
            double.TryParse(Option(args, "--wait"), out var waitSeconds)
                ? waitSeconds
                : 2));
        var (seen, lines) = await channel.SendAsync(
            $"SEEN GAME {mark}",
            readCount: true);
        Console.WriteLine($"[probe] {seen.Trim()} 帧新回包");

        var answers = lines
            .Where(line => line.StartsWith("F ", StringComparison.Ordinal))
            .Select(line => Convert.FromHexString(line[2..]))
            .Where(frame => frame.Length >= 64 && (frame[2] | (frame[3] << 8)) == 10082)
            .Where(frame =>
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    frame.AsSpan(12, 4)) == questId)
            .ToArray();

        // Whatever the server said, show its shape: a refusal is often a different
        // opcode (a notice, a dialog line) rather than a 10082 with zeros.
        var histogram = lines
            .Where(line => line.StartsWith("F ", StringComparison.Ordinal))
            .Select(line => Convert.FromHexString(line[2..]))
            .Where(frame => frame.Length >= 4)
            .GroupBy(frame => frame[2] | (frame[3] << 8))
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key}×{group.Count()}");
        Console.WriteLine($"[probe] 新回包的号: {string.Join(", ", histogram)}");
        foreach (var line in lines
                     .Where(line => line.StartsWith("F ", StringComparison.Ordinal)))
        {
            DescribeQuestTraffic(Convert.FromHexString(line[2..]));
        }

        if (answers.Length == 0)
        {
            Console.WriteLine(
                "[probe] 没有该任务号的 10082 回包——参考服没有给出这个任务" +
                "（等级不足、任务不存在、或该号不给这个任务）");
            foreach (var line in lines
                         .Where(line => line.StartsWith("F ", StringComparison.Ordinal))
                         .Take(6))
            {
                Console.WriteLine($"        {line}");
            }

            return 1;
        }

        DescribeQuestAnswer(answers[^1]);
        return 0;
    }

    private static long ParseSequence(string status)
    {
        // STATUS answers "OK <connections> GAME:<sequence>,...".
        var index = status.IndexOf("GAME:", StringComparison.Ordinal);
        if (index < 0)
        {
            return 0;
        }

        var digits = new string(status[(index + 5)..]
            .TakeWhile(char.IsAsciiDigit)
            .ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    /// <summary>
    /// Prints one S2C 10082 the way the frame holds it: who gives and receives the
    /// quest, its kind, every target slot, and the reward slots.
    /// </summary>
    private static void DescribeQuestAnswer(byte[] frame)
    {
        uint U32(int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                frame.AsSpan(offset, 4));
        int I32(int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                frame.AsSpan(offset, 4));

        Console.WriteLine();
        Console.WriteLine(
            $"任务 {U32(12)}   给任务NPC {U32(4)}   交任务NPC {U32(8)}   " +
            $"kind={I32(20)}   记录数={U32(16)}   帧长={frame.Length}");
        var targets = new List<string>();
        for (var slot = 0; slot < 6; slot++)
        {
            var monster = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(frame.AsSpan(32 + (slot * 2), 2));
            var required = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(frame.AsSpan(48 + (slot * 2), 2));
            if (monster != 0 || required != 0)
            {
                targets.Add($"怪物 {monster} ×{required}");
            }
        }

        Console.WriteLine(targets.Count == 0
            ? "目标: （无击杀目标）"
            : "目标: " + string.Join(" | ", targets));

        var rewards = new List<string>();
        for (var slot = 0; slot < 8; slot++)
        {
            var item = U32(64 + (slot * 72) + 8);
            rewards.Add(item == uint.MaxValue ? "-" : item.ToString());
        }

        Console.WriteLine("奖励槽: [" + string.Join(", ", rewards) + "]");
        Console.WriteLine($"原始帧: {Convert.ToHexString(frame)}");
    }

    /// <summary>A command connection to the proxy's probe channel.</summary>
    private sealed class ChannelConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private ChannelConnection(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }

        public static async Task<ChannelConnection> OpenAsync(string endpoint)
        {
            var (host, port) = Split(endpoint);
            var client = new TcpClient();
            await client.ConnectAsync(host, port);
            return new ChannelConnection(client);
        }

        public async Task<(string Status, IReadOnlyList<string> Lines)> SendAsync(
            string command,
            bool readCount)
        {
            await _writer.WriteLineAsync(command);
            var status = await _reader.ReadLineAsync() ?? string.Empty;
            var lines = new List<string>();
            if (readCount)
            {
                var parts = status.Split(' ');
                if (parts.Length > 2 && int.TryParse(parts[2], out var count))
                {
                    for (var index = 0; index < count; index++)
                    {
                        var line = await _reader.ReadLineAsync();
                        if (line is null)
                        {
                            break;
                        }

                        lines.Add(line);
                    }
                }
            }

            return (status, lines);
        }

        public ValueTask DisposeAsync()
        {
            _client.Close();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Lifts the printable session token out of a 10001 redirect: the longest
    /// printable run it carries, which avoids hard-coding an offset.
    /// </summary>
    private static string? SessionToken(byte[] redirect) =>
        LongestPrintableRun(redirect, 4) is { Length: >= 8 } run ? run : null;

    private static string Require(string[] args, int index, string usage)
    {
        if (args.Length <= index)
        {
            throw new ArgumentException($"用法：{usage}");
        }

        return args[index];
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static (string Host, int Port) Split(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        return separator < 0
            ? (endpoint, 5999)
            : (endpoint[..separator], int.Parse(endpoint[(separator + 1)..]));
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine($"未知模式：{mode}");
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            GodsWar 任务探针（Quest Probe）

            离线模式（只读抓包日志，不联网）：
              --self-test <capture.log>   用抓包里的明文/密文对校验本工具的编解码
              --dump <capture.log>        列出抓包里的连接、方向与帧

            联网模式（会占用账号，跑之前先确认没人在线玩）：
              --login [--gateway 127.1.1.110:5999] [--from <capture.log>]
                                          重放抓到的登录帧，打印回包与会话令牌
              --enter [--from <capture.log>] [--from-end <n>] [--game host:port]
                                          复用抓到的令牌直接连游戏口（令牌一次性，通常被拒）

            代理注入模式（用客户端已登录的那条连接发包，不需要登录）：
              先在抓包代理上加 --probe-port 7099 重启，然后：
              --status                    看有哪些可注入的连接
              --seen [--since <序号>]      列出服务器最近发来的帧（含号与长度）
              --inject <hex>[,<hex>…]      把帧注入到服务器方向（自动补齐到 256 字节整数倍）
              --inject-raw <hex>           不补齐（用来验证对齐假设）
              都可加 --via 127.0.0.1:7099 指定通道地址

            后续里程碑（还没实现）：
              --probe-quest <任务号>       自动走"点NPC→接任务"并发包，解码目标槽
            """);
    }
}
