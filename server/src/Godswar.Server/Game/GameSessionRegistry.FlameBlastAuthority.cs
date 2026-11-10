using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct FlameBlastSource(
    GameSessionContext Context,
    long LifeRevision);

internal sealed partial class GameSessionRegistry
{
    internal bool TryCaptureFlameBlastSource(
        ClientSession session,
        GameCharacter character,
        out FlameBlastSource source)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        source = default;
        lock (_gate)
        {
            if (!TryResolvePlayerMonsterAuthorityLocked(session, character.CurrentMap,
                    out var context, out _, out var authority) ||
                !ReferenceEquals(context.Character, character) ||
                context.CharacterId != character.Id || context.AccountId != character.AccountId)
            {
                return false;
            }

            lock (character.VitalsSync)
            {
                if (character.CurrentHp <= 0) return false;
                source = new(context, authority.LifeRevision);
                return true;
            }
        }
    }

    internal bool IsCurrentFlameBlastSource(FlameBlastSource source)
    {
        lock (_gate)
        {
            return TryResolveFlameBlastSourceLocked(source, out _);
        }
    }

    internal bool TryCaptureFlameBlastTargets(
        FlameBlastSource source,
        out IReadOnlyList<MonsterRuntimeSnapshot> targets,
        out PlayerMonsterCombatAuthority authority)
    {
        targets = [];
        authority = default;
        PlayerMonsterCombatAuthority beforeCapture;
        lock (_gate)
        {
            if (!TryResolveFlameBlastSourceLocked(source, out beforeCapture)) return false;
        }

        // Do not add an outer registry lock around the existing world-owner
        // snapshot operation. Its returned authority must still belong to the
        // frozen field source when we reacquire the registry gate below.
        if (!TryCapturePlayerMonsterTargets(source.Context.Session, source.Context.MapId,
                out var capturedTargets, out var capturedAuthority))
        {
            return false;
        }

        lock (_gate)
        {
            if (capturedAuthority != beforeCapture ||
                !TryResolveFlameBlastSourceLocked(source, out var currentAuthority) ||
                capturedAuthority != currentAuthority)
            {
                return false;
            }

            // Keep this precise pulse authority. Guarded damage commits reject
            // a later life, ownership, world revision or membership change.
            targets = capturedTargets;
            authority = capturedAuthority;
            return true;
        }
    }

    private bool TryResolveFlameBlastSourceLocked(
        FlameBlastSource source,
        out PlayerMonsterCombatAuthority authority)
    {
        authority = default;
        if (source.Context is null || source.LifeRevision < 0 ||
            !TryResolveMedusaPublicationContextLocked(source.Context, source.LifeRevision,
                out var current) ||
            !TryResolvePlayerMonsterAuthorityLocked(current.Session, current.MapId,
                out var resolved, out _, out var resolvedAuthority) ||
            !ReferenceEquals(current, resolved))
        {
            return false;
        }

        // The context resolver permits normal position/stat revisions while
        // retaining exact character, account, realm, instance, object, ownership,
        // membership epoch and life. The second resolver checks live ownership.
        lock (current.Character.VitalsSync)
        {
            if (current.Character.CurrentHp <= 0) return false;
            authority = resolvedAuthority;
            return true;
        }
    }
}
