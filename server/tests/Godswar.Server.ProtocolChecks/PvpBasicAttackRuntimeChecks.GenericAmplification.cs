// ROUND3: verify registry/typed combat, expiry and removal in both authoritative runtime modes.
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string GenericAmplificationCheckName =
        "Generic damage amplification reaches PvP and monster combat with expiry and source fencing";

    public static async Task RunGenericAmplificationAsync()
    {
        foreach (var id in Enumerable.Range(130, 8).Concat(Enumerable.Range(140, 4)))
            Check.True(GenericStatusCatalog.TryResolve(id, out _, out _), $"ordinary amplifier {id} resolves");
        foreach (var id in new[] { 450, 451 })
            Check.True(!GenericStatusCatalog.TryResolve(id, out _, out var error) && error.Contains("bound"),
                "extreme source rows remain refused instead of silently clamped");
        Check.True(GenericStatusCatalog.TryGet(130, out var source), "audited injury source exists");
        Check.True(GenericStatusCatalog.UnsupportedReason(source with { Effects = [new(13, -0.00001m)] }) is not null,
            "sub-basis-point effects are refused without truncation");
        Check.True(GenericStatusCatalog.UnsupportedReason(source with { Effects = [new(13, -0.5m), new(13, -0.5m)] }) is not null,
            "the complete row must fit the reviewed bound");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var attackerSocket = await RuntimePolicySessionSocket.CreateAsync();
            await using var targetSocket = await RuntimePolicySessionSocket.CreateAsync();
            var registry = Registry(mode);
            var attacker = Player(2460, GameDefaults.SpartaCamp, physicalAttack: 1_000, hit: 5_000);
            var target = Player(2461, GameDefaults.AthensCamp, physicalDefense: 100, dodge: 0);
            Join(registry, attackerSocket, attacker);
            Join(registry, targetSocket, target);
            try
            {
                var now = DateTimeOffset.UtcNow;
                Check.True(GenericStatusCatalog.TryResolve(130, out var injury, out _) &&
                    injury.PhysicalDamageTakenIncreaseBasisPoints == 1_000 && injury.MagicDamageTakenIncreaseBasisPoints == 0 &&
                    injury.PhysicalDamageReduction == 0m, "injury retains one exact incoming channel");
                Check.True(GenericStatusCatalog.TryResolve(140, out var weakness, out _) &&
                    weakness.PhysicalDamageTakenIncreaseBasisPoints == 500 && weakness.MagicDamageTakenIncreaseBasisPoints == 500,
                    "weakness retains both exact incoming channels");
                Check.True(SkillStatusEffectCatalog.TryGet(90, out var ward), "native mitigation exists");
                foreach (var status in new[] { injury, weakness, ward })
                    Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(targetSocket.Session,
                        status with { OperatorApplied = true }, now, "amplification-check", CancellationToken.None), "status applies");
                Check.True(registry.TryGetRuntimeIncomingDamageMitigation(targetSocket.Session, now, out var mitigation) &&
                    mitigation.PhysicalDamageTakenIncreaseBasisPoints == 1_500 && mitigation.MagicDamageTakenIncreaseBasisPoints == 500 &&
                    mitigation.PhysicalDamageReduction == 0.1m, "mitigation and amplification compose independently");
                foreach (var kind in new[] { MonsterAttackDamageKind.Physical, MonsterAttackDamageKind.Magical })
                {
                    var profile = MonsterCombatProfileCatalog.Resolve(120, kind);
                    var eventId = Enumerable.Range(1, 1_000).Select(static id => (ulong)id)
                        .First(id => MonsterIncomingCombatPolicy.ResolveAttack(profile, target, mitigation, id).Hit);
                    var damage = MonsterIncomingCombatPolicy.ResolveAttack(profile, target, mitigation, eventId);
                    var multiplier = kind == MonsterAttackDamageKind.Physical ? 1.15m : 1.05m;
                    Check.True(damage.Evidence.DamageAfterReduction > 0m &&
                        damage.Evidence.DamageAfterTakenIncrease == damage.Evidence.DamageAfterReduction * multiplier,
                        "actual monster attack consumes exactly the matching incoming amplification");
                }
                var revision = FindRevision(attacker, target, static resolution => resolution.Hit);
                var decision = await registry.ResolvePvpBasicAttackAsync(attackerSocket.Session,
                    WorldObjectIds.ForPlayer(target.Id), attacker.PositionX, attacker.PositionZ, revision, now, CancellationToken.None);
                Check.True(decision.Accepted && decision.Resolution.Hit && decision.AppliedDamage > 0 &&
                    decision.Resolution.Evidence.DamageAfterTakenIncrease == decision.Resolution.Evidence.DamageAfterReduction * 1.15m,
                    "actual PvP commits damage with the same runtime modifier");
                var stale = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(targetSocket.Session, now,
                    CancellationToken.None), injury.StatusId);
                Check.True(GenericStatusCatalog.TryResolve(131, out var replacement, out _), "stronger rank exists");
                await registry.ApplyRuntimeStatusAndPublishAsync(targetSocket.Session, replacement with { OperatorApplied = true },
                    now.AddSeconds(1), "injury-recast", CancellationToken.None);
                Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(targetSocket.Session, stale, now.AddSeconds(1),
                    CancellationToken.None))["ok"]!.GetValue<bool>(), "stale token cannot remove the recast");
                var current = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(targetSocket.Session, now.AddSeconds(1),
                    CancellationToken.None), replacement.StatusId);
                Check.True((await registry.RemoveRuntimeBuffForEditorAsync(targetSocket.Session, current, now.AddSeconds(1),
                    CancellationToken.None))["removed"]!.GetValue<bool>(), "exact token removes the amplifier");
                Check.True(registry.TryGetRuntimeIncomingDamageMitigation(targetSocket.Session, now.AddSeconds(1), out var remaining) &&
                    remaining.PhysicalDamageTakenIncreaseBasisPoints == 500 && remaining.MagicDamageTakenIncreaseBasisPoints == 500,
                    "removal reverses only its own contribution");
                var nativeToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(targetSocket.Session, now.AddSeconds(1),
                    CancellationToken.None), weakness.StatusId);
                await registry.RemoveRuntimeBuffForEditorAsync(targetSocket.Session, nativeToken, now.AddSeconds(1), CancellationToken.None);
                await registry.ApplyRuntimeStatusAndPublishAsync(targetSocket.Session, weakness, now.AddSeconds(1), "native-source", CancellationToken.None);
                nativeToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(targetSocket.Session, now.AddSeconds(1),
                    CancellationToken.None), weakness.StatusId);
                Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(targetSocket.Session, nativeToken, now.AddSeconds(1),
                    CancellationToken.None))["ok"]!.GetValue<bool>(), "native source retains removal ownership");
                Check.True(registry.TryGetRuntimeIncomingDamageMitigation(targetSocket.Session, now.AddSeconds(10), out var expired) &&
                    expired.PhysicalDamageTakenIncreaseBasisPoints == 0 && expired.MagicDamageTakenIncreaseBasisPoints == 0,
                    "expiry removes gameplay amplification in either runtime mode");
                // ROUND3: trusted internal producers still cannot overflow or bypass the existing combat cap.
                foreach (var kind in new[] { 90_000, 90_001 })
                    await registry.ApplyRuntimeStatusAndPublishAsync(targetSocket.Session,
                        injury with { Kind = kind, StatusId = (uint)kind, PhysicalDamageTakenIncreaseBasisPoints = int.MaxValue },
                        now.AddSeconds(10), "bounded-internal-source", CancellationToken.None);
                Check.True(registry.TryGetRuntimeIncomingDamageMitigation(targetSocket.Session, now.AddSeconds(10), out var capped) &&
                    capped.PhysicalDamageTakenIncreaseBasisPoints == 8_000 && capped.MagicDamageTakenIncreaseBasisPoints == 0,
                    "combined incoming amplification saturates at the reviewed 80% without integer overflow");
            }
            finally
            {
                registry.Remove(attackerSocket.Session);
                registry.Remove(targetSocket.Session);
            }
        }
    }
}
