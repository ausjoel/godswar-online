using System.Globalization;
using System.Text.Json.Nodes;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record OperatorApplyBuffAction(
    string ActorName,
    SkillStatusEffectDefinition Definition) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// ROUND3 (merged with FINALISE2): one ownership rule for an operator status id. Class-skill, item and
    /// status-data rows of <see cref="OperatorBuffCatalog"/> keep their gameplay definitions. The audited
    /// <see cref="GenericStatusCatalog"/> owns rows the operator catalogue lacks (native mounts, EXP layers) and
    /// the generated status-extra/hostile rows whose complete audited contract it can apply (periodic HP loss,
    /// incoming damage amplification, control flags, EXP). Any other id falls back to the operator catalogue.
    /// </summary>
    internal static bool TryResolveAuditedOperatorStatus(uint statusId, out SkillStatusEffectDefinition definition)
    {
        definition = default;
        var listed = OperatorBuffCatalog.TryGetByStatus(statusId, out var entry);
        if (listed &&
            entry.Source is OperatorBuffCatalog.SourceClassSkill or OperatorBuffCatalog.SourceItem or OperatorBuffCatalog.SourceStatusData)
        {
            return false;
        }
        if (statusId > int.MaxValue || !GenericStatusCatalog.TryResolve((int)statusId, out var audited, out _))
            return false;
        // A listed row is overridden only to ADD what the operator catalogue leaves icon-only (HP loss,
        // amplification, control flags, EXP); every value both catalogues carry must agree, so a value the
        // operator catalogue deliberately left out (e.g. 235-237 "unit unconfirmed") keeps main's behaviour.
        if (listed && audited with
            {
                HpDamagePerPulse = 0, DamageIntervalSeconds = 0, Control = HostileStatusControlFlags.None,
                ExperienceBonuses = null, PhysicalDamageTakenIncreaseBasisPoints = 0, MagicDamageTakenIncreaseBasisPoints = 0
            } != entry.Definition)
        {
            return false;
        }
        definition = audited;
        return true;
    }

    internal static string BuildOperatorBuffCatalogueJson()
    {
        var rows = new JsonArray();
        foreach (var entry in OperatorBuffCatalog.All.OrderBy(value => value.Definition.StatusId))
        {
            var definition = entry.Definition;
            var row = new JsonObject
            {
                ["source"] = entry.Source,
                ["name"] = entry.Name,          // FINALISE2 (F-102): Status.ini name
                ["hostile"] = entry.Hostile,    // FINALISE2
                ["note"] = entry.Note,          // FINALISE2: what the server applies and what it does not
                ["skillId"] = definition.SkillId,
                ["statusId"] = definition.StatusId,
                ["kind"] = definition.Kind,
                ["priority"] = definition.Priority,
                ["beneficial"] = definition.Beneficial,
                ["durationSeconds"] = checked((long)Math.Ceiling(definition.Duration.TotalSeconds)), // FINALISE2: 0.5 s rows say 1
                ["cooldownSeconds"] = checked((long)definition.Cooldown.TotalSeconds),
                // FINALISE2: only the effects a row has (181 rows with every zero field would pass the 64 KiB
                // management response limit; the editor skips missing and zero effects alike).
                ["effects"] = NonZeroEffects(definition)
            };
            // ROUND3: an apply by statusId uses the audited definition, so describe what it really does.
            if (TryResolveAuditedOperatorStatus(definition.StatusId, out var audited))
                DescribeAuditedRow(row, audited);
            rows.Add(row);
        }

        // ROUND3: the additive statuses list exposes every audited row, including explicit non-applicable reasons;
        // applicable audited rows the operator catalogue does not list (mounts, EXP layers) join the buffs list.
        var statuses = new JsonArray();
        foreach (var status in GenericStatusCatalog.All.OrderBy(value => value.StatusId))
        {
            var listed = OperatorBuffCatalog.TryGetByStatus((uint)status.StatusId, out _);
            var reason = GenericStatusCatalog.UnsupportedReason(status);
            var applicable = listed || reason is null;
            statuses.Add(new JsonObject
            {
                ["statusId"] = status.StatusId, ["name"] = status.Name,
                ["kind"] = status.Kind, ["priority"] = status.Priority,
                ["beneficial"] = status.Beneficial, ["durationSeconds"] = status.DurationSeconds,
                ["intervalSeconds"] = status.IntervalSeconds, ["rideId"] = status.RideId,
                ["serverOnly"] = status.ServerOnly, ["applicable"] = applicable,
                ["unsupportedReason"] = applicable ? null : reason,
                ["effects"] = new JsonArray(status.Effects.Select(effect => (JsonNode)new JsonObject
                { ["code"] = effect.Code, ["value"] = effect.Value }).ToArray())
            });
            if (listed || !TryResolveAuditedOperatorStatus((uint)status.StatusId, out var generic))
                continue;
            var row = new JsonObject
            {
                ["source"] = "status", ["name"] = status.Name, ["hostile"] = !generic.Beneficial,
                ["note"] = "", ["skillId"] = 0, ["statusId"] = status.StatusId,
                ["kind"] = generic.Kind, ["priority"] = generic.Priority,
                ["beneficial"] = generic.Beneficial,
                ["durationSeconds"] = checked((long)Math.Ceiling(generic.Duration.TotalSeconds)),
                ["cooldownSeconds"] = 0,
                ["effects"] = NonZeroEffects(generic)
            };
            if (generic.RequiresEquippedMount)
            { // ROUND3: conditional native mounts carry requirements and no invented flat-stat effects.
                const string mountNote = "Requires the matching equipped mount and enough MP. Uses its appearance and speed; costs 50 MP with a 6-second cooldown.";
                row["source"] = "mount";
                row["hostile"] = false;
                row["activation"] = "mount";
                row["activationNote"] = mountNote;
                row["note"] = "Applies: native riding. " + mountNote;
                row["cooldownSeconds"] = checked((long)MountCatalog.RideCooldown.TotalSeconds);
                row["effects"] = new JsonObject();
            }
            else
            {
                DescribeAuditedRow(row, generic);
            }
            rows.Add(row);
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["statusCatalogVersion"] = 1,
            ["sourceSha256"] = GenericStatusCatalog.SourceSha256,
            ["statusCount"] = statuses.Count,
            ["nativeStatusLimits"] = new JsonObject
            {
                ["beneficial"] = PlayerStatusComposer.MaximumBeneficialStatuses,
                ["total"] = PlayerStatusComposer.MaximumTotalStatuses
            },
            ["persistence"] = "session",
            ["stacking"] = "One runtime status per kind; a higher-priority active status blocks a weaker one.",
            ["maxDurationSeconds"] = uint.MaxValue,
            ["buffs"] = rows,
            ["statuses"] = statuses
        }.ToJsonString();
    }

    // ROUND3: effects, note and activation of a row applied through its audited definition.
    private static void DescribeAuditedRow(JsonObject row, SkillStatusEffectDefinition audited)
    {
        var effects = NonZeroEffects(audited);
        void Add(string key, int value) { if (value != 0) effects[key] = value; }
        // ROUND3: expose the actual incoming channels rather than a misleading negative reduction.
        Add("physicalDamageTakenIncreaseBasisPoints", audited.PhysicalDamageTakenIncreaseBasisPoints);
        Add("magicDamageTakenIncreaseBasisPoints", audited.MagicDamageTakenIncreaseBasisPoints);
        Add("fighterExperienceBasisPoints", audited.ExperienceBonuses?.Fighter ?? 0);
        Add("talentExperienceBasisPoints", audited.ExperienceBonuses?.Talent ?? 0);
        Add("petExperienceBasisPoints", audited.ExperienceBonuses?.Pet ?? 0);
        Add("controlFlags", (int)audited.Control);
        var extra = new List<string>();
        void Basis(string name, int bp) { if (bp != 0) extra.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {bp / 100m:+0.#;-0.#}%")); }
        Basis("Physical damage taken", audited.PhysicalDamageTakenIncreaseBasisPoints);
        Basis("Spell damage taken", audited.MagicDamageTakenIncreaseBasisPoints);
        Basis("EXP", audited.ExperienceBonuses?.Fighter ?? 0);
        Basis("Talent EXP", audited.ExperienceBonuses?.Talent ?? 0);
        Basis("Pet EXP", audited.ExperienceBonuses?.Pet ?? 0);
        if (audited.Control != HostileStatusControlFlags.None)
            extra.Add("controls: " + audited.Control.ToString());
        if (audited.HpDamagePerPulse > 0) // ROUND3: compact, explicit harmful pulse contract within the response bound.
        {
            var damageNote = $"Loses {audited.HpDamagePerPulse} HP every {audited.DamageIntervalSeconds} seconds. Ends on death or a change of character life. Administrative effect; no player kill rewards.";
            row["activation"] = "periodic-damage";
            row["activationNote"] = damageNote;
            effects["hpDamagePerPulse"] = audited.HpDamagePerPulse;
            effects["damageIntervalSeconds"] = audited.DamageIntervalSeconds;
            extra.Add($"HP -{audited.HpDamagePerPulse} every {audited.DamageIntervalSeconds} seconds (ends on death or a change of character life; no kill rewards)");
        }
        row["effects"] = effects;
        // Describe() reports stats and id-enforced controls; prepend what only the audited definition applies.
        const string prefix = "Applies: ";
        const string none = "no stat change";
        var described = OperatorBuffCatalog.Describe(audited, null);
        if (extra.Count > 0)
        {
            var rest = described[prefix.Length..];
            rest = rest.StartsWith(none, StringComparison.Ordinal) ? rest[none.Length..] : ", " + rest;
            described = prefix + string.Join(", ", extra) + rest;
        }
        row["note"] = described;
    }

    // FINALISE2: the "effects" object of one catalogue row, zero fields left out.
    private static JsonObject NonZeroEffects(SkillStatusEffectDefinition definition)
    {
        var effects = new JsonObject();
        void Add(string key, int value) { if (value != 0) effects[key] = value; }
        void AddFraction(string key, decimal value) { if (value != 0) effects[key] = value; }
        Add("hit", definition.HitBonus);
        Add("criticalAppend", definition.CriticalAppendBonus);
        AddFraction("physicalDamageReduction", definition.PhysicalDamageReduction);
        AddFraction("magicDamageReduction", definition.MagicDamageReduction);
        Add("physicalDefense", definition.PhysicalDefenseBonus);
        Add("magicDefense", definition.MagicDefenseBonus);
        Add("dodge", definition.DodgeBonus);
        Add("criticalResistance", definition.CriticalResistanceBonus);
        Add("physicalAttack", definition.PhysicalAttackBonus);
        Add("magicAttack", definition.MagicAttackBonus);
        Add("physicalDamageBonusBasisPoints", definition.PhysicalDamageBonusBasisPoints);
        Add("magicDamageBonusBasisPoints", definition.MagicDamageBonusBasisPoints);
        Add("hpPerSecond", definition.HpPerSecond);
        Add("mpPerSecond", definition.MpPerSecond);
        return effects;
    }

    internal static bool TryReadOperatorBuff(
        JsonObject body,
        out SkillStatusEffectDefinition definition,
        out string error)
    {
        definition = default;
        error = "statusId (or skillId of a class buff) must identify a buff in the operator catalogue";
        // ROUND3: a request has one unambiguous identity; client-supplied effect values
        // never become gameplay data.
        if (body.ContainsKey("skillId") && body.ContainsKey("statusId"))
        {
            error = "Provide exactly one of skillId or statusId";
            return false;
        }
        // statusId is the key for every row; skillId remains for the original 25 class-skill buffs.
        if (Int(body, "statusId") is { } statusId)
        {
            if (statusId < 1)
                return false;
            if (TryResolveAuditedOperatorStatus((uint)statusId, out var audited))
            {
                definition = audited;
            }
            else if (OperatorBuffCatalog.TryGetByStatus((uint)statusId, out var entry))
            {
                definition = entry.Definition;
            }
            else
            {
                // ROUND3: say why an audited status cannot be applied.
                if (GenericStatusCatalog.TryGet(statusId, out var row) && GenericStatusCatalog.UnsupportedReason(row) is { } reason)
                    error = reason;
                return false;
            }
        }
        else if (Int(body, "skillId") is not { } id || !OperatorBuffCatalog.TryGetBySkill(id, out var skillEntry))
        {
            return false;
        }
        else
        {
            definition = skillEntry.Definition;
        }
        // FINALISE2 (the admin F-102 "add them all"): hostile rows can be applied from the editor too (to test players).
        if (body.ContainsKey("durationSeconds"))
        {
            if (Long(body, "durationSeconds") is not { } seconds || seconds < 1 || seconds > uint.MaxValue)
            {
                error = "durationSeconds must be a whole number from 1 to 4294967295 (native uint32 seconds)";
                return false;
            }
            definition = definition with { Duration = TimeSpan.FromSeconds(seconds) };
        }
        error = string.Empty;
        return true;
    }
}
