using System.Text.Json.Nodes;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.Awards;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W4-B (the admin 2026-11-12): what a character gets when it enters the world.
///  * Login-window awards (login_window_awards): each open award the character (or its account) has not had yet is
///    claimed and paid in one transaction with the character row locked, so a reconnect, a second session or a
///    second character of the account cannot take it twice. A full bag sends the award's items to the Prize Chest.
///  * Buffs queued for this character (pending_character_buffs, from the editor or a login award) are applied now,
///    so their duration starts at this login. They are session statuses: they end at logout.
///  * One chat line per award and one reminder when the Prize Chest holds something. No pop-up (the admin's rule).
/// Runs once per connection and character, at the first EnterUiReady (the client sends 10357 after map changes too).
/// </summary>
internal sealed partial class GameClientHandler
{
    private int _loginAwardsCharacterId;

    private async Task RunLoginAwardsAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character || _account is not { } account ||
            _loginAwardsCharacterId == character.Id)
        {
            return;
        }

        _loginAwardsCharacterId = character.Id;
        if (EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        // Each part is isolated: a failure is logged and never breaks world entry or the other parts.
        await GuardLoginStepAsync("login-award", () => Finalise2Rates.On(Finalise2RateKeys.LoginAwards)
            ? GrantLoginAwardsAsync(store, account.Id, character, cancellationToken)
            : Task.CompletedTask, character, cancellationToken);
        await GuardLoginStepAsync("queued-buff", () => Finalise2Rates.On(Finalise2RateKeys.QueuedBuffs)
            ? ApplyQueuedBuffsAsync(store, cancellationToken)
            : Task.CompletedTask, character, cancellationToken);
        await GuardLoginStepAsync("prize-chest", () => Finalise2Rates.On(Finalise2RateKeys.PrizeChest)
            ? RemindPrizeChestAsync(store, cancellationToken)
            : Task.CompletedTask, character, cancellationToken);
    }

    private static async Task GuardLoginStepAsync(string tag, Func<Task> step, GameCharacter character,
        CancellationToken cancellationToken)
    {
        try
        {
            await step();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[{tag}] login step failed character={character.Name}: {error.GetType().Name} {error.Message}");
        }
    }

    private async Task GrantLoginAwardsAsync(PostgresEventsNpcStore store, int accountId, GameCharacter character,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LoginAwardGrant> grants;
        try
        {
            grants = await store.RunAsync(character.Id, EventsNpcDay(EventsNpcNow),
                tx => LoginAwardLedger.GrantOpenAwardsAsync(tx, DateTimeOffset.UtcNow, accountId, character.Id,
                    character.Level), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Nothing was committed: the awards stay unclaimed and are tried again at the next login.
            Console.WriteLine($"[login-award] skipped character={character.Name}: {error.GetType().Name} {error.Message}");
            return;
        }

        if (grants.Count == 0)
        {
            return;
        }

        var bagItems = grants.SelectMany(static grant => grant.BagItems).ToArray();
        var walletChanged = grants.Any(static grant => !grant.Award.ToPrizeChest &&
            (grant.Award.Silver > 0 || grant.Award.Gold > 0 || grant.Award.BindingGold > 0));
        var titleGranted = grants.Any(static grant => grant.TitleGranted);
        // A title insert bumps character_base.medusa_reward_revision (trigger), so reload the character for it too.
        if (bagItems.Length > 0 || walletChanged || titleGranted)
        {
            await RefreshAfterEventsNpcAsync(bagItems, cancellationToken);
        }

        var titles = grants.Where(static grant => grant.TitleGranted && grant.Award.TitleId is not null)
            .Select(static grant => (uint)grant.Award.TitleId!.Value).ToArray();
        if (titles.Length > 0 && _character is { } current)
        {
            foreach (var title in titles) current.AddOwnedTitle(title);
            _registry.UpdateCharacter(_session, current, advanceWorldRevision: false);
            await _session.SendAsync(PacketBuilder.MedusaDesignationInfo(current.SelectedTitleId, current.OwnedTitleIds),
                cancellationToken, "LoginAwardTitle");
        }

        foreach (var grant in grants)
        {
            await SendEventsNpcNoticeAsync(LoginAwardPolicy.Describe(grant), "LoginAward", cancellationToken);
        }
    }

    private async Task ApplyQueuedBuffsAsync(PostgresEventsNpcStore store, CancellationToken cancellationToken)
    {
        if (_character is not { } character) return;
        IReadOnlyList<(long Id, int StatusId, long? DurationSeconds)> queued;
        try
        {
            queued = await store.RunAsync(character.Id, EventsNpcDay(EventsNpcNow),
                LoginAwardLedger.ClaimPendingBuffsAsync, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[queued-buff] skipped character={character.Name}: {error.GetType().Name} {error.Message}");
            return;
        }

        foreach (var (id, statusId, durationSeconds) in queued)
        {
            var request = new JsonObject { ["statusId"] = statusId };
            if (durationSeconds is { } seconds) request["durationSeconds"] = seconds;
            string result;
            var giveBack = false;
            if (!GameSessionRegistry.TryReadOperatorBuff(request, out var definition, out var error))
            {
                // The status is no longer in the running catalogue: close the row and say why.
                result = "not applied: " + error;
            }
            else
            {
                var reply = await ApplyOperatorBuffForAdminAsync(definition, cancellationToken);
                JsonObject? parsed = null;
                try { parsed = JsonNode.Parse(reply) as JsonObject; }
                catch (System.Text.Json.JsonException) { /* a plain-text refusal: recorded as is */ }
                var applied = parsed?["applied"] is JsonValue flag && flag.TryGetValue<bool>(out var yes) && yes;
                // Only a dead character gets the buff back for its next login (ROUND3 refuses statuses on the dead);
                // any other refusal (a stronger status, a mount buff without the mount) closes the row.
                giveBack = !applied && _character is { } now && IsOperatorBuffTargetDead(now);
                result = applied ? "applied" : "not applied: " +
                    (parsed?["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) ? reason
                        : parsed?["error"] is JsonValue e && e.TryGetValue<string>(out var text) ? text
                        : LoginAwardPolicy.Clip(reply, 120));
            }

            try
            {
                await store.ExecuteScalarAsync(giveBack
                        ? "UPDATE public.pending_character_buffs SET applied_at = NULL, result = @r WHERE id = @id;"
                        : "UPDATE public.pending_character_buffs SET result = @r WHERE id = @id;",
                    cancellationToken, ("r", LoginAwardPolicy.Clip(result, 256)), ("id", id));
            }
            catch (Exception error2) when (error2 is not OperationCanceledException)
            {
                Console.WriteLine($"[queued-buff] result not saved id={id}: {error2.GetType().Name}");
            }

            Console.WriteLine($"[queued-buff] character={character.Name} id={id} status={statusId} {result}" +
                (giveBack ? " (kept for the next login)" : string.Empty));
        }
    }

    private async Task RemindPrizeChestAsync(PostgresEventsNpcStore store, CancellationToken cancellationToken)
    {
        if (_character is not { } character) return;
        object? waiting;
        try
        {
            waiting = await store.ExecuteScalarAsync(
                "SELECT count(*) FROM public.prize_chest_entries WHERE character_id = @c AND claimed_at IS NULL;",
                cancellationToken, ("c", character.Id));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[prize-chest] count skipped character={character.Name}: {error.GetType().Name}");
            return;
        }

        if (waiting is long count && count > 0)
        {
            await SendEventsNpcNoticeAsync(
                $"{count} prize(s) wait for you at the Prize Chest by the warehouse.", "PrizeChestReminder",
                cancellationToken);
        }
    }
}
