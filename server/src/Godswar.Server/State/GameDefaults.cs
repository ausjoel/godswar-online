using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;

namespace Godswar.Server.State;

internal static class GameDefaults
{
    public const byte SpartaCamp = FactionPortalSkillPolicy.SpartaCamp;

    public const byte AthensCamp = FactionPortalSkillPolicy.AthensCamp;

    public const byte SpartaCapitalMap = 0;

    public const byte AthensCapitalMap = 1;

    public const float StartingPositionX = 165.0f;

    public const float StartingPositionZ = -97.0f;

    public const string EmptyKitBag =
        "[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#";

    public const string StarterKitBag =
        "[4000,,,,,,0,10,1,1,0]#[4030,,,,,,0,10,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#";

    public static void NormalizeCamp(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        character.Camp = character.Camp == SpartaCamp ? SpartaCamp : AthensCamp;
    }

    public static void InitializeStartingLocation(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        NormalizeCamp(character);
        character.CurrentMap = character.Camp == SpartaCamp ? SpartaCapitalMap : AthensCapitalMap;
        character.PositionX = StartingPositionX;
        character.PositionZ = StartingPositionZ;
    }

    public static bool TryRecoverUnavailableInstanceLocation(
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(
                character.CurrentMap))
        {
            return false;
        }

        ReviveTrace.Log(
            $"TRYRECOVER dynamic map={character.CurrentMap} " +
            $"isMedusa={DynamicDungeonContentMapPolicy.IsMedusaMap(character.CurrentMap)}");

        // Every exact-instance map falls back to the capital on a plain login.
        // A Medusa challenger used to be re-anchored onto the island instead so a
        // run would survive a reconnect, but a login carries no gateway admission
        // and these maps may only be hosted by an authoritative instance: the
        // character was left standing on map 204 with no open world to join and the
        // session ended right after the entry. The reconnect trace showed
        // "medusa-anchor applied", then "RESTORE-ENTRY map=204", then nothing - the
        // client never even sent its first post-entry packet. Re-entering a live run
        // goes through the instance caller, which skips this recovery entirely.
        ReviveTrace.Log("TRYRECOVER falling back to capital");
        InitializeStartingLocation(character);
        return true;
    }

    public static string DefaultEquipment(byte profession)
    {
        return profession switch
        {
            0 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1000,,,,,,1,1,1,1,0]#[2000,,,,,,1,1,1,1,0]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            1 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1400,,,,,,1,1,1,1,0]#[]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            2 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1700,,,,,,1,1,1,1,0]#[2000,,,,,,1,1,1,1,0]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            3 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1800,,,,,,1,1,1,1,0]#[]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            _ => DefaultEquipment(0)
        };
    }
}
