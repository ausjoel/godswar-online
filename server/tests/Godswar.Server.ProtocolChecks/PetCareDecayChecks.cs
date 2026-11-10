using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetCareDecayChecks
{
    public const string CheckName =
        "Pet care decay cadence and native care frame";

    public static Task RunAsync()
    {
        Check.Equal(
            (ushort)10_245,
            Opcodes.PetCareState,
            "pet care-state opcode");
        Check.True(
            Opcodes.Name(Opcodes.PetCareState) == "PetCareState",
            "pet care-state opcode has a diagnostic name");

        // The installed client ships only recovery text, so the project
        // cadence is the sole authority: ten summoned minutes cost two
        // satiety and two lifetime, and one Owner Merge costs forty amity.
        Check.True(
            PetCareDecayPolicy.Interval == TimeSpan.FromMinutes(10) &&
            PetCareDecayPolicy.SatietyPointsPerInterval == 2 &&
            PetCareDecayPolicy.LifetimePointsPerInterval == 2,
            "summoned care drains two points every ten minutes");
        Check.Equal(
            40,
            PetManagerPlanner.OwnerMergeAmityCost,
            "one Owner Merge spends forty amity");
        Check.Equal(
            PetManagerPlanner.MinimumOwnerMergeAmity,
            PetManagerPlanner.OwnerMergeAmityCost,
            "the merge threshold and its cost are the same forty points");
        Check.True(
            PetCareDecayPolicy.Decay(1, 2) == 0 &&
            PetCareDecayPolicy.Decay(100, 2) == 98 &&
            PetCareDecayPolicy.CanBeSummoned(1, 1) &&
            !PetCareDecayPolicy.CanBeSummoned(0, 10) &&
            !PetCareDecayPolicy.CanBeSummoned(10, 0),
            "care drain floors at zero and an exhausted pet cannot be sent out");

        // 16-byte frame: uint32 pet ID at +4, satiety at +9, amity at +11,
        // uint16 current lifetime at +14.
        var packet = PacketBuilder.PetCareState(
            petId: 7,
            satiety: 98,
            amity: 60,
            remainingLifetime: 1234);
        Check.True(
            packet.SequenceEqual(
                Convert.FromHexString("10000528070000000062003C0000D204")),
            "pet care-state frame bytes");
        Check.True(
            PacketBuilder.PetCareState(7, 500, 500, 70_000)[9] == 100 &&
            PacketBuilder.PetCareState(7, 500, 500, 70_000)[11] == 100,
            "satiety and amity are clamped to the native percent byte");
        return Task.CompletedTask;
    }
}
