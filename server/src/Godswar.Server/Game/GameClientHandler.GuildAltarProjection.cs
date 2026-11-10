using Godswar.Server.Application.Guilds;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Keeps one character's altar attribute bonus in step with the altars it comes
/// from.
/// </summary>
/// <remarks>
/// The bonus is a projection: it is derived from the member's offering points
/// (<c>guild_altar_worship</c>) and the guild's altar levels
/// (<c>guild_buildings</c>), never written back. <see cref="CharacterStats.FromCharacter"/>
/// applies whatever is cached on the character, so the cache has to be refreshed
/// at the two moments the inputs change - when the guild window is opened or
/// pushed, and when an altar is offered to or built/upgraded.
///
/// Nothing else needs to remember the altar exists: because the projection is
/// applied inside the one stats funnel, login, combat, the attribute panel and
/// the ECS projection all pick it up.
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Recomputes the altar bonus from the database and applies it to the live
    /// character.
    /// </summary>
    /// <returns>Whether the projection actually changed.</returns>
    private async Task<bool> RefreshAltarBonusAsync(
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return false;
        }

        var balances = await _guilds.TryReadAltarWorshipAsync(
            _character.Id,
            DateTimeOffset.UtcNow,
            cancellationToken);
        var bonus = _guilds.ResolveAltarBonus(balances);
        // Recorded against the character's id, not on the character: the session's
        // character object is replaced by a fresh hydration on pet owner-Merge,
        // un-merge and login, and a value stored on it would be dropped there.
        if (!GuildAltarBonusCache.Set(_character.Id, bonus))
        {
            return false;
        }

        // The projection is written from the character's clean base, never from
        // CharacterStats.FromCharacter: that result already carries this bonus (and
        // the title bonus), and CalculatedStats is what FromCharacter reads back as
        // its baseline, so storing a bonused value here would apply the bonus again
        // on every later read. Measured in play as +810 arriving as +1620.
        CharacterCalculatedStatsProjectionApplier.Apply(
            _character,
            CharacterStats.FromCharacterBase(_character),
            CharacterHealthProjectionMode.PreserveAbsolute);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        Console.WriteLine(
            $"[guild] altar bonus character={_character.Name} " +
            $"hp={bonus.MaxHp} mp={bonus.MaxMp} " +
            $"patk={bonus.PhysicalAttack} matk={bonus.MagicAttack} " +
            $"pdef={bonus.PhysicalDefense} mdef={bonus.MagicDefense} " +
            $"hit={bonus.Hit} dodge={bonus.Dodge} " +
            $"absorb={bonus.DamageAbsorb} hprec={bonus.HpRecovery} " +
            $"mprec={bonus.MpRecovery} " +
            // The live ceilings the client is about to be sent, so a wrong total
            // can be told apart from a wrong bonus without querying the database.
            $"liveMaxHp={_character.MaxHp} liveMaxMp={_character.MaxMp}");
        return true;
    }

    /// <summary>
    /// Refreshes the altar bonus and, when it moved, tells the client to redraw
    /// the player's status so the new numbers show.
    /// </summary>
    /// <returns>Whether a status frame was sent.</returns>
    private async Task<bool> PushAltarBonusAsync(CancellationToken cancellationToken)
    {
        if (!await RefreshAltarBonusAsync(cancellationToken))
        {
            return false;
        }

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "GuildAltarBonusStatus");
        return true;
    }
}
