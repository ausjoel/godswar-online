using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W14: accounts.login_status is set by the game session (character screen) and cleared by its end, not by the
// login server. A login that never reaches the game port therefore leaves no stale "online" flag.
internal static partial class CharacterSnapshotHandlerChecks
{
    private static async Task CheckGameSessionOwnsLoginFlagAsync()
    {
        const int accountId = 7;
        var store = new PresenceOrderStore(accountId);
        var registry = new GameSessionRegistry(store: null);
        var loginBytes =
            CreateLoginPacket("snapshot-user").Buffer.ToArray();
        new PacketCipher().Transform(loginBytes);
        var transport = new ScriptedLegacyByteTransport(loginBytes);
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            new CountingSnapshotReader(EmptySnapshot(accountId)),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess());

        try
        {
            await handler.RunAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A fault after the character screen still runs the teardown under test.
        }

        Check.True(
            store.Calls.SequenceEqual(["online", "offline"]),
            "W14: the game login sets the login flag once and its session end clears it " +
            $"(calls: {string.Join(",", store.Calls)})");
    }

    private static async Task CheckRefusedGameLoginLeavesNoLoginFlagAsync()
    {
        var store = new PresenceOrderStore(accountId: null);
        var registry = new GameSessionRegistry(store: null);
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            new CountingSnapshotReader(EmptySnapshot(7)),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess());

        await InvokePacketAsync(handler, CreateLoginPacket("unknown-user"));

        Check.Equal(
            0,
            store.Calls.Count,
            "W14: a refused game login never sets the login flag");
    }

    private sealed class PresenceOrderStore(int? accountId) :
        GameStoreTestStub
    {
        public List<string> Calls { get; } = [];

        public override Task<GameAccount?> FindAccountByUsernameAsync(
            string username,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameAccount?>(
                accountId is { } id
                    ? new GameAccount { Id = id, Username = username }
                    : null);

        public override Task MarkAccountOnlineAsync(
            int requestedAccountId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("online");
            return Task.CompletedTask;
        }

        public override Task MarkAccountOfflineAsync(
            int requestedAccountId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("offline");
            return Task.CompletedTask;
        }
    }
}
