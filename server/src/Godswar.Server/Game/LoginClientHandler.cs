using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Realms;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.Game;

internal sealed class LoginClientHandler : IClientHandler
{
    private readonly ClientSession _session;
    private readonly ServerOptions _options;
    private readonly IAccountAuthenticator _authentication;
    private readonly SecureGameTarget? _gameTarget;
    private readonly IGameTicketStore? _ticketStore;
    private readonly LegacyAuthenticationAccess?
        _legacyAuthenticationAccess;
    private readonly IRealmCatalogReader? _realmCatalog;
    private AccountIdentity? _authenticatedAccount;
    private RealmCatalogSnapshot? _advertisedRealms;
    private SecureLoginGeneration? _loginGeneration;
    private RealmCatalogEntry? _selectedRealm;
    private bool _grantCommitted;
    private bool _loginAttempted;
    private readonly TimeSpan _redirectPacing;

    /// <summary>
    /// How long SendServer (S2C 7) and the game-server redirect (S2C 10001) each
    /// wait after the client's request for them (C2S 4 / C2S 6).
    /// </summary>
    /// <remarks>
    /// The client keeps one pending module switch and applies it once per frame,
    /// after it has drained every queued message. ServerList's 10008 asks for the
    /// LOGIN module, and the LOGIN module's startup (0x467280) is the only place
    /// that loads QuestInfoUI.xml and QuestViewUI.xml (0x5d0180 / 0x5d9e20).
    /// The client answers 10008 and 7 by itself, so on a fast link (a localhost
    /// rig, a slow background-window frame) 7 and the 10001 redirect land in the
    /// same drain as 10008. The redirect's ADAPTER request then overwrites the
    /// pending LOGIN switch, the quest UI is never loaded for the life of the
    /// process, and the first 10090 at world entry faults at 0x005D1CC3 on the
    /// lazily created, uninitialised QuestInfoUI (same root cause as the
    /// QuestView crash at 0x00493A4E). Holding 7 back lets the client finish
    /// the 10008 frame and start the LOGIN module first - the order a remote
    /// client sees (GAMESERVER_INFO, startup LOGIN, LINESERVER_INFO,
    /// GAMESERVER_READY) - and holding 10001 back is a second gate should one
    /// frame still swallow both. Over the Internet the round trip already gave
    /// these gaps; a login is about one second slower in total.
    /// </remarks>
    internal static readonly TimeSpan DefaultRedirectPacing =
        TimeSpan.FromMilliseconds(500);

    public LoginClientHandler(
        ClientSession session,
        ServerOptions options,
        IAccountAuthenticator authentication,
        IGameTicketStore? ticketStore = null,
        SecureGameTarget? gameTarget = null,
        LegacyAuthenticationAccess?
            legacyAuthenticationAccess = null,
        IRealmCatalogReader? realmCatalog = null,
        TimeSpan? redirectPacing = null)
    {
        _redirectPacing = redirectPacing ?? DefaultRedirectPacing;
        if (_redirectPacing < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(redirectPacing));
        }
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _authentication = authentication ??
            throw new ArgumentNullException(nameof(authentication));
        if ((ticketStore is null) != (gameTarget is null))
        {
            throw new ArgumentException(
                "Secure login tickets and game target must be configured together.");
        }
        _ticketStore = ticketStore;
        _gameTarget = gameTarget;
        _legacyAuthenticationAccess =
            legacyAuthenticationAccess;
        _realmCatalog = realmCatalog;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _session.ReadPacketAsync(
                    cancellationToken);
                if (packet is null)
                {
                    return;
                }

                using var activity = ServerActivity.StartPacket(
                    ServerTraceOperation.LoginPacket,
                    "login",
                    _session.IsSecure ? "tls" : "raw_tcp");
                try
                {
                    await HandlePacketAsync(packet, cancellationToken);
                    activity.Complete(
                        ServerTraceOutcome.Accepted);
                }
                catch (OperationCanceledException)
                {
                    activity.Complete(
                        ServerTraceOutcome.Cancelled);
                    throw;
                }
                catch
                {
                    activity.Complete(
                        ServerTraceOutcome.Faulted);
                    throw;
                }
            }
        }
        finally
        {
            if (!_grantCommitted &&
                _loginGeneration is not null &&
                _ticketStore is not null)
            {
                await _ticketStore.RevokeGenerationAsync(
                    _loginGeneration,
                    SecureTicketOperationDeadline.Default,
                    CancellationToken.None);
            }
        }
    }

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_session.AllowsPayloadDiagnostics)
        {
            LogReceived(packet);
        }

        switch (packet.Opcode)
        {
            case Opcodes.Login:
                await HandleLoginAsync(packet, cancellationToken);
                break;
            case Opcodes.SelectServer:
                await HandleServerSelectionAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.LoginReturnInfo:
                await HandleGameRedirectAsync(cancellationToken);
                break;
            default:
                if (_session.AllowsPayloadDiagnostics)
                {
                    Console.WriteLine(
                        $"[login] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                }
                break;
        }
    }

    private async Task HandleLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        try
        {
            if (_loginAttempted)
            {
                _session.Disconnect();
                return;
            }
            _loginAttempted = true;

            var payload = packet.Payload;
            if (payload.Length < 64)
            {
                await SendGenericFailureAsync(cancellationToken);
                return;
            }

            // ROUND3: the Origin login carries its uint32 build at payload +132.
            // Reject missing/old stamps before credential work or issuing a game grant.
            if (!ClientBuildPolicy.Allows(packet, _options.MinimumClientBuild))
            {
                Console.Error.WriteLine("[login] please update client: minimum build=" + _options.MinimumClientBuild);
                await SendGenericFailureAsync(cancellationToken);
                _session.Disconnect();
                return;
            }

            var rawUsername = PacketText.ReadFixedAscii(payload, 0, 32);
            if (string.IsNullOrWhiteSpace(rawUsername))
            {
                await SendGenericFailureAsync(cancellationToken);
                return;
            }
            var username = PacketText.DecodeLoginName(rawUsername);
            if (!_session.IsSecure)
            {
                if (_legacyAuthenticationAccess is null)
                {
                    ServerProfileMetrics
                        .RecordLegacyAuthenticationAttempt(
                            "login",
                            "blocked");
                    Console.Error.WriteLine(
                        "[security] rejected legacy authentication " +
                        "endpoint=login reason=profile");
                    _session.Disconnect();
                    return;
                }

                ServerProfileMetrics
                    .RecordLegacyAuthenticationAttempt(
                        "login",
                        "allowed");
            }
            else if (_ticketStore is null ||
                     _gameTarget is null)
            {
                _session.Disconnect();
                return;
            }

            var passwordField = packet.Buffer.AsSpan(36, 32);
            var passwordBytes = _session.IsSecure
                ? CopyPasswordBytes(passwordField)
                : CopyLegacyPasswordBytes(passwordField);
            try
            {
                var result = await _authentication.AuthenticateAsync(
                    username,
                    passwordBytes,
                    cancellationToken);
                if (!result.IsAccepted)
                {
                    await SendGenericFailureAsync(cancellationToken);
                    return;
                }

                // ROUND2: a banned account (accounts.status <> 0) is refused
                // like a wrong password.
                if (await Application.Operators.OperatorControls.IsAccountBlockedAsync(
                        result.Account!.Id,
                        cancellationToken))
                {
                    Console.WriteLine(
                        $"[operator] login refused (banned) endpoint=login account={result.Account.Id}");
                    await SendGenericFailureAsync(cancellationToken);
                    return;
                }

                if (_session.IsSecure)
                {
                    var generation = await _ticketStore!.BeginLoginAsync(
                        result.Account!.Id,
                        result.Account.Username,
                        SecureTicketOperationDeadline.Default,
                        cancellationToken);
                    if (!generation.IsStarted)
                    {
                        await SendGenericFailureAsync(cancellationToken);
                        return;
                    }

                    _authenticatedAccount = result.Account;
                    _loginGeneration = generation.Generation;
                }
                else
                {
                    _authenticatedAccount = result.Account;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            _session.MarkAuthenticated();
            if (!_session.IsSecure &&
                _session.AllowsPayloadDiagnostics)
            {
                Console.WriteLine($"[login] accepted {username}");
            }
            await SendServerListAsync(cancellationToken);
        }
        finally
        {
            ClearAvailableCredentialField(packet.Buffer);
        }
    }

    private async Task HandleGameRedirectAsync(
        CancellationToken cancellationToken)
    {
        if (_authenticatedAccount is null)
        {
            _session.Disconnect();
            return;
        }

        if (_realmCatalog is not null && _selectedRealm is null)
        {
            _session.Disconnect();
            return;
        }

        // QUEST-UI: let the client apply its LOGIN module switch (which loads
        // the quest UI) before the redirect asks for ADAPTER. Waited before any
        // ticket is issued, so the secure grant's lease deadline is untouched.
        await PaceLoginReplyAsync(cancellationToken);

        if (!_session.IsSecure)
        {
            // LOGINFIX: the legacy game login on the game port carries no
            // password; admit it only from the address that just passed here.
            LegacyGameLoginHandoff.Issue(
                _authenticatedAccount.Id,
                _session.RemoteEndPoint);
            if (_selectedRealm is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.GameServerRedirect(_selectedRealm),
                    cancellationToken,
                    "GameServerRedirect");
                return;
            }

            await _session.SendAsync(
                PacketBuilder.GameServerRedirect(
                    _options.Game.PublicHost,
                    _options.Game.ResolvePublicPort()),
                cancellationToken,
                "GameServerRedirect");
            return;
        }

        if (_grantCommitted ||
            _loginGeneration is null ||
            _ticketStore is null ||
            _gameTarget is null ||
            _session.SecureConnectionContext is not { } context)
        {
            _session.Disconnect();
            return;
        }

        var issued = await _ticketStore.IssueAsync(
            _loginGeneration,
            context,
            _gameTarget,
            SecureTicketOperationDeadline.Default,
            cancellationToken);
        if (!issued.IsIssued)
        {
            _session.Disconnect();
            return;
        }

        await using var lease = issued.Lease!;
        await _session.SendGameGrantAsync(
            lease.Grant,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.GameServerRedirect(
                _gameTarget.RouteHost,
                _gameTarget.RoutePort),
            cancellationToken,
            "GameServerRedirect");
        if (!await lease.CommitAsync(
                SecureTicketOperationDeadline.Default,
                cancellationToken))
        {
            _session.Disconnect();
            throw new SecureTransportException(
                "The game grant expired or was invalidated after redirect.");
        }

        _grantCommitted = true;
    }

    private async Task SendServerListAsync(
        CancellationToken cancellationToken)
    {
        if (_realmCatalog is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerList(),
                cancellationToken,
                "ServerList");
            return;
        }

        if (_advertisedRealms is not null)
        {
            _session.Disconnect();
            return;
        }

        _advertisedRealms = await _realmCatalog.ReadEnabledAsync(
            cancellationToken);
        if (_advertisedRealms.Entries.IsEmpty)
        {
            _session.Disconnect();
            return;
        }

        await _session.SendAsync(
            PacketBuilder.ServerList(_advertisedRealms),
            cancellationToken,
            "ServerList");
    }

    private async Task HandleServerSelectionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_authenticatedAccount is null)
        {
            _session.Disconnect();
            return;
        }

        if (_realmCatalog is null)
        {
            await PaceLoginReplyAsync(cancellationToken); // QUEST-UI
            await _session.SendAsync(
                PacketBuilder.SendServer(),
                cancellationToken,
                "SendServer");
            return;
        }

        if (_advertisedRealms is null ||
            _selectedRealm is not null ||
            !LegacyRealmSelectionPacket.TryRead(
                packet,
                out var realmId) ||
            !_advertisedRealms.TryFind(
                realmId,
                out var advertised) ||
            advertised is null)
        {
            _session.Disconnect();
            return;
        }

        var currentRealms = await _realmCatalog.ReadEnabledAsync(
            cancellationToken);
        if (!currentRealms.TryFind(realmId, out var selected) ||
            selected is null ||
            selected != advertised ||
            (_session.IsSecure &&
             selected.RealmId !=
                _options.Game.WorldInstances.ProcessRealmId))
        {
            _session.Disconnect();
            return;
        }

        _selectedRealm = selected;
        // QUEST-UI: 7 must not land in the client frame that drains 10008.
        await PaceLoginReplyAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.SendServer(selected),
            cancellationToken,
            "SendServer");
    }

    private async Task PaceLoginReplyAsync(CancellationToken cancellationToken)
    {
        if (_redirectPacing > TimeSpan.Zero)
        {
            await Task.Delay(_redirectPacing, cancellationToken);
        }
    }

    private async Task SendGenericFailureAsync(
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.LoginFailed(3),
            cancellationToken,
            "LoginFailed");
    }

    private static byte[] CopyPasswordBytes(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        var length = terminator >= 0 ? terminator : field.Length;
        return field[..length].ToArray();
    }

    private static byte[] CopyLegacyPasswordBytes(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        while (!field.IsEmpty && IsLegacyTrimByte(field[0]))
        {
            field = field[1..];
        }
        while (!field.IsEmpty && IsLegacyTrimByte(field[^1]))
        {
            field = field[..^1];
        }
        return field.ToArray();
    }

    private static bool IsLegacyTrimByte(byte value) =>
        value is 0x20 or >= 0x09 and <= 0x0D;

    private static void ClearAvailableCredentialField(byte[] buffer)
    {
        const int credentialOffset = 36;
        if (buffer.Length <= credentialOffset)
        {
            return;
        }

        var length = Math.Min(
            AuthenticationOptions.MaximumPasswordBytes,
            buffer.Length - credentialOffset);
        CryptographicOperations.ZeroMemory(
            buffer.AsSpan(credentialOffset, length));
    }

    private static void LogReceived(GamePacket packet)
    {
        if (packet.Opcode == Opcodes.Login)
        {
            Console.WriteLine(
                $"[login] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length}");
            return;
        }

        Console.WriteLine(
            $"[login] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} hex={packet.ToHexPreview(32)}");
    }
}
