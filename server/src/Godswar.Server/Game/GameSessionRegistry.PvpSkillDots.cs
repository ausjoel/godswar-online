using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// W3 PvP: starts a skill damage-over-time status on a player. The ticks run in the player Burn pump
    /// (CommitDuePlayerElementalBurns), which re-checks the PvP admission of the source on every tick and
    /// handles death, persistence and publication exactly like an elemental Burn.
    /// </summary>
    internal bool TryApplyPlayerSkillDot(
        ClientSession targetSession,
        SkillDamageOverTimeDefinition definition,
        long sourceCharacterId,
        ulong sourceEventId,
        DateTimeOffset appliedAt,
        int tickDamage)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(targetSession, out var target) ||
                !target.WorldReady ||
                target.Character.CurrentHp <= 0)
            {
                return false;
            }

            var state = GetOrCreateElementalCombatSession(
                targetSession,
                new ElementalCombatSessionIdentity(
                    target.CharacterId,
                    target.MapId,
                    target.WorldInstanceId,
                    target.Ownership));
            lock (state.Gate)
            {
                return state.SkillDots.TryApply(
                    definition,
                    sourceCharacterId,
                    sourceEventId,
                    appliedAt.ToUnixTimeMilliseconds(),
                    tickDamage);
            }
        }
    }
}
