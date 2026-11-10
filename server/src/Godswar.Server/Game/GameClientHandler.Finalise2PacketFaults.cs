using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

// FINALISE2 (P4, F-39; the admin: "Catch + reload, behind an env flag, rig-tested first"). Default OFF.
// GODSWAR_PACKET_FAULT_RECOVERY=1: an exception escaping one packet's handler no longer ends the session. The fault
// is logged ([game-fault] as before, then [game-fault-recovered]), the character snapshot is reloaded from the
// database (what was committed stays, unsaved in-memory changes of that packet are dropped), the bag and status are
// re-sent, and the next packet is read. At most 5 recoveries per session; the 6th fault, a fault before the
// character is loaded, or a failed reload ends the session exactly as before.
// GODSWAR_PACKET_FAULT_TEST_OPCODE=<n> (rig only) throws inside the handler for opcode n, to test this path.
internal sealed partial class GameClientHandler
{
    private const int Finalise2MaximumPacketFaultRecoveries = 5;

    private static readonly bool Finalise2PacketFaultRecovery =
        Environment.GetEnvironmentVariable("GODSWAR_PACKET_FAULT_RECOVERY") == "1";

    internal static readonly ushort Finalise2PacketFaultTestOpcode =
        ushort.TryParse(Environment.GetEnvironmentVariable("GODSWAR_PACKET_FAULT_TEST_OPCODE"), out var opcode)
            ? opcode
            : (ushort)0;

    private int _finalise2PacketFaultRecoveries;

    private async Task<bool> TryRecoverFromPacketFaultAsync(int opcode, Exception fault,
        CancellationToken cancellationToken)
    {
        // Never after a lost ownership/lease: reloading then could let two sessions drive one character.
        if (!Finalise2PacketFaultRecovery || fault is OperationCanceledException or PlayerOwnershipValidationException ||
            fault.GetType().Name.Contains("Ownership", StringComparison.Ordinal) ||
            fault.GetType().Name.Contains("Lease", StringComparison.Ordinal) ||
            _account is null || _character is null || !_characterSnapshotLoaded ||
            _finalise2PacketFaultRecoveries >= Finalise2MaximumPacketFaultRecoveries)
        {
            return false;
        }

        _finalise2PacketFaultRecoveries++;
        try
        {
            var bootstrapPending = _characterSnapshotBootstrapPending;
            if (!await RefreshCharacterSnapshotAsync("packet_fault", cancellationToken) || _character is null)
            {
                return false;
            }

            _characterSnapshotBootstrapPending = bootstrapPending;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            await SendKitBagRefreshAsync(cancellationToken);
            await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "PacketFaultStatus");
            Console.Error.WriteLine($"[game-fault-recovered] opcode={opcode} character={_character.Name} " +
                $"recoveries={_finalise2PacketFaultRecoveries}/{Finalise2MaximumPacketFaultRecoveries}");
            return true;
        }
        catch (Exception reload) when (reload is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[game-fault-recovered] reload failed opcode={opcode}: {reload.Message}");
            return false;
        }
    }
}
