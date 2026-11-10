using Godswar.Server.Application.North;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// FINALISE2 F-08 (the admin 2026-11-05): "It should be clear in the system info bottom right text that shows to the player
/// when they enter and exit the safe zones." One personal info line when a walk (or an arrival) puts the player inside
/// a north camp safe zone (pvp.npc_safe_radius round the gm_npc_spawns npcs), one when they walk out. Nothing at all
/// while the radius is 0. The state resets on every map change, so arriving inside a camp says "entered".
/// </summary>
internal sealed partial class GameClientHandler
{
    internal const string NorthSafeZoneEnteredText = "You entered a camp safe zone: players cannot attack you here.";
    internal const string NorthSafeZoneLeftText = "You left the camp safe zone: players can attack you again.";

    private short _northSafeZoneMap = -1;
    private bool _northSafeZoneInside;

    private async Task UpdateNorthSafeZoneLineAsync(CancellationToken token)
    {
        if (_character is null) return;
        var map = _character.CurrentMap;
        if (map != _northSafeZoneMap)
        {
            _northSafeZoneMap = map;
            _northSafeZoneInside = false;
        }

        var inside = _character.CurrentHp > 0 &&
                     NorthNpcSafeZone.Contains(map, _character.PositionX, _character.PositionZ);
        if (inside == _northSafeZoneInside) return;
        _northSafeZoneInside = inside;
        Console.WriteLine(
            $"[north-safe] {(inside ? "entered" : "left")} character={_character.Name} map={map} " +
            $"x={_character.PositionX:0.0} z={_character.PositionZ:0.0}");
        byte[] packet;
        try
        {
            packet = PacketBuilder.PersonalGameLog(inside ? NorthSafeZoneEnteredText : NorthSafeZoneLeftText);
        }
        catch (ArgumentException error)
        {
            Console.WriteLine($"[north-safe] line skipped: {error.Message}");
            return;
        }

        // Informational: a busy egress queue skips the line, never the connection.
        if (!_session.TryAdmitExact(packet, out var write)) return;
        try
        {
            await write;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
