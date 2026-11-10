using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task AssertMonsterClaimAsync(Fixture fixture)
    {
        var claim = await fixture.Socket.ReadPacketAsync(12);
        Check.Equal(
            Opcodes.MonsterClaimState,
            ReadOpcode(claim),
            "first completed Thunder publishes its monster claim before damage");
        Check.Equal(
            MonsterObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(claim.AsSpan(4, 4)),
            "Thunder claim identifies the damaged monster");
        Check.Equal(
            0xFFFFFF01u,
            BinaryPrimitives.ReadUInt32LittleEndian(claim.AsSpan(8, 4)),
            "Thunder claim publishes the native owner marker");
    }
}
