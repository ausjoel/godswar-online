// ROUND3: bounded operator riding, using native equipment, durable MP and exact lifetime authority.
using System.Text.Json.Nodes;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<string> ApplyOperatorMountForAdminAsync(
        SkillStatusEffectDefinition definition, CancellationToken cancellationToken)
    {
        string Refused(string reason) => new JsonObject
        { ["ok"] = false, ["applied"] = false, ["error"] = reason }.ToJsonString();
        var character = _character;
        if (character is null || _account is null) return Refused("not in the world");
        var now = DateTimeOffset.UtcNow;
        if (HasPendingSkillCast || ResolvePlayerSkillCastControl(now) != PlayerSkillCastControl.None)
            return Refused("Cannot activate a mount while casting or under a casting restriction.");
        if (_nextSkillCastAt.TryGetValue(MountCatalog.RideSkillId, out var next) && next > now)
            return Refused("Ride is cooling down. Try again shortly.");
        if (!RequireItemContent().Mounts.TryGetEquippedRideDefinition(character, out var mount) || mount.StatusId != definition.StatusId)
            return Refused("Equip the matching supported mount before applying this riding effect.");
        if (character.Level < mount.MountLevel) return Refused("This character is below the equipped mount's required level.");
        if (_registry.IsRuntimeStatusActive(_session, MountCatalog.RuntimeStatusKind, now))
            return Refused("This character is already riding. Dismount first.");
        if (!_registry.TryGetPlayerLifeRevision(_session, out var life)) return Refused("The character's session changed. Refresh and retry.");
        var activation = await _registry.TryActivateMountRideAndPublishAsync(_session, character.Id, life, mount, now,
            cancellationToken, operatorDuration: definition.Duration);
        if (activation is null) return Refused("Mount activation refused: check HP, MP, equipment and session state.");
        _nextSkillCastAt[MountCatalog.RideSkillId] = now + MountCatalog.RideCooldown;
        if (!ReferenceEquals(_character, activation.Value.Character)) InstallUpdatedCharacter(activation.Value.Character);
        await PublishRideActivationCompletionAsync(activation.Value.Character, mount, MountCatalog.RideSkillId,
            character.PositionX, character.PositionZ, CurrentPlayerObjectId, true, activation.Value.CurrentMana,
            activation.Value.StatusChanged, 0, cancellationToken);
        return new JsonObject
        {
            ["ok"] = true, ["applied"] = true, ["character"] = character.Name,
            ["source"] = "mount", ["statusId"] = mount.StatusId,
            ["requestedDurationSeconds"] = checked((long)definition.Duration.TotalSeconds),
            ["persistence"] = "session", ["manaSpent"] = MountCatalog.RideManaCost
        }.ToJsonString();
    }
}
