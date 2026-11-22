using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyAuthenticationProfileChecks
{
    public static async Task RunAsync()
    {
        await CheckRawLoginCapabilityAsync();
        await CheckRawGameBindingCapabilityAsync();
        await CheckSecureGameWithoutPrincipalAsync();
    }

    // ROUND3: exercise the actual handler, not only the build comparison helper.
    public static async Task RunClientBuildAsync()
    {
        const uint minimum = 0x3e9775;
        foreach (var (length, build, enabled, accepted) in new[]
        {
            (140, minimum - 1, true, false),
            (140, minimum, true, true),
            (140, minimum + 1, true, true),
            (139, minimum, true, false),
            (68, 0u, true, false),
            (68, 0u, false, true)
        })
        {
            var bytes = LoginPacket(Opcodes.Login, "test2", "password", length);
            if (length >= 140)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136, 4), build);
            }
            var authentication = new FixedPasswordAccountAuthenticator();
            var transport = new ScriptedLegacyByteTransport();
            await using var session = new ClientSession(transport, endpointRole: NetworkEndpointRole.Login);
            var options = LocalOptions();
            options.MinimumClientBuild = enabled ? minimum : 0;
            var handler = new LoginClientHandler(session, options, authentication,
                legacyAuthenticationAccess: LocalAccess(options));
            await InvokeAsync(handler, "HandleLoginAsync", new GamePacket(bytes));
            Check.Equal(accepted ? 1 : 0, authentication.Calls, "build gate precedes authentication");
            Check.Equal(accepted ? 0 : 1, transport.DisconnectCount, "old or missing build disconnects");
            Check.True(transport.WrittenBytes.AsSpan().SequenceEqual(
                Encrypt(accepted ? PacketBuilder.ServerList() : PacketBuilder.LoginFailed(3))),
                "rejected builds receive failure instead of a server list");
            Check.True(bytes.AsSpan(36, 32).IndexOfAnyExcept((byte)0) < 0,
                "credentials cleared even when build is rejected");
        }
    }

    // ROUND-CLIENTBUILD: the stamped build is read, kept on the journey (-> session_health.client_build) and logged,
    // with the gate off (the default) and on; plus the patch.vn label the log and the editor show.
    public static async Task RunClientBuildRecordingAsync()
    {
        Check.Equal("4108034 (2.54.017, release 17)", ClientBuildPolicy.Describe(4108034u), "stamped release label");
        Check.Equal("4108000 (2.54.000, unstamped)", ClientBuildPolicy.Describe(4108000u), "unstamped 1.5.6 label");
        Check.Equal("4102004 (2.51.002, stock 2.51.002)", ClientBuildPolicy.Describe(4102004u), "stock label");
        Check.Equal("7", ClientBuildPolicy.Describe(7u), "odd value is raw");
        Check.Equal("none", ClientBuildPolicy.Describe(null), "missing build");
        Check.True(ClientBuildPolicy.TryRead(new byte[136], out _), "136-byte payload carries a build");
        Check.True(!ClientBuildPolicy.TryRead(new byte[135], out var none) && none == 0, "135-byte payload has none");

        foreach (var (length, build, minimum, accepted) in new[]
        {
            (140, 4108034u, 0u, true),       // stamped release 17, gate off
            (140, 4108000u, 0u, true),       // every release up to 1.5.6, gate off: nothing changes for players
            (68, 0u, 0u, true),              // 2008-shaped login, gate off
            (140, 4108034u, 4108034u, true), // gate on at release 17
            (140, 4108000u, 4108034u, false) // gate on refuses an unstamped client, build still recorded
        })
        {
            var bytes = LoginPacket(Opcodes.Login, "test2", "password", length);
            if (length >= 140)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136, 4), build);
            }
            var authentication = new FixedPasswordAccountAuthenticator();
            var transport = new ScriptedLegacyByteTransport();
            await using var session = new ClientSession(transport, endpointRole: NetworkEndpointRole.Login);
            var options = LocalOptions();
            options.MinimumClientBuild = minimum;
            var handler = new LoginClientHandler(session, options, authentication,
                legacyAuthenticationAccess: LocalAccess(options));
            var log = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(log);
            try
            {
                await InvokeAsync(handler, "HandleLoginAsync", new GamePacket(bytes));
            }
            finally
            {
                Console.SetOut(previous);
            }
            var journey = (Godswar.Server.Operations.Health.SessionJourney)typeof(LoginClientHandler)
                .GetField("_journey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            long? expected = length >= 140 ? build : null;
            Check.True(journey.ClientBuild == expected, $"journey records build {build} (len {length}, min {minimum})");
            Check.Equal(accepted ? "accepted" : "refused", journey.Outcome, "login outcome");
            Check.True(log.ToString().Contains("[login] client build=" + ClientBuildPolicy.Describe((uint?)expected)),
                "login logs the client build");
            Check.Equal(accepted ? 1 : 0, authentication.Calls, "gate decides before authentication");
        }
    }

    private static async Task CheckRawLoginCapabilityAsync()
    {
        var packet = LoginPacket(
            Opcodes.Login,
            "test2",
            " password ",
            68);
        var allowedAuthentication =
            new FixedPasswordAccountAuthenticator();
        var allowedTransport =
            new ScriptedLegacyByteTransport();
        var allowedPacketBytes = (byte[])packet.Clone();
        await using (var session = new ClientSession(
            allowedTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var options = LocalOptions();
            var handler = new LoginClientHandler(
                session,
                options,
                allowedAuthentication,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleLoginAsync",
                new GamePacket(allowedPacketBytes));
        }

        Check.Equal(
            1,
            allowedAuthentication.Calls,
            "explicit local raw login authenticates once");
        Check.True(
            allowedAuthentication.LastPassword.AsSpan()
                .SequenceEqual("password"u8),
            "raw login preserves legacy password-edge trimming");
        Check.True(
            allowedPacketBytes.AsSpan(36, 32)
                .IndexOfAnyExcept((byte)0) < 0,
            "raw rollback credential packet bytes are cleared");

        var blockedAuthentication =
            new FixedPasswordAccountAuthenticator();
        var blockedTransport =
            new ScriptedLegacyByteTransport();
        var blockedPacketBytes = (byte[])packet.Clone();
        await using (var session = new ClientSession(
            blockedTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                LocalOptions(),
                blockedAuthentication);
            await InvokeAsync(
                handler,
                "HandleLoginAsync",
                new GamePacket(blockedPacketBytes));
        }

        Check.Equal(
            0,
            blockedAuthentication.Calls,
            "raw login without local capability performs no authentication");
        Check.Equal(
            1,
            blockedTransport.DisconnectCount,
            "raw login without local capability disconnects");
        Check.True(
            blockedPacketBytes.AsSpan(36, 32)
                .IndexOfAnyExcept((byte)0) < 0,
            "blocked raw credential packet bytes are cleared");

        var wrongPacket = LoginPacket(
            Opcodes.Login,
            "test2",
            "wrong-password",
            68);
        var wrongAuthentication =
            new FixedPasswordAccountAuthenticator();
        var wrongTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            wrongTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var options = LocalOptions();
            var handler = new LoginClientHandler(
                session,
                options,
                wrongAuthentication,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleLoginAsync",
                new GamePacket(wrongPacket));
        }

        Check.Equal(
            1,
            wrongAuthentication.Calls,
            "wrong-password raw login authenticates once");
        Check.True(
            wrongAuthentication.LastPassword.AsSpan().SequenceEqual(
                "wrong-password"u8),
            "raw login does not replace the supplied password");
        Check.True(
            wrongTransport.WrittenBytes.SequenceEqual(
                Encrypt(PacketBuilder.LoginFailed(3))),
            "wrong-password raw login returns only generic failure");
        Check.True(
            wrongPacket.AsSpan(36, 32)
                .IndexOfAnyExcept((byte)0) < 0,
            "wrong-password raw credential packet bytes are cleared");
    }

    private static async Task CheckRawGameBindingCapabilityAsync()
    {
        var packet = LoginPacket(
            Opcodes.LoginGameServer,
            "test2",
            null,
            36);
        Check.Equal(
            36,
            packet.Length,
            "legacy game-login compatibility packet has no password field");
        var allowedStore = new CountingAccountStore();
        var allowedTransport =
            new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            allowedTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var options = LocalOptions();
            var registry =
                new GameSessionRegistry(allowedStore);
            var handler = new GameClientHandler(
                session,
                allowedStore,
                registry,
                CharacterSnapshotReaderTestFixtures.Empty,
                WorldContentReaderTestFixtures.Empty,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket((byte[])packet.Clone()));
        }

        Check.Equal(
            1,
            allowedStore.FindByUsernameCalls,
            "explicit local raw game bind performs one username lookup");

        var blockedStore = new CountingAccountStore();
        var blockedTransport =
            new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            blockedTransport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var handler = new GameClientHandler(
                session,
                blockedStore,
                new GameSessionRegistry(blockedStore),
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty);
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket((byte[])packet.Clone()));
        }

        Check.Equal(
            0,
            blockedStore.FindByUsernameCalls,
            "raw game bind without local capability performs no lookup");
        Check.Equal(
            1,
            blockedTransport.DisconnectCount,
            "raw game bind without local capability disconnects");
    }

    private static async Task CheckSecureGameWithoutPrincipalAsync()
    {
        var instanceId = Enumerable.Repeat(
                (byte)0x55,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .ToArray();
        var buildHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            instanceId,
            instanceId,
            buildHash);
        var transport = new ScriptedSecureControlTransport(
            context,
            [],
            boundGamePrincipal: null);
        var store = new CountingAccountStore();
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game))
        {
            var options = LocalOptions();
            var handler = new GameClientHandler(
                session,
                store,
                new GameSessionRegistry(store),
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty,
                legacyAuthenticationAccess:
                    LocalAccess(options));
            await InvokeAsync(
                handler,
                "HandleGameLoginAsync",
                new GamePacket(
                    LoginPacket(
                        Opcodes.LoginGameServer,
                        "test2",
                        null,
                        36)));
        }

        Check.Equal(
            0,
            store.FindByUsernameCalls,
            "secure game channel never falls back to username lookup");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "secure game channel without a principal disconnects");
    }

    private static ServerOptions LocalOptions() =>
        new()
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=legacy-auth-check"
            },
            Authentication = new AuthenticationOptions
            {
                AllowLegacyRawAuthentication = true
            }
        };

    private static LegacyAuthenticationAccess LocalAccess(
        ServerOptions options) =>
        LegacyAuthenticationAccess.Create(
            ServerRuntimeProfilePolicy.Validate(options)) ??
        throw new InvalidOperationException(
            "Local compatibility capability was not created.");

    private static byte[] LoginPacket(
        ushort opcode,
        string username,
        string? password,
        int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            username);
        if (password is not null)
        {
            PacketText.WriteFixedAscii(
                packet.AsSpan(36, 32),
                password);
        }
        return packet;
    }

    private static async Task InvokeAsync(
        object handler,
        string methodName,
        GamePacket packet)
    {
        var method = handler.GetType().GetMethod(
            methodName,
            BindingFlags.Instance |
            BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"{methodName} test hook was not found.");
        var invocation = method.Invoke(
            handler,
            [packet, CancellationToken.None]);
        await (invocation as Task ??
            throw new InvalidOperationException(
                $"{methodName} did not return a task."));
    }

    private static byte[] Encrypt(byte[] clear)
    {
        var encrypted = (byte[])clear.Clone();
        new PacketCipher().Transform(encrypted);
        return encrypted;
    }

    private sealed class CountingAccountStore :
        GameStoreTestStub
    {
        public int FindByUsernameCalls { get; private set; }

        public override Task<GameAccount?>
            FindAccountByUsernameAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            FindByUsernameCalls++;
            return Task.FromResult<GameAccount?>(
                Account(username));
        }

        public override Task<GameCharacter?>
            GetFirstCharacterAsync(
                int accountId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<GameCharacter?>(null);

        private static GameAccount Account(string username) =>
            new()
            {
                Id = 7,
                Username = username
            };
    }
}
