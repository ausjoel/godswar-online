using System.Text.Json.Nodes;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<string> ApplyOperatorBuffForAdminAsync(
        SkillStatusEffectDefinition definition,
        CancellationToken cancellationToken)
    {
        // ROUND3 (rig f2): no administrative status of any kind lands on a dead character; say why (HTTP 409).
        if (_character is { } target && IsOperatorBuffTargetDead(target))
            return DeadTargetRefusal();
        // ROUND3: administrative riding reuses the native transaction; it never grants equipment or toggles an existing ride.
        if (definition.RequiresEquippedMount)
            return await ApplyOperatorMountForAdminAsync(definition, cancellationToken);
        // ROUND3: generic operator effects retain exact-token removal authority.
        definition = definition with { OperatorApplied = true };
        var applied = await _registry.ApplyRuntimeStatusAndPublishAsync(
            _session, definition, DateTimeOffset.UtcNow, "editor-buff", cancellationToken);
        // The registry re-checks life under its own lock; a death in between is refused the same way.
        if (!applied && _character is { } after && IsOperatorBuffTargetDead(after))
            return DeadTargetRefusal();
        var response = new JsonObject
        {
            ["ok"] = true,
            ["character"] = _character!.Name,
            ["applied"] = applied,
            ["source"] = definition.SkillId == 0 ? "status" : "class-skill",
            ["skillId"] = definition.SkillId,
            ["statusId"] = definition.StatusId,
            ["requestedDurationSeconds"] = checked((long)Math.Ceiling(definition.Duration.TotalSeconds)),
            ["persistence"] = "session"
        };
        if (!applied)
            response["reason"] = "Not applied: a stronger status of the same kind is active, or the character's session changed.";
        return response.ToJsonString();
    }

    private static bool IsOperatorBuffTargetDead(GameCharacter character)
    {
        lock (character.VitalsSync) return character.CurrentHp <= 0;
    }

    private static string DeadTargetRefusal() => new JsonObject
    {
        ["ok"] = false, ["applied"] = false,
        ["error"] = "The character is dead. Revive them before applying a status."
    }.ToJsonString();
}
