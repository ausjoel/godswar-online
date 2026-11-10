using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    private static void CheckCompleteCapturePlacements()
    {
        // Immutable external-full.log SHA81EA1117...D666DF44, Sep13 complete run.
        // Literal10018 frames at seq60454/63368/66044/71243/75071/78926/81892/86364.
        string[] arrivals = [
            "1800222764000000000026432DA57F3F000056C30100CF00",
            "18002227640000000000A041A52FBA40000042C30100CF00",
            "180022276400000000007CC26AACAC400000E6C20100CF00",
            "1800222764000000000010C3E6EDAF400000A8C10100CF00",
            "180022276400000000003AC3FFA1C540000043430100CF00",
            "18002227640000000000E842C363E03F000053430100CF00",
            "180022276400000000001E43293C12400000E8410100CF00",
            "180022276400000000006042D4D56A3F0000E0C20100CF00"];
        // SHA256 over canonical template ASCII+NUL, then exact little-endian X/Z
        // float32, in the reviewed roster order. Derived from capture packets,
        // not from the implementation; no filesystem or installed client dependency.
        string[] placementHashes = [
            "A53075475617AE422EE64F76A424DE933914D2AB0E9710F1F333885F0E74D96C",
            "A6D05A42AEF24A4BE0D89227DF7AEB3CC625D204EEFD4B3DEE448C79147E5AB9",
            "07C1E95E7CA2EC4512A3E7B79172A03E64B794325EA37C6489462C9C949D1E36",
            "1CA21D314B71CEC2AC6AF308E313249A7C309D5C6307AFAB6FD9AE9BAE34EDEB",
            "4DF55AD67F4F880CA04213102A1C3A1EEC45A0C6464976F9159FEF9A5FE456E1",
            "8055F432BE22C9D975EE578F41E553533B98F8D039985DC38EC0DED7BE9B7780",
            "EC95F89677A4DF453C866D521B22B60B9A6F67972BEF97B74082ABED8D594B46",
            "020A980A4580DC6EA649E97623BE54F9C12C0D7A97341574862ED8A303316217"];
        for (var island = 1; island <= 8; island++)
        {
            var arrival = Convert.FromHexString(arrivals[island - 1]);
            var entrance = WonderlandTerrainPolicy.GetIsland(island).Entrance;
            // Island one intentionally shares the user's (169,-216) initial
            // landing for admission, recovery, and revival. Preserve the
            // original capture bytes above as evidence of the small override.
            var expectedX = island == 1 ? 169f : BinaryPrimitives.ReadSingleLittleEndian(arrival.AsSpan(8));
            var expectedZ = island == 1 ? -216f : BinaryPrimitives.ReadSingleLittleEndian(arrival.AsSpan(16));
            Check.True(BinaryPrimitives.ReadUInt16LittleEndian(arrival.AsSpan(2)) == 10018 &&
                BinaryPrimitives.ReadUInt16LittleEndian(arrival.AsSpan(22)) == 207 &&
                entrance.X == expectedX && entrance.Z == expectedZ,
                $"island {island} uses its reviewed arrival, including the shared first-island landing");
            foreach (byte camp in new byte[] { 0, 1 })
            {
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                    foreach (var actor in WonderlandMonsterPlan.Create(island, 1, camp))
                    {
                        writer.Write(Encoding.ASCII.GetBytes(actor.TemplateKey));
                        writer.Write((byte)0);
                        writer.Write(actor.Position.X);
                        writer.Write(actor.Position.Z);
                    }
                Check.Equal(placementHashes[island - 1], Convert.ToHexString(SHA256.HashData(stream.ToArray())),
                    $"island {island}/{camp}: every monster template is paired with its exact captured first-appearance coordinates");
            }
        }
        var seventh = WonderlandMonsterPlan.Create(7, 1, 0);
        var seventhEntrance = WonderlandTerrainPolicy.GetIsland(7).Entrance;
        Check.True(seventh.Any(actor => actor.Role == WonderlandMonsterRole.PutridBird &&
            WonderlandTerrainPolicy.DistanceSquared(seventhEntrance, actor.Position.X, actor.Position.Z) < 100) &&
            seventh.All(actor => WonderlandTerrainPolicy.IsCombatArea(7, actor.Position.X, actor.Position.Z)),
            "the exact near-arrival Putrid Bird remains active instead of being suppressed by an invented ten-unit exclusion");
    }
}
