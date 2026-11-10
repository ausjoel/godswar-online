using System.Diagnostics;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// QUEST-UI: the client's quest UI (QuestInfoUI / QuestViewUI) is loaded only by
/// its LOGIN module startup. A redirect that lands in the same client frame as
/// the realm list skips that module, and the first 10090 then faults at
/// 0x005D1CC3. These checks pin the redirect pacing and keep 10090 inside the
/// world-entry burst (EnterMain .. EnterComplete), never on a map transition.
/// </summary>
internal static partial class LegacyRealmLoginProtocolChecks
{
    public const string QuestUiLifecycleCheckName =
        "Login redirect pacing and 10090 lifecycle (quest UI crash 0x005D1CC3)";

    public static async Task RunQuestUiLifecycleAsync()
    {
        Check.True(
            LoginClientHandler.DefaultRedirectPacing >=
                TimeSpan.FromMilliseconds(250),
            "default redirect pacing leaves the client a frame to start LOGIN");
        await CheckRedirectPacingAsync();
        CheckAcceptedQuestSendSites();
    }

    private static async Task CheckRedirectPacingAsync()
    {
        var dwargon = Entry(
            RealmId.Dwargon,
            "Dwargon",
            "DWG3jcIzqGgKvOf1dbYZKC8cS",
            "127.1.1.111",
            recommended: false,
            displayOrder: 1);
        var catalog = new RealmCatalogSnapshot([dwargon]);
        var options = LocalOptions();
        var pacing = TimeSpan.FromMilliseconds(400);
        var transport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                options,
                new FixedPasswordAccountAuthenticator(),
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: new FixedRealmCatalogReader(catalog),
                redirectPacing: pacing);
            await DispatchAsync(handler, LoginPacket());
            var afterServerList = transport.WrittenBytes.Length;

            var selectionClock = Stopwatch.StartNew();
            var selection = DispatchAsync(
                handler,
                SelectionPacket(dwargon.LegacyWireId));
            await Task.Delay(100);
            Check.Equal(
                afterServerList,
                transport.WrittenBytes.Length,
                "no SendServer (7) is written while the client may still be " +
                "draining the 10008 frame");
            await selection;
            selectionClock.Stop();
            Check.True(
                selectionClock.Elapsed >= pacing - TimeSpan.FromMilliseconds(20),
                $"SendServer waits its pacing after the selection " +
                $"(waited {selectionClock.Elapsed.TotalMilliseconds:F0} ms)");
            var beforeRedirect = transport.WrittenBytes.Length;
            Check.True(
                beforeRedirect > afterServerList,
                "SendServer is written once the pacing has passed");

            var clock = Stopwatch.StartNew();
            var redirect = DispatchAsync(
                handler,
                OpcodePacket(Opcodes.LoginReturnInfo));
            await Task.Delay(100);
            Check.Equal(
                beforeRedirect,
                transport.WrittenBytes.Length,
                "no redirect is written while the client may still be " +
                "draining the realm list frame");
            await redirect;
            clock.Stop();
            Check.True(
                clock.Elapsed >= pacing - TimeSpan.FromMilliseconds(20),
                $"redirect waits its pacing after the request " +
                $"(waited {clock.Elapsed.TotalMilliseconds:F0} ms)");
        }

        var expected = PacketBuilder.ServerList(catalog)
            .Concat(PacketBuilder.SendServer(dwargon))
            .Concat(PacketBuilder.GameServerRedirect(dwargon))
            .ToArray();
        Check.True(
            transport.WrittenBytes.SequenceEqual(Encrypt(expected)),
            "paced redirect is still the unchanged 10001 frame");
        Check.Equal(0, transport.DisconnectCount, "paced flow stays open");
    }

    /// <summary>
    /// Source ratchet: the accepted-quest snapshot has one sender, it runs only
    /// from the world-entry burst, and it goes out before EnterComplete.
    /// </summary>
    private static void CheckAcceptedQuestSendSites()
    {
        var root = FindServerRoot();
        var source = Path.Combine(root, "src", "Godswar.Server");
        var builders = new List<string>();
        var snapshotCalls = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     source,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(path);
            if (Regex.IsMatch(
                    text,
                    @"\b(QuestSnapshotFrames|PlayerAcceptedQuestsFrame|" +
                    @"QuestSnapshot|LoginSnapshotFrame|" +
                    @"LoginQuestSnapshotFrame)\(") &&
                !relative.StartsWith("Packets/", StringComparison.Ordinal))
            {
                builders.Add(relative);
            }

            if (Regex.IsMatch(
                    text,
                    @"(?<!Task )\bSendInitialQuestSnapshotAsync\("))
            {
                snapshotCalls.Add(relative);
            }
        }

        Check.True(
            builders.SequenceEqual(["Game/GameClientHandler.Quests.cs"]),
            "only the login snapshot builds a 10090 outside Packets/ " +
            $"(found: {string.Join(", ", builders)})");
        Check.True(
            snapshotCalls.SequenceEqual(
                ["Game/GameClientHandler.LoginWorldEntry.cs"]),
            "the 10090 snapshot is sent only from world entry, never from a " +
            $"map transition (found: {string.Join(", ", snapshotCalls)})");

        var entry = File.ReadAllText(Path.Combine(
            source,
            "Game",
            "GameClientHandler.LoginWorldEntry.cs"));
        var enterMain = entry.IndexOf(
            "\"EnterMain\");",
            StringComparison.Ordinal);
        var snapshot = entry.IndexOf(
            "await SendInitialQuestSnapshotAsync(",
            StringComparison.Ordinal);
        var enterComplete = entry.IndexOf(
            "PacketBuilder.EnterComplete()",
            StringComparison.Ordinal);
        Check.True(
            enterMain >= 0 && snapshot > enterMain &&
                enterComplete > snapshot,
            "10090 keeps the retail position: after EnterMain, before " +
            "EnterComplete");
    }

    private static string FindServerRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var candidate = new DirectoryInfo(seed);
                 candidate is not null;
                 candidate = candidate.Parent)
            {
                if (File.Exists(Path.Combine(
                        candidate.FullName,
                        "GodswarServer.sln")))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the server root containing GodswarServer.sln.");
    }
}
