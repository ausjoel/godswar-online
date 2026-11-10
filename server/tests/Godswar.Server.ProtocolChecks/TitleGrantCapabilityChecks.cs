using System.Reflection;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class TitleGrantCapabilityChecks
{
    public const string CheckName = "Title grant capability identifies compatible ownership readers and bounded full catalogue support";
    public static Task RunAsync()
    {
        var method = typeof(GameSessionRegistry).GetMethod("BuildOperatorCapabilitiesJson", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var doc = JsonDocument.Parse((string)method.Invoke(null, null)!);
        var capability = doc.RootElement.GetProperty("titleGrants");
        Check.Equal(1, capability.GetProperty("version").GetInt32(), "editor can identify compatible grant readers");
        Check.Equal(CharacterSnapshotLimits.OwnedTitleCount, capability.GetProperty("ownedLimit").GetInt32(),
            "editor sees the actual snapshot/list bound instead of guessing");
        Check.True(CharacterSnapshotLimits.OwnedTitleCount >= 105, "all ninety native plus authored earned titles fit");
        return Task.CompletedTask;
    }
}
