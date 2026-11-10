using System.Globalization;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Arena;
using Godswar.Server.Protocol;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// ARENA build: AFK auto-revival (IGG "AFK System", igg-acid=155).
/// </summary>
/// <remarks>
/// Guide text: "This function allows players to revive themselves automatically
/// after they die while in AFK mode. However, each revival costs 10 Gold. Click
/// 'Start Auto-Revival' and set the time when the character will revive. Players
/// can also choose to stop auto-revival under certain circumstances... If players
/// don't have enough Gold in the bag, the auto-revival function will not work."
/// <para>
/// The installed 2.51 client's AFK window (<c>UI/XML/AutoFight.xml</c>,
/// <c>AutoFight.lua</c>) has no revival tab and its <c>AutoFightAPI</c> has no
/// revival call, and the AFK switch never reaches the server. What the client
/// does have is <c>AutoFight_OnUpdate</c>: while AFK runs it hides the death window
/// (<c>ReviveBGWin</c>) as soon as the player's HP comes back, which fits a
/// server-side revival. So the setting is a chat command, kept for the session:
/// <c>/autorevive on [seconds] [limit]</c>, <c>/autorevive off</c>,
/// <c>/autorevive</c> (status). The server cannot see whether the client is in AFK
/// mode, so an armed setting revives after any death outside instances and the
/// Duel Arena.
/// </para>
/// <para>
/// GENERATED (the guide gives none of these): the default delay (10 seconds, the
/// length of the client's own free-revival countdown), the allowed delay range
/// (5-300 s), the optional limit (stop after N revivals), reviving on the spot,
/// and the 10 % HP/MP restored (the same share as the free revival).
/// The 10 Gold price is the guide's (the GodsArena wiki says 15);
/// <c>GODSWAR_AFK_REVIVAL_GOLD</c> changes it.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    internal const int AfkRevivalDefaultDelaySeconds = 10;
    internal const int AfkRevivalMinimumDelaySeconds = 5;
    internal const int AfkRevivalMaximumDelaySeconds = 300;
    private static readonly string[] AfkRevivalCommandWords = ["/autorevive"];

    private AfkRevivalSetting? _afkRevival;
    private DateTimeOffset? _afkDeadSince;
    private bool _afkShortNoticeSent;
    private CancellationTokenSource? _afkRevivalCancellation;
    private Task? _afkRevivalTask;

    internal sealed record AfkRevivalSetting(int DelaySeconds, int Limit)
    {
        public int Used { get; set; }
    }

    /// <summary>
    /// Consumes <c>/autorevive ...</c> when the gate is on. With the gate off the
    /// line is not recognised and reaches chat exactly as before.
    /// </summary>
    private async Task<bool> TryHandleAfkRevivalCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!ArenaFeatureGates.AfkRevival ||
            _character is null ||
            !TryReadTalkText(packet.Payload, out var text))
        {
            return false;
        }

        var offset = DeveloperCommandText.FindCommandOffset(
            text, AfkRevivalCommandWords, out var word);
        if (offset < 0)
        {
            return false;
        }

        var args = text[offset..].Trim()[word.Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var (setting, reply) = ParseAfkRevivalCommand(
            args, _afkRevival, ArenaFeatureGates.AfkRevivalGold);
        _afkRevival = setting;
        _afkDeadSince = null;
        _afkShortNoticeSent = false;
        if (setting is not null)
        {
            StartAfkRevival();
        }

        Console.WriteLine(
            $"[afk-revive] setting character={_character.Name} " +
            $"armed={setting is not null} delay={setting?.DelaySeconds} limit={setting?.Limit}");
        await _session.SendAsync(
            PacketBuilder.ServerNote(reply), cancellationToken, "AfkRevivalSetting");
        return true;
    }

    /// <summary>Pure parser (checked directly): the new setting and the reply line.</summary>
    internal static (AfkRevivalSetting? Setting, string Reply) ParseAfkRevivalCommand(
        IReadOnlyList<string> args,
        AfkRevivalSetting? current,
        int price)
    {
        static string Describe(AfkRevivalSetting s, int price) =>
            $"Auto-revival is ON: {s.DelaySeconds} s after death, {price} Gold each" +
            (s.Limit > 0 ? $", at most {s.Limit} times ({s.Used} used)." : ".");

        if (args.Count == 0)
        {
            return (current, current is null
                ? $"Auto-revival is OFF. Type /autorevive on [seconds] [limit]. Each revival costs {price} Gold."
                : Describe(current, price));
        }

        switch (args[0].ToLowerInvariant())
        {
            case "off":
            case "stop":
                return (null, "Auto-revival is OFF.");
            case "on":
            case "start":
                var delay = AfkRevivalDefaultDelaySeconds;
                var limit = 0;
                if (args.Count > 1 &&
                    (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out delay) ||
                     delay is < AfkRevivalMinimumDelaySeconds or > AfkRevivalMaximumDelaySeconds))
                {
                    return (current,
                        $"Revival time must be {AfkRevivalMinimumDelaySeconds}-{AfkRevivalMaximumDelaySeconds} seconds.");
                }
                if (args.Count > 2 &&
                    (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                     limit is < 0 or > 1000))
                {
                    return (current, "The limit must be 0-1000 revivals (0 = no limit).");
                }
                var setting = new AfkRevivalSetting(delay, limit);
                return (setting, Describe(setting, price));
            default:
                return (current, "Usage: /autorevive on [seconds] [limit], /autorevive off");
        }
    }

    private void StartAfkRevival()
    {
        if (_afkRevivalTask is { IsCompleted: false })
        {
            return;
        }

        _afkRevivalCancellation?.Dispose();
        _afkRevivalCancellation = new CancellationTokenSource();
        _afkRevivalTask = RunAfkRevivalAsync(_afkRevivalCancellation.Token);
    }

    /// <summary>Joined in RunAsync's finally, before the character gate is disposed.</summary>
    private async Task StopAfkRevivalAsync()
    {
        if (_afkRevivalCancellation is null)
        {
            return;
        }

        _afkRevivalCancellation.Cancel();
        try
        {
            if (_afkRevivalTask is not null)
            {
                await _afkRevivalTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        _afkRevivalCancellation.Dispose();
        _afkRevivalCancellation = null;
    }

    private async Task RunAfkRevivalAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (_afkRevival is null || !ArenaFeatureGates.AfkRevival)
                    {
                        return;
                    }

                    await TickAfkRevivalAsync(cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[afk-revive] tick failed character={_character?.Name}: {error.Message}");
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TickAfkRevivalAsync(CancellationToken cancellationToken)
    {
        var setting = _afkRevival!;
        if (_character is null || _account is null)
        {
            return;
        }

        if (_character.CurrentHp > 0)
        {
            _afkDeadSince = null;
            _afkShortNoticeSent = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _afkDeadSince ??= now;
        if (now - _afkDeadSince.Value < TimeSpan.FromSeconds(setting.DelaySeconds) ||
            !_registered ||
            !_worldPresenceAnnounced ||
            _pendingMapTransition is not null)
        {
            return;
        }

        var map = _character.CurrentMap;
        if (DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(map) ||
            DynamicDungeonContentMapPolicy.IsMedusaMap(map) ||
            map == DuelArenaCombatArea.MapId)
        {
            return;
        }

        var price = ArenaFeatureGates.AfkRevivalGold;
        if (_character.Gold < price)
        {
            if (!_afkShortNoticeSent)
            {
                _afkShortNoticeSent = true;
                await _session.SendAsync(
                    PacketBuilder.ServerNote($"Auto-revival needs {price} Gold."),
                    cancellationToken, "AfkRevivalNoGold");
            }
            return;
        }

        var balance = await AfkRevivalWallet.TryDebitGoldAsync(
            _character.Id, _account.Id, price, cancellationToken);
        if (balance is null)
        {
            if (!_afkShortNoticeSent)
            {
                _afkShortNoticeSent = true;
                await _session.SendAsync(
                    PacketBuilder.ServerNote($"Auto-revival needs {price} Gold."),
                    cancellationToken, "AfkRevivalNoGold");
            }
            return;
        }

        bool revived;
        try
        {
            revived = await TryAfkReviveInPlaceAsync(cancellationToken);
        }
        catch
        {
            // A failure after the charge (a disconnect while sending, say) must
            // not keep the Gold: give it back, then let the tick log the error.
            // If the character did come back to life, the revival stands and the
            // refund still happens, so the player is never charged for an error.
            var refunded = await AfkRevivalWallet.RefundGoldAsync(
                _character.Id, _account.Id, price, CancellationToken.None);
            _character.Gold = refunded ?? balance.Value;
            throw;
        }

        if (!revived)
        {
            balance = await AfkRevivalWallet.RefundGoldAsync(
                _character.Id, _account.Id, price, CancellationToken.None) ?? balance;
        }

        _character.Gold = balance.Value;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(), cancellationToken, "AfkRevivalWallet");
        _afkDeadSince = null;
        if (!revived)
        {
            return;
        }

        setting.Used++;
        var limitReached = setting.Limit > 0 && setting.Used >= setting.Limit;
        await _session.SendAsync(
            PacketBuilder.ServerNote(
                $"Auto-revival: revived for {price} Gold." +
                (limitReached ? " Limit reached, auto-revival is now OFF." : string.Empty)),
            cancellationToken, "AfkRevivalDone");
        if (limitReached)
        {
            _afkRevival = null;
        }
    }

    /// <summary>
    /// Revives the dead character where it lies: the same messages as the free
    /// revival (10025 fight reset, 10097 vitals, then the same-map 10018 scene
    /// change through the ordinary readiness flow), with the death position as
    /// the landing. False, with nothing changed, if the move is refused.
    /// </summary>
    private async Task<bool> TryAfkReviveInPlaceAsync(CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!_registry.TryGetPlayerLifeRevision(_session, out _))
        {
            return false;
        }

        var deathMap = character.CurrentMap;
        var x = character.PositionX;
        var z = character.PositionZ;
        if (!float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        var lifeRevision = _registry.AdvancePlayerLifeRevision(_session);
        if (lifeRevision < 0)
        {
            return false;
        }

        await _session.SendAsync(
            PacketBuilder.ObjectFightState(LocalPlayerObjectId, engaged: false),
            cancellationToken,
            "AfkRevivalFightStateReset");
        _registry.ClearMonsterAggroForCharacter(
            deathMap, _session, character.Id, DateTimeOffset.UtcNow);

        var restoredHp = Math.Max(1, checked(character.MaxHp * 10 / 100));
        var restoredMp = Math.Max(0, checked(character.MaxMp * 10 / 100));
        await _session.SendAsync(
            PacketBuilder.PlayerVitalsUpdate(LocalPlayerObjectId, restoredHp, restoredMp),
            cancellationToken,
            "AfkRevivalVitals");
        var outcome = await TryBeginSameMapSceneTransitionAsync(
            x,
            z,
            "afk-revive",
            () => _registry.TryGetPlayerLifeRevision(_session, out var current) &&
                  current == lifeRevision,
            cancellationToken);
        Console.WriteLine(
            $"[afk-revive] character={character.Name} map={deathMap} " +
            $"at={x:F1},{z:F1} life={lifeRevision} outcome={outcome}");
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            return false;
        }

        lock (character.VitalsSync)
        {
            character.CurrentHp = restoredHp;
            character.CurrentMp = restoredMp;
            character.MarkVitalsChanged();
        }

        await PersistVitalsCheckpointAsync(character, force: true, cancellationToken);
        return true;
    }
}
