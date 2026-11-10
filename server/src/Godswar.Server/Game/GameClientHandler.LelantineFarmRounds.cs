using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// Lelantine Farm round rewards. The round clock and settlement run in the registry
// (GameSessionRegistry.LelantineFarmRounds.cs); this is the paying half. Every character in the world checks
// its own unclaimed rewards every 20 s and once on entry, so a player who was online at settlement is paid within
// seconds and one who was offline is paid at the next login. A claim is marked first and put back if paying throws.
internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan FarmRewardClaimInterval = TimeSpan.FromSeconds(20);
    private CancellationTokenSource? _farmRewardCancellation;
    private Task? _farmRewardTask;

    private ILelantineFarmRoundStore? FarmRoundStore => _farmPoints as ILelantineFarmRoundStore;

    private void StartFarmRewardClaims()
    {
        if (FarmRoundStore is null || _farmRewardTask is { IsCompleted: false })
        {
            return;
        }

        _farmRewardCancellation?.Dispose();
        _farmRewardCancellation = new CancellationTokenSource();
        _farmRewardTask = RunFarmRewardClaimsAsync(_farmRewardCancellation.Token);
    }

    private async Task StopFarmRewardClaimsAsync()
    {
        var cancellation = _farmRewardCancellation;
        var task = _farmRewardTask;
        _farmRewardCancellation = null;
        _farmRewardTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunFarmRewardClaimsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FarmRewardClaimInterval);
        try
        {
            do
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (_character is not null && TryCaptureCurrentPlayerOwnership(out _))
                    {
                        await PayFarmRoundRewardsAsync(cancellationToken);
                        await PayTrainingGroundRewardsAsync(cancellationToken); // TRAINING
                        await PayWarriorScrollTasksAsync(cancellationToken); // WARRIOR scrolls (F-146 b)
                        await GrantFarmToolsAsync(cancellationToken);
                        await SendFarmStragglerHomeAsync(cancellationToken); // FINALISE2 (the admin F-66)
                        await SendTroyStragglerHomeAsync(cancellationToken); // W2 F-27 TROY
                        await TickTroyBossesAsync(cancellationToken); // W2 F-27 TROY slice 2: the hero sequence
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[farm] reward claim failed character={_character?.Name}: {error.Message}");
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal const string FarmRoundOverNote = "The Lelantine Farm round is over. You were sent back to your capital.";

    /// <summary>
    /// FINALISE2 (the admin F-66): a player still on the farm when its gate is closed (the round ended at 23:30 AEST, or the
    /// editor closed it) is sent to their own capital, the Returning Helper's destination. Checked every 20 s, so it
    /// happens within 20 s of the end. GMs who may skip the gate stay; a dead character goes at the next tick after
    /// reviving; always-open (event.lelantine_always_open = 1) sends nobody.
    /// </summary>
    private async Task SendFarmStragglerHomeAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character ||
            character.CurrentMap != Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId ||
            character.CurrentHp <= 0 ||
            _pendingMapTransition is not null ||
            BattlefieldSchedulePolicy.IsOpen(
                Godswar.Server.Domain.World.Content.BattlefieldDestinationKind.LelantineFarm,
                _realmCalendar,
                DateTimeOffset.UtcNow))
        {
            return;
        }

        if (Godswar.Server.Application.Gates.GateRules.GmMaySkip(Godswar.Server.Application.Npc.LevelBandGates.LelantineFarm) &&
            await HasOperatorAuthorityAsync(cancellationToken))
        {
            return;
        }

        var targetMapId = character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var outcome = await TryBeginMapTransitionAsync(
            targetMapId,
            GameDefaults.StartingPositionX,
            GameDefaults.StartingPositionZ,
            "farm-round-over",
            cancellationToken);
        Console.WriteLine(
            (outcome == SceneTransitionOutcome.RejectedWithoutRelocation
                ? "[farm] round over, send-home not possible yet (retried in 20 s) character="
                : "[farm] round over, sent home character=") +
            $"{character.Name} map={Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId}->{targetMapId} outcome={outcome}");
        if (outcome == SceneTransitionOutcome.CommittedAwaitingReadiness && !_session.IsDisconnected)
        {
            await _session.SendAsync(PacketBuilder.ServerNote(FarmRoundOverNote), cancellationToken, "FarmRoundOver");
        }
    }

    private async Task PayFarmRoundRewardsAsync(CancellationToken cancellationToken)
    {
        if (FarmRoundStore is not { } store || _character is not { } character || _account is not { } account)
        {
            return;
        }

        var claims = await store.ClaimFarmRoundRewardsAsync(character.Id, cancellationToken);
        foreach (var claim in claims)
        {
            try
            {
                await PayFarmRoundRewardAsync(account.Id, character, claim, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Put the claim back so the next tick (or login) retries it.
                Console.Error.WriteLine(
                    $"[farm] reward payment failed character={character.Name} round={claim.RoundStart:u}: {error.Message}");
                await store.UnclaimFarmRoundRewardAsync(character.Id, claim.RoundStart, CancellationToken.None);
                return;
            }
        }
    }

    private async Task PayFarmRoundRewardAsync(
        int accountId, GameCharacter character, FarmRoundRewardClaim claim, CancellationToken cancellationToken)
    {
        // Character experience and talent (with level-ups and the client's gain packets) through the shared progression
        // path the Zeus Gift events use. Talent points are paid as points, as the page states them: the same
        // points x TalentExperiencePerPoint conversion the Zeus Gift claim uses.
        await PayZeusGiftProgressionAsync(
            claim.CharacterExperience,
            checked(claim.TalentPoints * Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint),
            "farm-round", cancellationToken);

        // Pet experience goes to the summoned pet; the event id makes the payment idempotent per round and character.
        if (claim.PetExperience > 0)
        {
            var id = SHA256.HashData(Encoding.UTF8.GetBytes($"lelantine-farm-round:{claim.RoundStart:u}:{character.Id}"));
            var eventId = new Guid(id.AsSpan(0, 16));
            var pet = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(
                accountId, character.Id, eventId, claim.PetExperience, cancellationToken);
            if (pet is { HasPetProjection: true })
            {
                await _session.SendAsync(
                    PacketBuilder.PetExperience(pet.PetId!.Value, pet.TotalExperience!.Value),
                    cancellationToken,
                    "FarmRoundPetExperience");
            }
        }

        Console.WriteLine(
            $"[farm] round reward paid character={character.Name} round={claim.RoundStart:u} outcome={claim.Outcome} " +
            $"exp={claim.CharacterExperience} pet-exp={claim.PetExperience} talent-points={claim.TalentPoints}");
    }

    /// <summary>
    /// Ninto's free wooden tools, handed out automatically on the farm (see <see cref="LelantineFarmToolPolicy"/>).
    /// The bag change is installed the same way a farm donation's is.
    /// </summary>
    private async Task GrantFarmToolsAsync(CancellationToken cancellationToken)
    {
        if (FarmRoundStore is not { } store || _character is not { } character ||
            character.CurrentMap != Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId ||
            character.Level < LelantineFarmToolPolicy.MinimumLevel)
        {
            return;
        }

        var grant = await store.GrantFarmToolsAsync(character.Id, DateTimeOffset.UtcNow, cancellationToken);
        if (grant.Granted <= 0 || grant.Character is not { } refreshed)
        {
            return;
        }

        InstallUpdatedCharacter(refreshed);
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine($"[farm] free wooden tools character={character.Name} count={grant.Granted}");
    }
}
