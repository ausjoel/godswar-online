using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

/// <summary>
/// Shared handler/combat checkpoint hold. Every transition uses the owning character's
/// VitalsSync; no monitor is held during database I/O or while awaiting resolution.
/// </summary>
internal sealed class PendingPotionVitals
{
    private readonly GameCharacter _character;
    private readonly TaskCompletionSource<bool> _resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PendingPotionVitals(GameCharacter character, uint template, PlayerOwnershipFence owner, PotionUsePlan plan)
    { _character = character; ItemTemplateId = template; Ownership = owner; Plan = plan; }

    public uint ItemTemplateId { get; }
    public PlayerOwnershipFence Ownership { get; }
    public PotionUsePlan Plan { get; }
    public Task<bool> Resolution => _resolved.Task;
    public bool HoldsCheckpoints => Plan.HoldsCheckpoints;

    public static bool TryPrepare(GameCharacter character, uint item, BagConsumableEffect effect,
        PlayerOwnershipFence owner, out PendingPotionVitals? pending, out PotionUseContext? context)
    {
        pending = null; context = null;
        lock (character.VitalsSync)
        {
            if (character.PendingPotionUse is { HoldsCheckpoints: true } || !owner.IsValid ||
                character.CheckpointOwnerId != owner.OwnerId || character.CheckpointOwnerGeneration != owner.Generation)
                return false;
            var before = Snapshot(character);
            var candidate = new PotionUseContext(item, before);
            if (candidate.IsValid) context = candidate;
            if (!PotionUsePolicy.TryPlan(before, effect, out var plan)) return true;
            // Checkpoint contracts require a positive revision. A newly hydrated, injured
            // character may still have zero; make its before-snapshot persistable first.
            if (before.Revision == 0)
            {
                character.MarkVitalsChanged();
                before = Snapshot(character);
                if (!PotionUsePolicy.TryPlan(before, effect, out plan)) return false;
                context = new(item, before);
            }
            pending = new(character, item, owner, plan!);
            character.PendingPotionUse = pending;
            character.VitalsRevision = plan!.ReservedRevision;
            return true;
        }
    }

    public bool TryResolve(bool committed, PotionUseEvidence? evidence = null)
    {
        lock (_character.VitalsSync)
        {
            if (_resolved.Task.IsCompleted || !ReferenceEquals(_character.PendingPotionUse, this) ||
                _character.CheckpointOwnerId != Ownership.OwnerId ||
                _character.CheckpointOwnerGeneration != Ownership.Generation ||
                committed && (evidence is null || !evidence.Matches(Plan, ItemTemplateId, Ownership)))
                return false;
            var current = Snapshot(_character);
            var resolved = committed ? Plan.TrySettleCommitted(current, out var after)
                : Plan.TrySettleNotCommitted(current, out after);
            if (!resolved) return false;
            _character.CurrentHp = after.Hp;
            _character.CurrentMp = after.Mp;
            _character.VitalsRevision = after.Revision;
            _character.PendingPotionUse = null;
            _resolved.TrySetResult(true);
            return true;
        }
    }

    public void AbandonUnresolved()
    {
        lock (_character.VitalsSync)
        {
            if (!HoldsCheckpoints) return;
            Plan.MarkIndeterminate();
            // Keep the hold attached so teardown cannot overwrite an uncertain commit.
            // Wake forced flushes with an explicit failure rather than hanging forever.
            _resolved.TrySetResult(false);
        }
    }

    private static PotionVitalsSnapshot Snapshot(GameCharacter character) => new(character.CurrentHp,
        character.CurrentMp, character.EffectiveMaxHp, character.EffectiveMaxMp, character.VitalsRevision);
}
