using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetHealingTalentLiveAdapterChecks
{
    private static void CheckPreCharacterFailureRestoresIncomingReplay()
    {
        var adapter = new PlayerVitalsDamageEcsAdapter(
            new ProcessPetHealingCooldownStore());
        var character = Character();
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        var request = Request(
            eventId: 91,
            character,
            objectId,
            resolvedAt: Start,
            damage: 50);

        var threw = false;
        try
        {
            _ = adapter.Apply(
                character,
                objectId,
                currentLifeRevision: 0,
                request,
                beforeLethalCommit: () =>
                {
                    character.CurrentHp = 1;
                    character.VitalsRevision = 9;
                    throw new InvalidOperationException(
                        "Injected pre-copy death claim fault.");
                });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Check.True(threw,
            "pre-copy callback fault is surfaced");
        Check.Equal(50, character.CurrentHp,
            "pre-copy callback fault leaves Character HP unchanged");
        Check.Equal(0L, character.VitalsRevision,
            "pre-copy callback fault leaves Character vitals unchanged");

        var retry = adapter.Apply(
            character,
            objectId,
            currentLifeRevision: 0,
            request);
        Check.True(retry.Applied && retry.Killed,
            "same event remains retryable after pre-copy callback fault");
        Check.Equal(91UL, retry.LastAttackEventId,
            "retry commits the restored incoming replay identity");
    }

    private static void CheckPetCooldownFailureRestoresIncomingTransaction()
    {
        var cooldowns = new ProcessPetHealingCooldownStore();
        var adapter = new PlayerVitalsDamageEcsAdapter(cooldowns);
        adapter.UpdateActivePet(
            new PetHealingTalentHydrationSnapshot(
                PetId: 70,
                Level: 120,
                Aptitude: (short)PetAptitude.Transcendent,
                TalentMask: 8,
                IsCarried: true,
                IsSummoned: true));
        var character = Character();
        character.VitalsRevision = long.MaxValue - 1;
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        var failedRequest = Request(
            eventId: 92,
            character,
            objectId,
            resolvedAt: Start,
            damage: 10);

        var overflowed = false;
        try
        {
            _ = adapter.Apply(
                character,
                objectId,
                currentLifeRevision: 0,
                failedRequest);
        }
        catch (OverflowException)
        {
            overflowed = true;
        }

        Check.True(overflowed,
            "post-scheduler pre-copy revision failure is surfaced");
        Check.Equal(50, character.CurrentHp,
            "failed pet-heal tick leaves Character HP unchanged");
        Check.Equal(long.MaxValue - 1, character.VitalsRevision,
            "failed pet-heal tick leaves Character revision unchanged");
        Check.Equal(0, cooldowns.Count,
            "failed pet-heal tick releases its process cooldown claim");

        character.VitalsRevision = 0;
        var retryRequest = failedRequest with
        {
            ExpectedVitalsRevision = 0
        };
        var retry = adapter.Apply(
            character,
            objectId,
            currentLifeRevision: 0,
            retryRequest);
        Check.True(retry.Applied && retry.PetHealing is not null,
            "same event and Healing cooldown remain retryable after rollback");
        Check.Equal(65, character.CurrentHp,
            "retried damage commits its exact pet-healed final HP");
    }
}
