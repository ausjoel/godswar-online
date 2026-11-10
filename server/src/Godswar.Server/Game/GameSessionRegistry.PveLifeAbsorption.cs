using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal PveLifeAbsorptionCommit CommitPveLifeAbsorption(
        ClientSession session,
        GameCharacter character,
        PveLifeAbsorptionCommitter committer,
        IReadOnlyList<PveCommittedMonsterDamage> hits,
        int healingReceivedBasisPoints)
    {
        // Bind the heal to the same life and world membership as the HP
        // mutation, before any persistence or combat-publication awaits.
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var source) ||
                !source.WorldReady ||
                !ReferenceEquals(source.Character, character) ||
                !_playerLifeRevisions.TryGetValue(session, out var life))
            {
                return default;
            }

            return committer.Commit(character, hits, healingReceivedBasisPoints)
                with { SourceContext = source, SourceLifeRevision = life };
        }
    }

    internal bool IsCurrentPveLifeAbsorption(PveLifeAbsorptionCommit commit)
    {
        lock (_gate)
        {
            return commit.Applied && commit.SourceContext is { } source &&
                TryResolveMedusaPublicationContextLocked(
                    source, commit.SourceLifeRevision, out _);
        }
    }

    internal void PublishPveLifeAbsorption(
        PveLifeAbsorptionCommit commit,
        CancellationToken cancellationToken)
    {
        GameSessionContext source;
        WorldInstanceRuntime runtime;
        lock (_gate)
        {
            if (!commit.Applied || commit.HitHealing.IsDefaultOrEmpty ||
                commit.SourceContext is not { } captured ||
                !TryResolveMedusaPublicationContextLocked(
                    captured, commit.SourceLifeRevision, out source!) ||
                !TryGetWorldInstance(source, out runtime!))
            {
                return;
            }

        }

        // World-owner work must not run while holding the registry gate.
        // Each recipient and the source are revalidated at final admission.
        var members = SnapshotReadySessions(runtime, excludeSession: null);
        var recipients = CaptureMonsterAttackPublicationRecipients(runtime, members);
        foreach (var recipient in recipients)
        {
            PublishPveLifeAbsorptionToRecipient(
                runtime, source, recipient, commit, cancellationToken);
        }
    }

    private void PublishPveLifeAbsorptionToRecipient(
        WorldInstanceRuntime runtime,
        GameSessionContext source,
        MonsterAttackPublicationRecipient recipient,
        PveLifeAbsorptionCommit commit,
        CancellationToken cancellationToken)
    {
        Task completion;
        ClientSession? claimedDisconnect = null;
        ExactEgressAdmissionOutcome outcome;
        GameSessionContext target;
        var self = ReferenceEquals(recipient.Context.Session, source.Session);
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime, source, commit.SourceLifeRevision, out source) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime, recipient.Context, recipient.LifeRevision, out target!))
            {
                return;
            }

            // Hold both authority and vitals through non-waiting admission.
            // Another heal/mana update must not make the final HP/MP stale
            // between constructing this batch and owning its queue position.
            var character = source.Character;
            lock (character.VitalsSync)
            {
                if (character.CurrentHp <= 0 ||
                    character.VitalsRevision < commit.AfterVitalsRevision)
                {
                    return;
                }

                var objectId = self ? LocalPlayerObjectId : source.ObjectId;
                // Native negative damage renders green healing. Skill zero
                // needs no cast, impact or new asset. Keep final vitals in the
                // same batch so the per-monster ticks cannot replace final HP.
                var packets = new ReadOnlyMemory<byte>[commit.HitHealing.Length + 1];
                for (var index = 0; index < commit.HitHealing.Length; index++)
                {
                    packets[index] = PacketBuilder.SkillHealing(objectId, objectId,
                        commit.HitHealing[index].AppliedHealing, skillId: 0,
                        character.PositionX, character.PositionZ);
                }
                packets[^1] = PacketBuilder.PlayerVitalsUpdate(objectId,
                    character.CurrentHp, character.CurrentMp);
                // Preserve every native frame, but reserve one stream write.
                // A large AOE must not exhaust the queue's item limit merely
                // because it healed from many monsters at the same instant.
                var feedback = new byte[packets.Sum(static packet => packet.Length)];
                var offset = 0;
                foreach (var packet in packets)
                {
                    packet.Span.CopyTo(feedback.AsSpan(offset));
                    offset += packet.Length;
                }
                outcome = target.Session.TryAdmitExactOutcome(feedback, out completion);
                if (outcome != ExactEgressAdmissionOutcome.Admitted &&
                    target.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = target.Session;
                }
            }
        }

        // Completion and physical disconnect cleanup must run outside locks.
        if (claimedDisconnect is not null)
        {
            CompleteClaimedExactStatusDisconnect(claimedDisconnect);
        }
        if (outcome is ExactEgressAdmissionOutcome.Admitted or
            ExactEgressAdmissionOutcome.AdmittedTerminal)
        {
            ObserveExactAdmissionCompletion(target.Session, completion,
                self ? "PveLifeAbsorptionSelf" : "PveLifeAbsorptionWorld");
        }
    }
}
