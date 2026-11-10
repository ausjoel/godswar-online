using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GameHandlerCheckpointLifecycleChecks
{
    private static async Task
        CheckRuntimeStatsRefreshPreservesLiveVitalsAsync()
    {
        const int accountId = 7005;
        const int characterId = 7005;
        const int liveHp = 196_614;
        const int liveMp = 2_559;
        const int baseCalculatedMaxHp = 183_741;
        const int effectiveMaxHp = 198_440;
        const long liveRevision = 13_407;

        var staleProjection = new CharacterStats
        {
            CharacterId = characterId,
            AccountId = accountId,
            Name = "AresTempest",
            Profession = 1,
            Level = 160,
            MaxHp = baseCalculatedMaxHp,
            MaxMp = liveMp,
            CurrentHp = 1_500,
            CurrentMp = 177
        };
        var store = new StaleVitalsProjectionStore(staleProjection);
        var coordinator = new RecordingCoordinator(
            positionRevision: 0,
            vitalsRevision: liveRevision);
        var character = new GameCharacter
        {
            Id = characterId,
            AccountId = accountId,
            Name = staleProjection.Name,
            Profession = staleProjection.Profession,
            Level = staleProjection.Level,
            MaxHp = effectiveMaxHp,
            MaxMp = liveMp,
            CurrentHp = liveHp,
            CurrentMp = liveMp,
            VitalsRevision = liveRevision,
            CheckpointOwnerId = coordinator.Owner.OwnerId,
            CheckpointOwnerGeneration = coordinator.Owner.Generation
        };
        SetEarthHealthProfile(character);

        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(store: null),
            new RejectingSnapshotReader(),
            WorldContentReaderTestFixtures.Empty,
            characterCheckpoints: coordinator);
        InstallIdentity(handler, accountId, character);
        OwnershipAcquiredField.SetValue(handler, true);

        var refresh = RequiredMethod("RefreshCharacterStatsAsync");
        await InvokeTaskAsync(
            refresh,
            handler,
            character,
            accountId,
            "player-detail",
            CancellationToken.None);

        Check.Equal(liveHp, character.CurrentHp,
            "stale stat refresh preserves exact live HP");
        Check.Equal(liveMp, character.CurrentMp,
            "stale stat refresh preserves exact live MP");
        Check.Equal(effectiveMaxHp, character.MaxHp,
            "stat refresh applies the effective elemental maximum HP");
        Check.Equal(baseCalculatedMaxHp, character.CalculatedStats!.MaxHp,
            "calculated stats retain the non-compounding base maximum HP");
        Check.Equal(liveHp, character.CalculatedStats!.CurrentHp,
            "installed stat projection records preserved live HP");
        Check.Equal(liveMp, character.CalculatedStats.CurrentMp,
            "installed stat projection records preserved live MP");
        Check.Equal(liveRevision + 1, character.VitalsRevision,
            "stat refresh advances only the preserved live-vitals revision");

        var authoritativeStats = character.CalculatedStats;
        var baseOnlyReload = new GameCharacter
        {
            Id = characterId,
            AccountId = accountId,
            Name = staleProjection.Name,
            Profession = staleProjection.Profession,
            Level = staleProjection.Level,
            MaxHp = 1_500,
            MaxMp = 177,
            CurrentHp = liveHp,
            CurrentMp = liveMp,
            VitalsRevision = liveRevision
        };
        Invoke(InstallCharacterMethod, handler, baseOnlyReload);
        var installed =
            (GameCharacter?)CharacterField.GetValue(handler) ??
            throw new InvalidOperationException(
                "Base-only character reload was not installed.");

        Check.Equal(effectiveMaxHp, installed.MaxHp,
            "base-only reload preserves authoritative maximum HP");
        Check.Equal(liveMp, installed.MaxMp,
            "base-only reload preserves authoritative maximum MP");
        Check.Equal(liveHp, installed.CurrentHp,
            "base-only reload preserves exact live HP");
        Check.Equal(liveMp, installed.CurrentMp,
            "base-only reload preserves exact live MP");
        Check.True(
            ReferenceEquals(authoritativeStats, installed.CalculatedStats),
            "base-only reload preserves the authoritative stat projection");
        Check.Equal(liveRevision + 1, installed.VitalsRevision,
            "base-only reload does not synthesize a vitals revision");

        await InvokeAsync(FinalizeOwnershipMethod, handler);
        var checkpoint = coordinator.LastVitalsCheckpoint ??
            throw new InvalidOperationException(
                "Finalization emitted no vitals checkpoint.");
        Check.Equal(liveHp, checkpoint.CurrentHp,
            "shutdown checkpoint retains exact live HP");
        Check.Equal(liveMp, checkpoint.CurrentMp,
            "shutdown checkpoint retains exact live MP");
        Check.Equal(installed.VitalsRevision, checkpoint.Revision,
            "shutdown checkpoint retains refreshed live revision");
    }

    private static async Task InvokeTaskAsync(
        MethodInfo method,
        object target,
        params object?[] arguments)
    {
        try
        {
            await ((Task?)method.Invoke(target, arguments) ??
                throw new InvalidOperationException(
                    $"{method.Name} returned no task."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static void SetEarthHealthProfile(GameCharacter character)
    {
        var rawEffects = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            static _ => 0);
        counts[ElementKind.Earth] = 3;
        var activeTiers = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            element => ElementalResonanceCatalog.ActiveFor(
                element,
                counts[element]));
        var profile = new ElementalEquipmentProfile(
            rawEffects,
            counts,
            activeTiers);
        var property = typeof(GameCharacter).GetProperty(
            nameof(GameCharacter.ElementalEquipment),
            BindingFlags.Instance | BindingFlags.Public) ??
            throw new InvalidOperationException(
                "GameCharacter.ElementalEquipment was not found.");
        property.SetValue(character, profile);
    }

    private sealed class StaleVitalsProjectionStore(
        CharacterStats projection) : GameStoreTestStub
    {
        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterStats?>(projection);
    }
}
