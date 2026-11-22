// ROUND3: patch-channel compatibility gate; the stamp is not client attestation.
using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal static class ClientBuildPolicy
{
    // tools/r2client.py and rates_probe.py: 32 + 32 + 68 bytes before the stamp.
    internal const int BuildPayloadOffset = 132;

    // ROUND-CLIENTBUILD: the client sends twice its patch.vn number (GodsWar.exe 0x468467 lea edx,[ecx+ecx]).
    // patch.vn = 2054000 + channel release (launcher/Launcher/client_build.py), so release 17 sends 4108034.
    internal const uint StockBuild = 4102004;    // retail 2.51.002 patch.vn 2051002 (also Native and the probes)
    internal const uint UnstampedBuild = 4108000; // Origin patch.vn 2054000: every release up to 1.5.6 (channel 16)
    internal const uint PatchVnReleaseBase = 2054000;

    public static bool Allows(GamePacket packet, uint minimumBuild) =>
        minimumBuild == 0 ||
        (packet.Opcode == Opcodes.Login &&
         TryRead(packet.Payload, out var build) &&
         build >= minimumBuild);

    // ROUND-CLIENTBUILD: one reader for the recorder, the log line and the gate.
    public static bool TryRead(ReadOnlySpan<byte> payload, out uint build)
    {
        if (payload.Length >= BuildPayloadOffset + sizeof(uint))
        {
            build = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(BuildPayloadOffset, sizeof(uint)));
            return true;
        }
        build = 0;
        return false;
    }

    // ROUND-CLIENTBUILD: "4108034 (2.54.017, release 17)"; the label is what the login screen shows.
    public static string Describe(uint? build)
    {
        if (build is not { } sent)
        {
            return "none";
        }
        if (sent % 2 != 0 || sent > int.MaxValue)
        {
            return sent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var patchVn = sent / 2;
        var label = $"{patchVn / 1000000}.{patchVn / 1000 % 1000}.{patchVn % 1000:000}";
        var note = sent switch
        {
            StockBuild => ", stock 2.51.002",
            UnstampedBuild => ", unstamped",
            _ when patchVn > PatchVnReleaseBase && patchVn <= PatchVnReleaseBase + 999 =>
                $", release {patchVn - PatchVnReleaseBase}",
            _ => string.Empty
        };
        return $"{sent} ({label}{note})";
    }
}
