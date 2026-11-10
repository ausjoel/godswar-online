using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Operators;
using Godswar.Server.Application.Progression;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// ROUND2: operator (GM) chat commands: moderation, teleport, the GM's own
// powers, buffs and live item edits. Only accounts in the developer-command
// allowlist (DeveloperCommandOptions.Allows) can use them. Like the stock
// developer commands, a recognised command is always consumed: it never
// reaches chat, and a refused attempt is only logged on the server. Every
// use is logged as "[operator] ..." and as a row in public.operator_log.
// None of the command words contains the letters "gm", which the client's
// chat filter masks.
internal sealed partial class GameClientHandler
{
    private const uint OperatorWorldChannelWord = 0x482;
    private const int OperatorMaximumMuteMinutes = 525_600;

    private static readonly string[] OperatorCommandWords =
    [
        "/announce", "/kick", "/mute", "/unmute", "/ban", "/unban",
        "/goto", "/leave", "/testrun", "/summon", "/move", "/where", "/instances", "/gm", "/panel", "/**",
        "/god", "/walls", "/speed", "/power", "/armor", "/bufflock", "/petlock", "/powers",
        "/buff", "/give", "/bag", "/bind", "/dupe", "/itemset", "/commands", "/mall",
        .. (ProfessionBlueprintCatalog.Enabled ? new[] { "/craft" } : Array.Empty<string>()) // PROF (q09): GODSWAR_PROFESSIONS=1 only
    ];

    private bool _operatorMuteLoaded;

    private async Task<bool> HandleOperatorCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text))
        {
            return false;
        }

        var offset = DeveloperCommandText.FindCommandOffset(
            text,
            OperatorCommandWords,
            out var word);
        if (offset < 0)
        {
            return false;
        }

        if (_account is null || _character is null ||
            !await HasOperatorAuthorityAsync(cancellationToken))
        {
            Console.WriteLine(
                $"[operator] denied account={_account?.Id ?? 0} " +
                $"character={_character?.Name ?? "none"} command={word}");
            return true;
        }

        var body = text[offset..].Trim();
        var args = body.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string reply;
        try
        {
            reply = word.ToLowerInvariant() switch
            {
                "/announce" => await OperatorAnnounceAsync(body[word.Length..].Trim(), cancellationToken),
                "/kick" => await OperatorKickAsync(args, cancellationToken),
                "/mute" => await OperatorMuteAsync(args, cancellationToken),
                "/unmute" => await OperatorUnmuteAsync(args, cancellationToken),
                "/ban" => await OperatorBanAsync(args, cancellationToken),
                "/unban" => await OperatorUnbanAsync(args, cancellationToken),
                "/goto" => await OperatorGotoAsync(args, cancellationToken),
                "/leave" => await OperatorLeaveRunAsync(cancellationToken),
                "/testrun" => await OperatorStartTestRunAsync(args, cancellationToken),
                "/summon" => await OperatorSummonAsync(args, cancellationToken),
                "/move" => await OperatorMoveAsync(args, cancellationToken),
                "/where" => OperatorWhere(args),
                "/instances" => OperatorInstances(),
                // The client masks "gm" to "**" before sending, so typing /gm arrives as /**.
                "/gm" or "/panel" or "/**" => await OperatorOpenPanelAsync(cancellationToken),
                "/god" => await OperatorTogglePowerAsync(word, args, cancellationToken),
                "/walls" => await OperatorWallsAsync(args, cancellationToken),
                "/bufflock" => await OperatorTogglePowerAsync(word, args, cancellationToken),
                "/petlock" => await OperatorTogglePowerAsync(word, args, cancellationToken),
                "/speed" => await OperatorNumberPowerAsync(word, args, cancellationToken),
                "/power" => await OperatorNumberPowerAsync(word, args, cancellationToken),
                "/armor" => await OperatorNumberPowerAsync(word, args, cancellationToken),
                "/powers" => await OperatorPowersAsync(args, cancellationToken),
                "/buff" => await OperatorBuffAsync(args, cancellationToken),
                "/give" => await OperatorGiveAsync(args, cancellationToken),
                "/mall" => await OperatorMallAsync(args, cancellationToken),
                "/craft" => await OperatorCraftAsync(args, cancellationToken), // PROF (q09)
                "/bag" => await OperatorBagAsync(args, cancellationToken),
                "/bind" => await OperatorItemFieldAsync(word, args, cancellationToken),
                "/itemset" => await OperatorItemFieldAsync(word, args, cancellationToken),
                "/dupe" => await OperatorDuplicateAsync(args, cancellationToken),
                "/commands" => OperatorUsage,
                _ => OperatorUsage
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            reply = $"failed: {error.Message}";
            Console.WriteLine(
                $"[operator] {word} failed account={_account.Id} " +
                $"character={_character.Name}: {error}");
        }

        foreach (var line in reply.Split('\n'))
        {
            await SendDeveloperItemFeedbackAsync(
                packet,
                $"[{word.TrimStart('/')}] {line}",
                cancellationToken);
        }

        return true;
    }

    private const string OperatorUsage =
        "/announce <text> | /kick <name> | /mute <name> <minutes> | /unmute <name>\n" +
        "/ban <name> [reason] | /unban <name> | /goto <name> | /leave | /summon <name>\n" +
        "/move <map> <x> <z> | /where [name] | /instances | /gm | /god on|off | /walls on|off (on = walk through) | /speed <x>|off [@name]\n" +
        "/power <x>|off | /armor <n>|off | /bufflock on|off | /petlock on|off | /powers [off]\n" +
        "/buff list [hostile] [page] | /buff <skill>|s<status> [minutes] [@name] | /buff info s<status>\n" + // FINALISE2
        "/give <itemId> [qty] [bound] [@name]\n" +
        "/mall [category [page] | next | prev | find <name or id>] (GM Mall shop window)\n" +
        "/bag [name] | /bind <slot> on|off [@name] | /dupe <slot> [@name]\n" +
        "/itemset <slot> quality|grade|stack|exp <n> [@name] | /level, /talent, /zenergy ... @name";

    // ---- logging -----------------------------------------------------------

    private async Task LogOperatorAsync(
        string command,
        string target,
        string detail,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[operator] {command} account={_account?.Id ?? 0} " +
            $"character={_character?.Name ?? "none"} target={target} {detail}");
        var store = OperatorControls.Store;
        if (store is null || _account is null || _character is null)
        {
            return;
        }

        try
        {
            await store.LogAsync(
                _account.Id,
                _character.Name,
                command,
                target,
                detail,
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[operator] log row failed: {error.Message}");
        }
    }

    private static IOperatorStore RequireOperatorStore() =>
        OperatorControls.Store ??
        throw new InvalidOperationException("the operator database store is unavailable");

    /// <summary>Strips a trailing "@Name" token; returns the name or null.</summary>
    internal static string? ExtractOperatorTargetSuffix(ref string text)
    {
        var trimmed = text.TrimEnd();
        var space = trimmed.LastIndexOfAny([' ', '\t']);
        if (space < 0 || space + 2 > trimmed.Length || trimmed[space + 1] != '@')
        {
            return null;
        }

        var name = trimmed[(space + 2)..];
        text = trimmed[..space];
        return name;
    }

    private static string? TakeTarget(ref string[] args)
    {
        if (args.Length > 0 && args[^1].Length > 1 && args[^1][0] == '@')
        {
            var name = args[^1][1..];
            args = args[..^1];
            return name;
        }

        return null;
    }

    // ---- moderation --------------------------------------------------------

    private async Task<string> OperatorAnnounceAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (message.Length == 0)
        {
            return "usage: /announce <text>";
        }

        if (message.Length > PacketBuilder.CenteredRedAnnouncementMaximumTextLength ||
            message.Any(static character => character is < ' ' or > '~') ||
            message.Contains("|c", StringComparison.OrdinalIgnoreCase))
        {
            return $"the banner takes at most " +
                $"{PacketBuilder.CenteredRedAnnouncementMaximumTextLength} plain ASCII characters";
        }

        var banner = PacketBuilder.CenteredRedAnnouncement(message);
        var sessions = _registry.SnapshotWorldReadySessions();
        var bannered = 0;
        foreach (var context in sessions)
        {
            try
            {
                await context.Session.SendAsync(banner, cancellationToken, "OperatorAnnouncement");
                bannered++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[operator] banner to {context.DisplayName} failed: {error.Message}");
            }
        }

        // the admin 2026-11-10 (board.server_info = 1): the chat box is for player chat, so the copy that stays on screen
        // goes to the board (top box) instead of World chat.
        var chatted = 0;
        if (ServerMessageRouting.ToTopBox)
        {
            await _registry.PublishBoardMessageAsync(message, cancellationToken);
        }
        else
        {
            // The same text as a World chat line, so it stays in everyone's chat
            // history. The client prints the line as "<speaker>:<text>".
            var recipients = _registry.ResolveChatRecipients(_session, camp: null, characterIds: null);
            chatted = await _registry.DeliverChatAsync(
                recipients,
                recipient => PacketBuilder.ChatMessage(
                    recipient.SenderObjectId,
                    OperatorWorldChannelWord,
                    "Announcement:" + message),
                "OperatorAnnouncementChat",
                cancellationToken);
            await _registry.MirrorToBoardAsync(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.BoardAnnouncements, message, cancellationToken);
        }
        await LogOperatorAsync(
            "announce",
            "all",
            $"banner={bannered} chat={chatted} text='{message}'",
            cancellationToken);
        return $"sent to {bannered} players (chat {chatted})";
    }

    private async Task<string> OperatorKickAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2)
        {
            return "usage: /kick <name>";
        }

        var target = _registry.FindOnlineCharacter(args[1]);
        if (target is null)
        {
            return $"{args[1]} is not online";
        }

        if (target.Session == _session)
        {
            return "you cannot kick yourself";
        }

        await TryNotifyAsync(target, "You were disconnected by the server staff.", cancellationToken);
        target.Session.Disconnect();
        await LogOperatorAsync("kick", target.CharacterName, $"account={target.AccountId}", cancellationToken);
        return $"{target.CharacterName} was disconnected";
    }

    private async Task<string> OperatorMuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 3 ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 1 or > OperatorMaximumMuteMinutes)
        {
            return $"usage: /mute <name> <minutes 1-{OperatorMaximumMuteMinutes}>";
        }

        var character = await RequireOperatorStore().FindCharacterAsync(args[1], cancellationToken);
        if (character is null)
        {
            return $"no character named {args[1]}";
        }

        var until = DateTimeOffset.UtcNow.AddMinutes(minutes);
        await RequireOperatorStore().SetMuteAsync(
            character.CharacterId,
            checked((int)until.ToUnixTimeSeconds()),
            cancellationToken);
        OperatorControls.SetMutedUntil(character.CharacterId, until);
        var online = _registry.FindOnlineCharacter(character.CharacterName);
        if (online is not null)
        {
            await TryNotifyAsync(online, $"You are muted for {minutes} minutes.", cancellationToken);
        }

        await LogOperatorAsync(
            "mute",
            character.CharacterName,
            $"minutes={minutes} until={until:O} online={online is not null}",
            cancellationToken);
        return $"{character.CharacterName} is muted until {until:yyyy-MM-dd HH:mm} UTC";
    }

    private async Task<string> OperatorUnmuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2)
        {
            return "usage: /unmute <name>";
        }

        var character = await RequireOperatorStore().FindCharacterAsync(args[1], cancellationToken);
        if (character is null)
        {
            return $"no character named {args[1]}";
        }

        await RequireOperatorStore().SetMuteAsync(character.CharacterId, 0, cancellationToken);
        OperatorControls.SetMutedUntil(character.CharacterId, null);
        await LogOperatorAsync("unmute", character.CharacterName, string.Empty, cancellationToken);
        return $"{character.CharacterName} can chat again";
    }

    private async Task<string> OperatorBanAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            return "usage: /ban <name> [reason]";
        }

        var character = await RequireOperatorStore().FindCharacterAsync(args[1], cancellationToken);
        if (character is null)
        {
            return $"no character named {args[1]}";
        }

        var reason = string.Join(' ', args.Skip(2));
        await RequireOperatorStore().SetAccountStatusAsync(
            character.AccountId,
            OperatorControls.AccountStatusBanned,
            cancellationToken);
        var online = _registry.FindOnlineAccount(character.AccountId);
        if (online is not null)
        {
            await TryNotifyAsync(online, "This account has been banned.", cancellationToken);
            online.Session.Disconnect();
        }

        await LogOperatorAsync(
            "ban",
            character.CharacterName,
            $"account={character.AccountId} username={character.Username} " +
            $"kicked={online is not null} reason='{reason}'",
            cancellationToken);
        return $"account {character.Username} ({character.CharacterName}) is banned" +
            (online is not null ? " and was disconnected" : string.Empty);
    }

    private async Task<string> OperatorUnbanAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2)
        {
            return "usage: /unban <name>";
        }

        var character = await RequireOperatorStore().FindCharacterAsync(args[1], cancellationToken);
        if (character is null)
        {
            return $"no character named {args[1]}";
        }

        await RequireOperatorStore().SetAccountStatusAsync(
            character.AccountId,
            OperatorControls.AccountStatusNormal,
            cancellationToken);
        await LogOperatorAsync(
            "unban",
            character.CharacterName,
            $"account={character.AccountId} username={character.Username}",
            cancellationToken);
        return $"account {character.Username} ({character.CharacterName}) can log in again";
    }

    private static async Task TryNotifyAsync(
        GameSessionContext target,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await target.Session.SendAsync(
                PacketBuilder.ServerNote(message),
                cancellationToken,
                "OperatorNotice");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[operator] notice to {target.DisplayName} failed: {error.Message}");
        }
    }

    /// <summary>
    /// Chat from a muted character is dropped; the speaker is told why.
    /// Commands are handled before chat, so a muted GM keeps his commands.
    /// </summary>
    private async Task<bool> RejectMutedChatAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_operatorMuteLoaded && OperatorControls.Store is { } store)
        {
            _operatorMuteLoaded = true;
            try
            {
                var stored = await store.FindCharacterAsync(_character.Name, cancellationToken);
                if (stored is { MuteUntilUnixSeconds: > 0 } &&
                    stored.CharacterId == _character.Id)
                {
                    OperatorControls.SetMutedUntil(
                        _character.Id,
                        DateTimeOffset.FromUnixTimeSeconds(stored.MuteUntilUnixSeconds));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[operator] mute read failed character={_character.Name}: {error.Message}");
            }
        }

        if (!OperatorControls.TryGetMutedUntil(_character.Id, now, out var until))
        {
            return false;
        }

        var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
        Console.WriteLine(
            $"[operator] muted chat dropped character={_character.Name} minutes-left={minutes}");
        await _session.SendAsync(
            PacketBuilder.ServerNote($"You are muted for {minutes} more minutes."),
            cancellationToken,
            "OperatorMutedNotice");
        return true;
    }

    // ---- teleport ------------------------------------------------------------

    private async Task<string> OperatorGotoAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2)
        {
            return "usage: /goto <name>";
        }

        var target = _registry.FindOnlineCharacter(args[1]);
        if (target is null)
        {
            return $"{args[1]} is not online";
        }

        if (_registry.DescribeWorldInstance(target) is { Kind: Godswar.Server.Domain.World.Instances.InstanceKind.Dungeon } run)
        {
            return await OperatorEnterRunAsync(target, run, cancellationToken);
        }

        var where = target.Character;
        var result = await OperatorTeleportSelfAsync(
            checked((byte)where.CurrentMap),
            where.PositionX,
            where.PositionZ,
            cancellationToken);
        await LogOperatorAsync(
            "goto",
            target.CharacterName,
            $"map={where.CurrentMap} x={where.PositionX:F1} z={where.PositionZ:F1} result={result}",
            cancellationToken);
        return $"to {target.CharacterName}: {result}";
    }

    private async Task<string> OperatorSummonAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2)
        {
            return "usage: /summon <name>";
        }

        var target = _registry.FindOnlineCharacter(args[1]);
        if (target is null)
        {
            return $"{args[1]} is not online";
        }

        if (target.Session == _session)
        {
            return "you are already here";
        }

        var result = await _registry.RunOperatorActionAsync(
            target.Session,
            new OperatorTeleportAction(
                _character!.Name,
                checked((byte)_character.CurrentMap),
                _character.PositionX,
                _character.PositionZ),
            cancellationToken);
        await LogOperatorAsync(
            "summon",
            target.CharacterName,
            $"map={_character.CurrentMap} x={_character.PositionX:F1} " +
            $"z={_character.PositionZ:F1} result={result}",
            cancellationToken);
        return $"{target.CharacterName}: {result}";
    }

    private async Task<string> OperatorMoveAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        if (args.Length != 4 ||
            !byte.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var map) ||
            !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return "usage: /move <map> <x> <z> [@name]   (see /where for numbers)";
        }

        string result;
        if (target is null)
        {
            result = await OperatorTeleportSelfAsync(map, x, z, cancellationToken);
        }
        else if (_registry.FindOnlineCharacter(target) is { } online)
        {
            result = online.Session == _session
                ? await OperatorTeleportSelfAsync(map, x, z, cancellationToken)
                : await _registry.RunOperatorActionAsync(
                    online.Session,
                    new OperatorTeleportAction(_character!.Name, map, x, z),
                    cancellationToken);
        }
        else
        {
            // Offline: write the position the character will log in at.
            var stored = await RequireOperatorStore().FindCharacterAsync(target, cancellationToken);
            result = stored is null
                ? $"no character named {target}"
                : await RequireOperatorStore().MoveOfflineCharacterAsync(
                    stored.CharacterId, map, x, z, cancellationToken)
                    ? "offline: will log in there"
                    : "offline: not moved";
        }

        await LogOperatorAsync(
            "move",
            target ?? _character!.Name,
            $"map={map} x={x:F1} z={z:F1} result={result}",
            cancellationToken);
        return result;
    }

    private string OperatorWhere(string[] args)
    {
        if (args.Length == 1)
        {
            return $"you: map {_character!.CurrentMap} x {_character.PositionX:F1} z {_character.PositionZ:F1}";
        }

        var target = _registry.FindOnlineCharacter(args[1]);
        return target is null
            ? $"{args[1]} is not online"
            : $"{target.CharacterName}: map {target.Character.CurrentMap} " +
              $"x {target.Character.PositionX:F1} z {target.Character.PositionZ:F1} " +
              $"{_registry.DescribeWorldInstance(target)?.Label}";
    }

    /// <summary>Every running world instance that has a player in it, and who.</summary>
    private string OperatorInstances()
    {
        var lines = _registry.SnapshotOccupiedWorldInstances()
            .OrderBy(static instance => instance.Kind)
            .ThenBy(static instance => instance.MapId)
            .Select(static instance =>
                $"{instance.Label}: {string.Join(", ", instance.Players)}")
            .ToArray();
        return lines.Length == 0 ? "no players in the world" : string.Join('\n', lines);
    }

    private async Task<string> OperatorTeleportSelfAsync(
        byte mapId,
        float x,
        float z,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return "not in the world";
        }

        var outcome = _character.CurrentMap == mapId
            ? await TryBeginSameMapSceneTransitionAsync(
                x,
                z,
                "operator-teleport",
                continuationGuard: null,
                cancellationToken)
            : await TryBeginMapTransitionAsync(
                mapId,
                x,
                z,
                "operator-teleport",
                cancellationToken);
        return outcome switch
        {
            SceneTransitionOutcome.CommittedAwaitingReadiness => "moved",
            SceneTransitionOutcome.CommittedRequiresReconnect => "moved (reconnect needed)",
            _ => "refused (map unknown, instance map, or a transfer already running)"
        };
    }

    // ---- the GM's own powers -------------------------------------------------

    private async Task<string> OperatorTogglePowerAsync(
        string word,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 2 || args[1] is not ("on" or "off" or "ON" or "OFF" or "On" or "Off"))
        {
            return $"usage: {word} on|off";
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        var powers = OperatorControls.UpdatePowers(_character!.Id, current => word switch
        {
            "/god" => current with { Invulnerable = on },
            "/bufflock" => current with { BuffLock = on },
            _ => current with { PetCareLock = on }
        });
        await PublishOperatorStatusAsync(cancellationToken);
        await LogOperatorAsync(word.TrimStart('/'), _character.Name, on ? "on" : "off", cancellationToken);
        return DescribePowers(powers);
    }

    /// <summary>
    /// Wall walking is client-side: the server has no player collision, so this
    /// only tells a patched client (Q20-Client/patch_gm_walls.py) to stop asking
    /// its block map. An unpatched client ignores the message.
    /// </summary>
    private async Task<string> OperatorWallsAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 2 || args[1].ToLowerInvariant() is not ("on" or "off"))
        {
            return "usage: /walls on|off";
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        _gmWallWalk = on;
        await SyncGmClientFlagsAsync(force: true, cancellationToken);
        await LogOperatorAsync("walls", _character!.Name, on ? "on" : "off", cancellationToken);
        return on
            ? "Walk through walls: ON, until /walls off or the client closes (client 1.4.2+)."
            : "Walk through walls: OFF. Walls stop you again.";
    }

    private async Task<string> OperatorNumberPowerAsync(
        string word,
        string[] args,
        CancellationToken cancellationToken)
    {
        var speedTarget = word == "/speed" ? TakeTarget(ref args) : null;
        if (args.Length != 2)
        {
            return $"usage: {word} <value>|off";
        }

        var off = args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
        float value = 0;
        if (!off &&
            !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return $"usage: {word} <value>|off";
        }

        OperatorPowers powers;
        switch (word)
        {
            case "/speed":
                if (!off && (!float.IsFinite(value) || value <= 0 || (!_developerCommands.UseCharacterGmFlags && value is < 0.5f or > 10f)))
                {
                    return _developerCommands.UseCharacterGmFlags ? "speed must be a finite positive number" : "speed must be 0.5 to 10 (1 = normal)";
                }

                if (speedTarget is not null)
                {
                    return await OperatorSpeedForAsync(speedTarget, off, value, cancellationToken);
                }

                powers = OperatorControls.UpdatePowers(_character!.Id, current =>
                    current with { SpeedFactor = off ? 1f : value });
                break;
            case "/power":
                if (!off && (!int.TryParse(args[1], out var _) || value < 1 || (!_developerCommands.UseCharacterGmFlags && value > 1000)))
                {
                    return _developerCommands.UseCharacterGmFlags ? "power must be a positive 32-bit integer" : "power must be 1 to 1000 (attack multiplier)";
                }

                powers = OperatorControls.UpdatePowers(_character!.Id, current =>
                    current with { AttackMultiplier = off ? 1 : int.Parse(args[1], CultureInfo.InvariantCulture) });
                break;
            default:
                if (!off && (!int.TryParse(args[1], out var _) || value < 0 || (!_developerCommands.UseCharacterGmFlags && value > 1_000_000)))
                {
                    return _developerCommands.UseCharacterGmFlags ? "armor must be a nonnegative 32-bit integer" : "armor must be 0 to 1,000,000 (added defence)";
                }

                powers = OperatorControls.UpdatePowers(_character!.Id, current =>
                    current with { DefenseBonus = off ? 0 : int.Parse(args[1], CultureInfo.InvariantCulture) });
                break;
        }

        await PublishOperatorStatusAsync(cancellationToken);
        await LogOperatorAsync(word.TrimStart('/'), _character.Name, off ? "off" : $"{value}", cancellationToken);
        return DescribePowers(powers);
    }

    private async Task<string> OperatorPowersAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 2 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            OperatorControls.UpdatePowers(_character!.Id, _ => OperatorPowers.None);
            await PublishOperatorStatusAsync(cancellationToken);
            await LogOperatorAsync("powers", _character.Name, "all off", cancellationToken);
        }

        return DescribePowers(OperatorControls.PowersOf(_character!.Id));
    }

    // the admin 2026-11-08: the GM window should be easy to understand, so the powers line is plain English (ASCII only:
    // the client's window font has no other characters).
    internal static string DescribePowers(OperatorPowers powers) =>
        $"God mode {(powers.Invulnerable ? "ON" : "off")}, " +
        $"speed {(powers.SpeedFactor == 1 ? "normal" : $"x{powers.SpeedFactor:0.##}")}, " +
        $"attack {(powers.AttackMultiplier == 1 ? "normal" : $"x{powers.AttackMultiplier}")}, " +
        $"armour {(powers.DefenseBonus == 0 ? "normal" : $"+{powers.DefenseBonus:N0}")}, " +
        $"buff timers {(powers.BuffLock ? "LOCKED" : "normal")}, " +
        $"pet care {(powers.PetCareLock ? "LOCKED" : "normal")}.";

    private async Task<string> RefreshOperatorStatusAsync(CancellationToken cancellationToken)
    {
        await PublishOperatorStatusAsync(cancellationToken);
        return "status sent";
    }

    /// <summary>/speed x @name: one online player's speed, until the server restarts.</summary>
    private async Task<string> OperatorSpeedForAsync(
        string targetName,
        bool off,
        float value,
        CancellationToken cancellationToken)
    {
        var target = _registry.FindOnlineCharacter(targetName);
        if (target is null)
        {
            return $"{targetName} is not online";
        }

        OperatorControls.UpdatePowers(target.CharacterId, current =>
            current with { SpeedFactor = off ? 1f : value });
        var result = await _registry.RunOperatorActionAsync(
            target.Session,
            new OperatorStatusRefreshAction(_character!.Name),
            cancellationToken);
        await LogOperatorAsync("speed", target.CharacterName, off ? "off" : $"{value}", cancellationToken);
        return $"{target.CharacterName}: speed x{(off ? 1f : value):0.##} ({result})";
    }

    /// <summary>Re-sends 10166 so the client's movement speed follows /speed.</summary>
    private async Task PublishOperatorStatusAsync(CancellationToken cancellationToken)
    {
        if (_character is null || !_worldPresenceAnnounced)
        {
            return;
        }

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "OperatorStatusRefresh");
        // finalise-gm: a patched client applies the 10166 speed on foot only while byte 1 of 10999 is set.
        await SyncGmClientFlagsAsync(force: false, cancellationToken);
    }

    // ---- buffs -----------------------------------------------------------------

    // FINALISE2 (F-103): /buff knows every row of the operator catalogue (helpful and hostile, 181 rows), not only
    // the 25 class buffs. "/buff <skill id>" keeps its exact meaning (1.4.1 GM windows send it); a status is chosen
    // with "/buff s<status id>" or "/buff status <status id>", because skill and status numbers overlap.
    internal const int BuffListPageRows = 15;

    internal static string BuffListPage(bool hostile, int page)
    {
        var rows = OperatorBuffCatalog.Listed.Where(entry => entry.Hostile == hostile).ToArray();
        var pages = Math.Max(1, (rows.Length + BuffListPageRows - 1) / BuffListPageRows);
        page = Math.Clamp(page, 1, pages);
        var kind = hostile ? "hostile" : "helpful";
        var lines = new List<string>
        {
            $"{rows.Length} {kind} buffs, page {page} of {pages}. /buff list {(hostile ? "hostile " : "")}<page>, " +
            $"/buff list {(hostile ? "" : "hostile ")}1, /buff info s<id>, /buff s<id> [minutes] [@name]"
        };
        foreach (var entry in rows.Skip((page - 1) * BuffListPageRows).Take(BuffListPageRows))
        {
            var definition = entry.Definition;
            var skill = entry.Source == OperatorBuffCatalog.SourceClassSkill
                ? string.Create(CultureInfo.InvariantCulture, $" (skill {definition.SkillId})")
                : "";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"s{definition.StatusId} {entry.Name}{skill}, {BuffDurationText(definition.Duration)}"));
        }
        return string.Join('\n', lines);
    }

    internal static string BuffName(uint statusId) =>
        OperatorBuffCatalog.TryGetByStatus(statusId, out var entry) && entry.Name.Length > 0 ? entry.Name : $"status {statusId}";

    internal static string BuffDurationText(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0.#} min")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0} s");

    /// <summary>FINALISE2: "s160" / "status 160" -> a status row; a bare number -> the class buff of that skill.</summary>
    internal static bool TryResolveOperatorBuff(ref string[] args, out OperatorBuffCatalog.Entry entry)
    {
        entry = default;
        if (args.Length >= 3 && args[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            args = [args[0], "s" + args[2], .. args[3..]];
        }

        if (args.Length < 2)
        {
            return false;
        }

        var word = args[1];
        if (word.Length > 1 && (word[0] is 's' or 'S') &&
            uint.TryParse(word.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var statusId))
        {
            return OperatorBuffCatalog.TryGetByStatus(statusId, out entry);
        }

        return int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var skillId) &&
            OperatorBuffCatalog.TryGetBySkill(skillId, out entry);
    }

    private async Task<string> OperatorBuffAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        if (args.Length >= 2 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var hostile = args.Length >= 3 && args[2].Equals("hostile", StringComparison.OrdinalIgnoreCase);
            var pageWord = args.Length > (hostile ? 3 : 2) ? args[hostile ? 3 : 2] : "1";
            return int.TryParse(pageWord, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                ? BuffListPage(hostile, page)
                : "usage: /buff list [hostile] [page]";
        }

        if (args.Length == 3 && args[1].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            var infoArgs = new[] { args[0], args[2] };
            return TryResolveOperatorBuff(ref infoArgs, out var info)
                ? string.Create(CultureInfo.InvariantCulture,
                    $"s{info.Definition.StatusId} {info.Name} ({(info.Hostile ? "hostile" : "helpful")}, " +
                    $"{BuffDurationText(info.Definition.Duration)}): {info.Note}")
                : "no such buff; /buff list shows them";
        }

        if (!TryResolveOperatorBuff(ref args, out var resolved) || args.Length > 3)
        {
            return "usage: /buff list [hostile] [page] | /buff info s<status> | " +
                "/buff <skill id>|s<status id> [minutes 1-71582788] [@name]";
        }

        var definition = resolved.Definition;
        if (args.Length == 3)
        {
            if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
                minutes < 1 || minutes > uint.MaxValue / 60)
            {
                return "minutes must fit the native uint32-second duration (1 to 71582788)";
            }

            definition = definition with { Duration = TimeSpan.FromMinutes(minutes) };
        }

        string result;
        string targetName;
        if (target is null || string.Equals(target, _character!.Name, StringComparison.OrdinalIgnoreCase))
        {
            targetName = _character!.Name;
            result = await ApplyOperatorStatusAsync(definition, cancellationToken);
        }
        else if (_registry.FindOnlineCharacter(target) is { } online)
        {
            targetName = online.CharacterName;
            result = await _registry.RunOperatorActionAsync(
                online.Session,
                new OperatorStatusAction(_character.Name, definition),
                cancellationToken);
        }
        else
        {
            return $"{target} is not online";
        }

        await LogOperatorAsync(
            "buff",
            targetName,
            $"skill={definition.SkillId} status={definition.StatusId} hostile={!definition.Beneficial} " + // FINALISE2
            $"minutes={definition.Duration.TotalMinutes:0} result={result}",
            cancellationToken);
        return $"{targetName}: {result}";
    }

    private async Task<string> ApplyOperatorStatusAsync(
        SkillStatusEffectDefinition definition,
        CancellationToken cancellationToken) =>
        await _registry.ApplyRuntimeStatusAndPublishAsync(
            _session,
            definition,
            DateTimeOffset.UtcNow,
            "operator-buff",
            cancellationToken)
            // FINALISE2 (the admin F-85: names, not ids): "Apollo's Shield Lv. 1 applied for 10 min"; the id stays in the log.
            ? $"{BuffName(definition.StatusId)} applied for {BuffDurationText(definition.Duration)}"
            : "not applied (a stronger buff of that kind is active)";

    // ---- items ---------------------------------------------------------------

    private async Task<(int CharacterId, string Name, GameSessionContext? Online)?> ResolveItemTargetAsync(
        string? target,
        CancellationToken cancellationToken)
    {
        if (target is null || string.Equals(target, _character!.Name, StringComparison.OrdinalIgnoreCase))
        {
            return (_character!.Id, _character.Name, null);
        }

        var online = _registry.FindOnlineCharacter(target);
        if (online is not null)
        {
            return (online.CharacterId, online.CharacterName, online);
        }

        var stored = await RequireOperatorStore().FindCharacterAsync(target, cancellationToken);
        return stored is null ? null : (stored.CharacterId, stored.CharacterName, null);
    }

    /// <summary>After a database change: reload the owner's bag on his own handler.</summary>
    private async Task<string> RefreshAfterOperatorItemChangeAsync(
        (int CharacterId, string Name, GameSessionContext? Online) target,
        string reason,
        CancellationToken cancellationToken)
    {
        if (target.CharacterId == _character!.Id)
        {
            return await ReloadAfterOperatorChangeAsync(reason, cancellationToken);
        }

        return target.Online is null
            ? "offline: applies at next login"
            : await _registry.RunOperatorActionAsync(
                target.Online.Session,
                new OperatorReloadAction(_character.Name, reason),
                cancellationToken);
    }

    private async Task<string> OperatorGiveAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        if (args.Length is < 2 or > 4 ||
            !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
        {
            return "usage: /give <item id> [quantity 1-9999] [bound 0|1] [@name]";
        }

        var quantity = 1;
        if (args.Length >= 3 &&
            (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out quantity) ||
             quantity is < 1 or > 9999))
        {
            return "quantity must be 1 to 9999";
        }

        short bound = 0;
        if (args.Length == 4 && args[3] is not ("0" or "1"))
        {
            return "bound must be 0 (tradable) or 1 (bound)";
        }

        if (args.Length == 4)
        {
            bound = args[3] == "1" ? (short)1 : (short)0;
        }

        if (!RequireItemContent().Templates.TryGet(itemId, out var template))
        {
            return $"item {itemId} does not exist on this server";
        }

        // A packed Seal Jade with no linked pet stops its owner entering the world (PacketBuilder.ItemSerialization).
        if (itemId == PetItemCatalog.PackedSealJade)
        {
            return "a packed Seal Jade only comes from sealing a pet";
        }

        var stackCap = ReadItemStackCap(template.StatsJson);
        var resolved = await ResolveItemTargetAsync(target, cancellationToken);
        if (resolved is null)
        {
            return $"no character named {target}";
        }

        var result = await RequireOperatorStore().GrantItemAsync(
            resolved.Value.CharacterId,
            itemId,
            quantity,
            stackCap,
            bound,
            cancellationToken);
        var refresh = result.Outcome == OperatorItemOutcome.Done
            ? await RefreshAfterOperatorItemChangeAsync(resolved.Value, "give", cancellationToken)
            : string.Empty;
        await LogOperatorAsync(
            "give",
            resolved.Value.Name,
            $"item={itemId} name=\"{template.DisplayName}\" quantity={quantity} bound={bound} " +
            $"outcome={result.Outcome} {result.Detail} refresh={refresh}",
            cancellationToken);
        return result.Outcome == OperatorItemOutcome.Done
            ? $"{quantity} x {template.DisplayName} to {resolved.Value.Name}, {result.Detail}" // FINALISE2 (F-85): no item number
            : $"not given: {result.Detail}";
    }

    internal static short ReadItemStackCapForOperator(string statsJson) =>
        ReadItemStackCap(statsJson);

    /// <summary>
    /// /admin edit on this (online) character: the database write, then a
    /// reload from the database and a full client refresh, so the change is
    /// both durable and visible without a relog.
    /// </summary>
    private async Task<string> RunOperatorAdminEditAsync(
        OperatorAdminEditAction action,
        CancellationToken cancellationToken)
    {
        var result = await action.Edit(_character!.Id, cancellationToken);
        if (result.Outcome != OperatorItemOutcome.Done)
        {
            return "error: " + result.Detail;
        }

        var reload = await ReloadAfterOperatorChangeAsync("admin", cancellationToken);
        if (_character is null || reload != "bag refreshed")
        {
            return "error: changed in the database, but the reload failed (" + reload + ")";
        }

        if (action.AdvanceWorld)
        {
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: true);
        }

        var detail = PacketBuilder.PlayerDetail(_character);
        if (detail.Length > 0)
        {
            await _session.SendAsync(detail, cancellationToken, "AdminPlayerDetail", framed: false);
        }

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "AdminPlayerStatus");
        if (action.ZodiacChanged)
        {
            await SendZodiacFullSyncAsync(cancellationToken);
        }

        if (action.QuestsChanged)
        {
            // ROUND-QUESTRESET: only completed history changed (carried quests are refused), so the
            // quest log needs no resend; the npc marks follow the new history.
            await SendQuestNpcMarksAsync(cancellationToken);
        }

        return result.Detail;
    }

    private static short ReadItemStackCap(string statsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (document.RootElement.TryGetProperty("Overlap", out var overlap) &&
                short.TryParse(
                    overlap.ValueKind == JsonValueKind.String ? overlap.GetString() : overlap.GetRawText(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var cap) &&
                cap >= 1)
            {
                // The wire carries the stack in one byte.
                return Math.Min(cap, (short)255);
            }
        }
        catch (JsonException)
        {
        }

        return 1;
    }

    private async Task<string> OperatorBagAsync(string[] args, CancellationToken cancellationToken)
    {
        var resolved = await ResolveItemTargetAsync(args.Length > 1 ? args[1].TrimStart('@') : null, cancellationToken);
        if (resolved is null)
        {
            return $"no character named {args[1]}";
        }

        var items = await RequireOperatorStore().ReadBagAsync(resolved.Value.CharacterId, cancellationToken);
        if (items.Count == 0)
        {
            return $"{resolved.Value.Name}'s bag is empty";
        }

        var templates = RequireItemContent().Templates;
        return $"{resolved.Value.Name}'s bag ({items.Count} items):\n" + string.Join(
            '\n',
            items.Select(item =>
            {
                // FINALISE2 (F-85): the item's name, not its number (unknown items keep the number so they can be found).
                var name = templates.TryGet(checked((uint)item.ItemId), out var template) && !string.IsNullOrWhiteSpace(template.DisplayName)
                    ? template.DisplayName
                    : $"unknown item {item.ItemId}";
                return $"slot {item.Slot}: {name} x{item.Stack} " +
                    $"Q{item.Quality} G{item.Grade} {(item.Bound == 1 ? "bound" : "tradable")}" +
                    (item.ItemExperience > 0 ? $" exp {item.ItemExperience}" : string.Empty) +
                    (item.SealedPetId is not null ? " holds a sealed pet" : string.Empty); // FINALISE2: no pet id
            }));
    }

    private async Task<string> OperatorItemFieldAsync(
        string word,
        string[] args,
        CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        OperatorItemField field;
        int value;
        short slot;
        if (word == "/bind")
        {
            if (args.Length != 3 ||
                !short.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out slot) ||
                args[2].ToLowerInvariant() is not ("on" or "off"))
            {
                return "usage: /bind <slot> on|off [@name]   (off = tradable)";
            }

            field = OperatorItemField.Bound;
            value = args[2].Equals("on", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        else
        {
            if (args.Length != 4 ||
                !short.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out slot) ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return "usage: /itemset <slot> quality|grade|stack|exp <value> [@name]";
            }

            switch (args[2].ToLowerInvariant())
            {
                case "quality":
                    field = OperatorItemField.Quality;
                    break;
                case "grade":
                    field = OperatorItemField.Grade;
                    break;
                case "stack":
                    field = OperatorItemField.Stack;
                    break;
                case "exp":
                    field = OperatorItemField.ItemExperience;
                    break;
                default:
                    return "field must be quality, grade, stack or exp";
            }
        }

        var resolved = await ResolveItemTargetAsync(target, cancellationToken);
        if (resolved is null)
        {
            return $"no character named {target}";
        }

        var result = await RequireOperatorStore().SetBagItemFieldAsync(
            resolved.Value.CharacterId,
            slot,
            field,
            value,
            cancellationToken);
        var refresh = result.Outcome == OperatorItemOutcome.Done
            ? await RefreshAfterOperatorItemChangeAsync(resolved.Value, word.TrimStart('/'), cancellationToken)
            : string.Empty;
        await LogOperatorAsync(
            word.TrimStart('/'),
            resolved.Value.Name,
            $"slot={slot} field={field} value={value} outcome={result.Outcome} {result.Detail} refresh={refresh}",
            cancellationToken);
        return result.Outcome == OperatorItemOutcome.Done
            ? $"{resolved.Value.Name}: {result.Detail}"
            : $"not changed: {result.Detail}";
    }

    private async Task<string> OperatorDuplicateAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        if (args.Length != 2 ||
            !short.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var slot))
        {
            return "usage: /dupe <slot> [@name]   (see /bag for slot numbers)";
        }

        var resolved = await ResolveItemTargetAsync(target, cancellationToken);
        if (resolved is null)
        {
            return $"no character named {target}";
        }

        var result = await RequireOperatorStore().DuplicateBagItemAsync(
            resolved.Value.CharacterId,
            slot,
            cancellationToken);
        var refresh = result.Outcome == OperatorItemOutcome.Done
            ? await RefreshAfterOperatorItemChangeAsync(resolved.Value, "dupe", cancellationToken)
            : string.Empty;
        await LogOperatorAsync(
            "dupe",
            resolved.Value.Name,
            $"slot={slot} outcome={result.Outcome} {result.Detail} refresh={refresh}",
            cancellationToken);
        return result.Outcome == OperatorItemOutcome.Done
            ? $"{resolved.Value.Name}: copied {result.Detail}"
            : $"not copied: {result.Detail}";
    }

    // ---- progression on another character ------------------------------------

    private async Task RunProgressionOnOtherCharacterAsync(
        GamePacket packet,
        DeveloperProgressionCommand command,
        string targetName,
        CancellationToken cancellationToken)
    {
        string result;
        if (string.Equals(targetName, _character!.Name, StringComparison.OrdinalIgnoreCase))
        {
            result = await ExecuteDeveloperProgressionCommandAsync(packet, command, cancellationToken);
        }
        else if (_registry.FindOnlineCharacter(targetName) is { } online)
        {
            result = await _registry.RunOperatorActionAsync(
                online.Session,
                new OperatorProgressionAction(_character.Name, packet, command),
                cancellationToken);
            targetName = online.CharacterName;
        }
        else
        {
            result = "not online (the progression commands need the character in the world)";
        }

        await LogOperatorAsync(
            "progression",
            targetName,
            $"operation={command.Operation} value={command.Value} result={result}",
            cancellationToken);
        await SendDeveloperItemFeedbackAsync(
            packet,
            $"[progression] {targetName}: {result}",
            cancellationToken);
    }

    // ---- the sink: actions other sessions ask this handler to run ------------

    private async Task<string> HandleOperatorSessionActionAsync(
        OperatorSessionAction action,
        CancellationToken cancellationToken)
    {
        await _characterStateGate.WaitAsync(cancellationToken);
        try
        {
            if (_character is null || _account is null)
            {
                return "not in the world";
            }

            var result = action switch
            {
                OperatorTeleportAction teleport => await OperatorTeleportSelfAsync(
                    teleport.MapId,
                    teleport.X,
                    teleport.Z,
                    cancellationToken),
                OperatorReloadAction reload => await ReloadAfterOperatorChangeAsync(
                    reload.Reason,
                    cancellationToken),
                OperatorProgressionAction progression => await ExecuteDeveloperProgressionCommandAsync(
                    progression.CommandPacket,
                    progression.Command,
                    cancellationToken),
                OperatorStatusRefreshAction => await RefreshOperatorStatusAsync(cancellationToken),
                OperatorStatusAction status => await ApplyOperatorStatusAsync(
                    status.Definition,
                    cancellationToken),
                OperatorApplyBuffAction buff => await ApplyOperatorBuffForAdminAsync(
                    buff.Definition,
                    cancellationToken),
                OperatorInspectBuffAction => await InspectOperatorBuffsAsync(cancellationToken),
                PlayerTradeReloadAction trade => await ReloadAfterPlayerTradeAsync(trade.Reason, cancellationToken), // TRADE
                OperatorRemoveBuffAction removal => await RemoveOperatorBuffAsync(removal.Token, cancellationToken),
                OperatorAdminEditAction adminEdit => await RunOperatorAdminEditAsync(
                    adminEdit,
                    cancellationToken),
                OperatorMarriageRefreshAction => await ApplyMarriageRefreshAsync(cancellationToken), // MARRIAGE
                PetDispatchDueAction => await CompleteDuePetDispatchesAsync(cancellationToken), // W3 Magic Bell
                _ => "unknown action"
            };
            Console.WriteLine(
                $"[operator] applied {action.GetType().Name} by={action.ActorName} " +
                $"to={_character.Name} result={result}");
            return result;
        }
        finally
        {
            _characterStateGate.Release();
        }
    }

    private async Task<string> ReloadAfterOperatorChangeAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var previousKitBag = _character?.KitBag ?? string.Empty;
        if (!await RefreshCharacterSnapshotAsync("operator_" + reason, cancellationToken) ||
            _character is null)
        {
            return "reload failed";
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        foreach (var acknowledgement in PacketBuilder.KitBagMutationDeletionAcknowledgements(
                     previousKitBag,
                     _character.KitBag))
        {
            await _session.SendAsync(acknowledgement, cancellationToken, "OperatorKitBagDeleteAck");
        }

        await SendKitBagRefreshAsync(cancellationToken);
        return "bag refreshed";
    }
}
