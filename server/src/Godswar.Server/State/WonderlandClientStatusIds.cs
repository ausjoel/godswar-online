namespace Godswar.Server.State;

// Encounter overlays combine custom blessings with captured native debuffs.
// These are presentation IDs; authoritative modifiers are applied once by
// the encounter, separately from the player's ordinary runtime status ledger.
internal static class WonderlandClientStatusIds
{
    public const uint PetbirdBlessing = 1510;
    public const uint PutridBirdBlessing = 1511;
    public const uint Stunned = 1512;
    public const uint Silenced = 361;
    public const uint InternalInjury = 133;
    public const uint SpearBlast = 1514;
    public const uint ArmorRend = 1515;
}
