using System.Collections.Concurrent;

namespace Godswar.Server.State;

/// <summary>
/// W1 dead items: Cypress Leave (4140, Magic.ini skill 4531 ScriptID ChangeHideID; item text: "your name will be displayed
/// as a Masked Man for 2 hours. Nearby players will not see your face clearly"). While a mask is active the player's
/// world-spawn frame (what OTHER players get) carries "Masked Man" instead of the name. Kept in memory and in
/// public.masked_state (runtime DDL), reloaded at start-up.
/// </summary>
internal static class MaskedNames
{
    public const string MaskName = "Masked Man";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private static readonly ConcurrentDictionary<int, DateTimeOffset> Masks = new();

    public static void Set(int characterId, DateTimeOffset until) => Masks[characterId] = until;

    public static bool IsMasked(int characterId) =>
        Masks.TryGetValue(characterId, out var until) &&
        (DateTimeOffset.UtcNow < until || !Masks.TryRemove(characterId, out _) && false);

    public static string DisplayName(GameCharacter character) => IsMasked(character.Id) ? MaskName : character.Name;
}
