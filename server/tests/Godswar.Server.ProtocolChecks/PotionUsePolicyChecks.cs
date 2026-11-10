using System.Text.Json.Nodes;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PotionUsePolicyChecks
{
    public const string CheckName = "Potion healing plans preserve calculated vitals and concurrent damage";

    public static Task RunAsync()
    {
        var templates = PinnedItemTemplateCatalog.Create("potion-policy", [
            Item(4000, "consume item", "1", "3100"), Item(4001, "consume item", 1, 3101),
            Item(4030, "consume item", "1", "3120"), Item(4990, "head", "1", "3100"),
            Item(4991, "consume item", "0", "3100"), Item(4150, "consume item", "1", "4600"),
            // ROUND3: Candy/Chocolate are reviewed healing; use an unregistered skill for the negative fixture.
            Item(4992, "consume item", "1", int.MaxValue),
            Item(9001, "consume item", "1", "3107"), Item(9002, "consume item", 1, 3126)], [], [], []);
        Check.True(PotionUsePolicy.TryResolve(templates, 4000, out var hp), "native HP item resolves string stats");
        Check.True(PotionUsePolicy.TryResolve(templates, 4001, out var hp500), "native HP item resolves numeric stats");
        Check.Equal(500, hp500.Amount, "native HP amount");
        Check.True(PotionUsePolicy.TryResolve(templates, 4030, out var mp), "native mana item resolves");
        Check.Equal(60, mp.Amount, "native mana amount");
        // ROUND3: retain explicit positive coverage instead of merely removing a negative assertion.
        Check.True(PotionUsePolicy.TryResolve(templates, 9001, out var candy), "reviewed Candy HP effect resolves");
        Check.Equal(2_000, candy.Amount, "Candy restores the reviewed 2000 HP");
        Check.True(candy.Kind == BagConsumableEffectKind.RestoreHitPoints, "Candy is HP healing");
        Check.True(PotionUsePolicy.TryResolve(templates, 9002, out var chocolate), "reviewed Chocolate MP effect resolves");
        Check.Equal(500, chocolate.Amount, "Chocolate restores the reviewed 500 MP");
        Check.True(chocolate.Kind == BagConsumableEffectKind.RestoreManaPoints, "Chocolate is MP healing");
        foreach (var id in new uint[] { 4990, 4991, 4150, 4992, 999999 })
            Check.True(!PotionUsePolicy.TryResolve(templates, id, out _), $"wearable, disabled, money or unknown item {id} rejected");

        Check.True(BagConsumableCooldownPolicy.TryResolve(templates, 4000, out var cooldown), "native HP cooldown resolves");
        Check.Equal(TimeSpan.FromSeconds(1), cooldown.Duration, "HP potion uses native one-second cooldown");
        var before = new PotionVitalsSnapshot(54_000, 9_900, 55_000, 10_000, 12);
        CheckContext(before);
        // ROUND3: include the reviewed Candy/Chocolate skills in headroom-capped planning checks too.
        foreach (var skill in new[] {3100,3101,3102,3103,3104,3105,3106,3107,3108,3120,3121,3122,3123,3124,3125,3126})
        {
            Check.True(BagConsumableEffectCatalog.TryResolve(skill, out var native), "reviewed potion skill exists");
            var nativePlan = Plan(before, native);
            Check.True(nativePlan.AppliedAmount > 0 && nativePlan.AppliedAmount <= native.Amount,
                "each reviewed potion is capped to actual headroom");
        }
        var p = Plan(before, hp);
        Check.Equal(54_200, p.PersistedVitals.Hp, "uses calculated 55k max rather than base 1500");
        Check.Equal(9_900, p.PersistedVitals.Mp, "HP potion does not touch mana");
        Check.Equal(13L, p.ReservedRevision, "DB revision reserved before asynchronous work");
        Check.True(p.HoldsCheckpoints, "ordinary checkpoint writes held until proven result");
        var reserved = before with { Revision = p.ReservedRevision };
        var damaged = reserved with { Hp = 53_700, Mp = 9_870, Revision = 14 };
        Check.True(p.TrySettleCommitted(damaged, out var healed), "settles after concurrent damage and mana expenditure");
        Check.Equal(53_900, healed.Hp, "preserves damage instead of replacing with stale 54200");
        Check.Equal(9_870, healed.Mp, "preserves concurrently spent mana");
        Check.Equal(15L, healed.Revision, "live checkpoint revision follows both damage and DB settlement");
        Check.True(!p.HoldsCheckpoints && p.IsSettled, "checkpoints resume after reconciliation");
        Check.True(!p.TrySettleCommitted(healed with { Hp = 50_000, Revision = 16 }, out var duplicate),
            "same use cannot heal twice even after later damage");
        Check.Equal(50_000, duplicate.Hp, "duplicate leaves newer damage unchanged");

        var capped = Plan(before with { Hp = 54_950 }, hp);
        Check.Equal(50, capped.AppliedAmount, "partial use commits only initial available headroom");
        Check.True(capped.TrySettleCommitted(capped.Before with { Hp = 54_850, Revision = 14 }, out var partial),
            "partial use reconciles concurrent damage");
        Check.Equal(54_900, partial.Hp, "later damage does not expand the committed potion amount");
        var mana = Plan(before, mp);
        Check.True(mana.TrySettleCommitted(before with { Hp = 53_000, Mp = 9_870, Revision = 14 }, out var restoredMp),
            "mana potion reconciles concurrent damage and spell cost");
        Check.Equal(53_000, restoredMp.Hp, "mana potion cannot heal HP");
        Check.Equal(9_930, restoredMp.Mp, "mana potion restores reviewed amount to current MP");

        var dead = Plan(before, hp);
        Check.True(dead.TrySettleCommitted(before with { Hp = 0, Revision = 14 }, out var stillDead),
            "committed use settles even if killed meanwhile");
        Check.Equal(0, stillDead.Hp, "potion never resurrects");
        var deadMana = Plan(before, mp);
        Check.True(deadMana.TrySettleCommitted(before with { Hp = 0, Mp = 100, Revision = 14 }, out var stillDeadMana),
            "mana use settles after death");
        Check.Equal(100, stillDeadMana.Mp, "no mana effect after death");
        var interrupted = Plan(before, hp);
        interrupted.MarkIndeterminate();
        Check.True(interrupted.HoldsCheckpoints && interrupted.IsIndeterminate, "ambiguous commit/cancellation keeps checkpoint hold");
        Check.True(interrupted.TrySettleCommitted(damaged, out var recovered), "inbox commit proof resolves uncertain result");
        Check.Equal(53_900, recovered.Hp, "recovered commit applies once");
        var rollback = Plan(before, hp);
        rollback.MarkIndeterminate();
        Check.True(rollback.TrySettleNotCommitted(damaged, out var unhealed), "proven rollback releases hold");
        Check.Equal(53_700, unhealed.Hp, "rollback cannot add healing or undo damage");
        Check.Equal(15L, unhealed.Revision, "rollback still leaves a distinct next checkpoint revision");
        Check.True(!rollback.TrySettleCommitted(unhealed, out _), "aborted use cannot subsequently apply a stale commit");

        var changedMax = Plan(before, hp);
        Check.True(changedMax.TrySettleCommitted(before with { Hp = 999, MaximumHp = 1000, Revision = 14 }, out var lowerCap),
            "new calculated maximum is respected");
        Check.Equal(1000, lowerCap.Hp, "heal does not exceed changed live maximum");
        var unmarked = Plan(before, hp);
        Check.True(!unmarked.TrySettleCommitted(reserved with { Hp = 53000 }, out _), "rejects conflicting equal revision");
        Check.True(unmarked.HoldsCheckpoints, "invalid reconciliation cannot release checkpoint hold");
        Check.True(!unmarked.TrySettleCommitted(before, out _), "rejects snapshots older than reservation");
        Check.True(!unmarked.TrySettleCommitted(damaged with { Revision = long.MaxValue }, out _), "revision overflow fails closed");

        foreach (var invalid in new[] { before with { Hp = 0 }, before with { Hp = -1 },
            before with { Hp = 55_001 }, before with { Mp = 10_001 }, before with { MaximumHp = 0 },
            before with { MaximumMp = -1 }, before with { Revision = -1 }, before with { Revision = long.MaxValue - 1 },
            before with { Hp = 55_000 } })
            Check.True(!PotionUsePolicy.TryPlan(invalid, hp, out _), "dead, full and invalid inputs cannot consume a potion");
        Check.True(!PotionUsePolicy.TryPlan(before with { Mp = 10_000 }, mp, out _), "full mana rejects without consumption");
        Check.True(!PotionUsePolicy.TryPlan(before, hp with { Amount = 9999 }, out _), "forged native amount rejected");
        Check.True(!PotionUsePolicy.TryPlan(before, new(4600, BagConsumableEffectKind.GrantSilver, 10000), out _),
            "money effect cannot enter potion coordination");
        var ceiling = Plan(new(int.MaxValue - 1, 0, int.MaxValue, 0, long.MaxValue - 2), hp);
        Check.Equal(int.MaxValue, ceiling.PersistedVitals.Hp, "exact integer ceiling cannot overflow");
        Check.True(ceiling.TrySettleCommitted(ceiling.Before with { Revision = ceiling.ReservedRevision }, out var last),
            "last representable settlement revision is valid");
        Check.Equal(long.MaxValue, last.Revision, "revision ceiling is exact");
        return Task.CompletedTask;
    }

    private static void CheckContext(PotionVitalsSnapshot before)
    {
        var context = new PotionUseContext(4000, before);
        var command = new BagItemActivationCommand(Guid.NewGuid(), 3) { PotionContext = context };
        var subject = new CommandSubject(44, 7);
        var connection = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
        var envelope = BagItemActivationCommandEnvelope.Create(subject, connection, DateTimeOffset.UtcNow, command);
        Check.True(CommandEnvelopeValidation.Valid == BagItemActivationCommandEnvelope.Validate(envelope),
            "trusted calculated-vitals context validates");
        var later = BagItemActivationCommandEnvelope.Create(subject, connection, DateTimeOffset.UtcNow,
            command with { PotionContext = context with { Before = before with { Hp = 53_000, Revision = 99 } } });
        Check.Equal(envelope.RequestHash, later.RequestHash, "later vitals do not change logical request hash");
        Check.Equal(envelope.OperationId, later.OperationId, "later vitals do not create a new operation");
        var noContext = BagItemActivationCommandEnvelope.Create(subject, connection, DateTimeOffset.UtcNow,
            command with { PotionContext = null });
        Check.Equal(envelope.RequestHash, noContext.RequestHash, "old commands retain their exact canonical hash");
        foreach (var invalid in new[] { context with { ItemTemplateId = 0 },
            context with { Before = before with { Hp = 0 } },
            context with { Before = before with { Revision = -1 } },
            context with { Before = before with { Revision = long.MaxValue - 1 } },
            context with { Before = before with { MaximumHp = 1500 } } })
        {
            Check.True(CommandEnvelopeValidation.InvalidCommand ==
                BagItemActivationCommandEnvelope.Validate(envelope with { Command = command with { PotionContext = invalid } }),
                "invalid or base-only vitals context rejected before execution");
        }
        Check.True(CommandEnvelopeValidation.InvalidCommand ==
            BagItemActivationCommandEnvelope.Validate(envelope with { Command = command with
            { ExecutionConstraint = BagItemActivationExecutionConstraint.PlayerSkillBookOnly } }),
            "skill-book-only commands cannot carry potion context");
    }

    private static PotionUsePlan Plan(PotionVitalsSnapshot before, BagConsumableEffect effect)
    {
        Check.True(PotionUsePolicy.TryPlan(before, effect, out var plan), "valid potion plan");
        return plan!;
    }

    private static ItemTemplateDefinition Item(uint id, string kind, object use, object skill)
    {
        var json = new JsonObject { ["ID"] = id.ToString(), ["Use"] = JsonValue.Create(use), ["Skill"] = JsonValue.Create(skill) };
        return new(id, kind, "Test" + id, "Test " + id, (short)(kind == "head" ? 12 : 0), [], 1, 200,
            null, kind == "head" ? 12 : 0, "t.gwo", "1,1", json.ToJsonString());
    }
}
