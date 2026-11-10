using System.Collections.Concurrent;

namespace Godswar.Server.State;

/// <summary>
/// The two guild numbers the character panel prints: the character's duty (the
/// <c>Positionpro</c> row) and the guild contribution it still holds (the
/// <c>Contributepro</c> row).
/// </summary>
/// <remarks>
/// <para>
/// Both ride <c>S2C 10166</c> at wire offsets 80 and 84, which the receive
/// handler copies into <c>GameData+0x2A4</c> and <c>GameData+0x2A8</c> (the
/// status block starts at wire 8 and lands on <c>GameData+0x25C</c>);
/// <see cref="Packets.PacketBuilder.PlayerStatusUpdate"/> writes them from this
/// cache.
/// </para>
/// <para>
/// Duty 0 is the client's own "no guild" (its job table runs 1-6 - see
/// <c>Settings/Sys/Consortia_Job.ini</c>, which numbers 0 as 无); contribution
/// is the character's remaining balance, not the per-member
/// <c>contribution_gold</c>/<c>contribution_silver</c> the guild window's member
/// table shows.
/// </para>
/// </remarks>
internal readonly record struct GuildPanelStatus(
    byte Duty,
    int Contribution,
    string GuildName = "")
{
    /// <summary>What a character outside every guild has: nothing to print.</summary>
    public static readonly GuildPanelStatus None = new(0, 0, string.Empty);
}

/// <summary>
/// The guild panel numbers a character currently has, held by character id
/// rather than on the character object.
/// </summary>
/// <remarks>
/// <para>
/// Keying by id is the same decision <see cref="GuildAltarBonusCache"/> records
/// and for the same reason: the session's <see cref="GameCharacter"/> is swapped
/// for a freshly hydrated one on login, on every durable bag/wallet round-trip
/// (<c>GameClientHandler.CharacterRefresh.InstallUpdatedCharacter</c>) and on pet
/// owner-Merge, so a value parked on that object is silently replaced by the new
/// object's default.
/// </para>
/// <para>
/// This is a cache of a projection, not a second source of truth. The
/// authoritative state is <c>character_base.guild_duty</c> (written together with
/// <c>consortia_job</c> and <c>consortia</c> by <c>PostgresGuildStore</c>) and
/// <c>character_base.consortia_contribute</c>; every writer of this cache reads
/// it back from there, so a lost entry costs one refresh and never a wrong answer
/// that outlives it.
/// </para>
/// </remarks>
internal static class GuildPanelStatusCache
{
    private static readonly ConcurrentDictionary<int, GuildPanelStatus> Statuses =
        new();

    /// <summary>
    /// The status this character currently has, or
    /// <see cref="GuildPanelStatus.None"/> when nothing was read for it yet.
    /// </summary>
    public static GuildPanelStatus For(int characterId) =>
        Statuses.TryGetValue(characterId, out var status)
            ? status
            : GuildPanelStatus.None;

    /// <summary>
    /// Records the status a character now has.
    /// </summary>
    /// <returns>Whether it differs from what was cached.</returns>
    public static bool Set(int characterId, GuildPanelStatus status)
    {
        if (For(characterId) == status)
        {
            return false;
        }

        Statuses[characterId] = status;
        return true;
    }

    /// <summary>
    /// Drops a character's entry, which the next refresh restores.
    /// </summary>
    public static void Clear(int characterId) =>
        Statuses.TryRemove(characterId, out _);
}
