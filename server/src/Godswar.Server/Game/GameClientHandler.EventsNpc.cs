using Godswar.Server.Application.EventsNpc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// EVENTSNPC: entry points for the Lost Book, Marathon and Random Quest Manager events. Each hook returns false
// (and touches nothing) while its event's server_rates switch is 0, so the base build's menu-only answers run
// unchanged. Rules and sources: Application/EventsNpc/EventsNpcPolicy.cs and Development/build/eventsnpc/README.md.
internal sealed partial class GameClientHandler
{
    /// <summary>A question or quantity form this handler sent and is waiting to see submitted.</summary>
    private sealed record EventsNpcForm(uint NpcId, int Function, int Entry, int Manual, int Left, int Right,
        DateTimeOffset Expires);

    private EventsNpcForm? _eventsNpcForm;

    /// <summary>Every word of the current request, logged with form submissions (the typed field is UNPROVEN).</summary>
    private string _eventsNpcWords = string.Empty;

    private static string EventsNpcWords(Godswar.Server.Protocol.GamePacket packet)
    {
        var payload = packet.Payload;
        var words = new System.Text.StringBuilder();
        for (var offset = 0; offset + 4 <= payload.Length; offset += 4)
        {
            words.Append(" p").Append(offset).Append('=')
                .Append(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));
        }

        return words.ToString();
    }

    /// <summary>The event NPCs to add to a map at entry (one-line hook in LoginWorldEntry).</summary>
    private static IReadOnlyList<NpcSpawnDefinition> EventsNpcSpawns(short mapId,
        IReadOnlyList<NpcSpawnDefinition> existing) =>
        EventsNpcSpawnPolicy.For(mapId, existing, EventsNpcRates.LostBookOn, EventsNpcRates.MarathonOn);

    /// <summary>
    /// One click on an event NPC's scripted window (hook in HandleScriptedNpcDialogueAsync). The path is the
    /// request's +12.. click slots (each entry clicked, in order); the amount is the +0x38 typed number.
    /// </summary>
    private async Task<bool> TryHandleEventsNpcDialogueAsync(NpcSpawnDefinition npc, int dialogIndex, int selection,
        int[] path, int amount, string words, CancellationToken cancellationToken)
    {
        _eventsNpcWords = words;
        if (_character is null || _account is null)
        {
            return false;
        }

        // The path is the request's click slots (+12 first click, +16 second, ...; docs/zeus-gift-capture-analysis.md
        // 2.2): the latest click is the last one. A request for a fresh page has -1 in every slot.
        selection = path.Length > 0 ? path[^1] : -1;
        try
        {
            return dialogIndex switch
            {
                EventsNpcPolicy.OldManFunction when EventsNpcRates.LostBookOn && npc.NpcKey.EndsWith("_083") =>
                    await HandleMysteriousElderAsync(npc, dialogIndex, selection, cancellationToken),
                EventsNpcPolicy.RemainFunction when EventsNpcRates.LostBookOn =>
                    await HandleAncientRelicAsync(npc, dialogIndex, selection, cancellationToken),
                EventsNpcPolicy.HealthFunction when EventsNpcRates.MarathonOn =>
                    await HandleFitnessTrainerAsync(npc, dialogIndex, selection, path, amount, cancellationToken),
                EventsNpcPolicy.QuestFunction when EventsNpcRates.RandomQuestOn && npc.NpcKey.EndsWith("_118") =>
                    await HandleRandomQuestManagerAsync(npc, dialogIndex, selection, path, amount, cancellationToken),
                _ => false
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An event fault must never end the session; nothing was committed.
            Console.Error.WriteLine($"[eventsnpc] fault npc={npc.NpcKey} function={dialogIndex} " +
                $"selection={selection}: {error.GetType().Name}: {error.Message}");
            return true;
        }
    }

    private async Task SendEventsNpcReplyAsync(NpcSpawnDefinition npc, int dialogIndex, int[] reply, string reason,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(PacketBuilder.NpcFunctionActionResponse(npc.InteractionId, dialogIndex, reply),
            cancellationToken, reason);
        Console.WriteLine($"[eventsnpc] reply npc={npc.NpcKey} function={dialogIndex} {reason}=" +
            $"[{string.Join(',', reply)}]");
    }

    private DateTimeOffset EventsNpcNow => DateTimeOffset.UtcNow;

    private DateOnly EventsNpcDay(DateTimeOffset now) => _realmCalendar.GetDay(now);

    /// <summary>Runs one event transaction for this character, or returns <paramref name="unavailable"/>.</summary>
    private async Task<T> RunEventsNpcAsync<T>(Func<EventsNpcTransaction, Task<T>> body, T unavailable,
        CancellationToken cancellationToken)
    {
        if (EventsNpcRuntime.Store is not { } store || _character is null)
        {
            Console.WriteLine("[eventsnpc] store unavailable");
            return unavailable;
        }

        return await store.RunAsync(_character.Id, EventsNpcDay(EventsNpcNow), body, cancellationToken);
    }

    /// <summary>
    /// Reloads the character after an event transaction (bag, B-Gold, Talent Points) and repaints the bag.
    /// <paramref name="announced"/> are logged as acquisitions (the chat line the Zeus exchange uses); an
    /// item use passes none, so no notice follows a consumed item (the admin's rule).
    /// </summary>
    private async Task RefreshAfterEventsNpcAsync(IReadOnlyList<(int ItemId, int Quantity)> announced,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character)
        {
            return;
        }

        await RefreshZeusGiftInventoryAsync(character, announced, cancellationToken);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "EventsNpcStatus");
    }

    /// <summary>
    /// Pays event EXP through the shared progression path (level-ups, 10030, status). The event rate (exp.events,
    /// capped at 100M) applies unless <paramref name="eventRate"/> is false: the Exp Pill turn-in passes an amount
    /// already rated by exp.pill and validated exactly, and must be paid as is (review fix 5).
    /// </summary>
    private Task PayEventsNpcExperienceAsync(long experience, string reason, CancellationToken cancellationToken,
        bool eventRate = true) =>
        PayZeusGiftProgressionAsync(checked((int)Math.Min(experience, int.MaxValue)), 0, reason, cancellationToken,
            eventRate);

    /// <summary>One chat line to this player only (the operator announcement's World-channel frame).</summary>
    private async Task SendEventsNpcNoticeAsync(string text, string reason, CancellationToken cancellationToken)
    {
        if (ServerMessageRouting.ToTopBox) // the admin 2026-11-10: server info goes to the top box, not the chat box
        {
            await _registry.SendServerInfoAsync(_session, text, cancellationToken);
            Console.WriteLine($"[eventsnpc] notice {reason} (board) character={_character?.Name}: {text}");
            return;
        }
        var self = _registry.ResolveChatRecipients(_session, camp: null, characterIds: Array.Empty<int>());
        await _registry.DeliverChatAsync(self, recipient => PacketBuilder.ChatMessage(recipient.SenderObjectId,
            EventsNpcNoticeChannelWord, text), reason, cancellationToken);
        Console.WriteLine($"[eventsnpc] notice {reason} character={_character?.Name}: {text}");
    }

    private const uint EventsNpcNoticeChannelWord = 0x482; // = OperatorWorldChannelWord

    /// <summary>The form this NPC is waiting on, if it is still fresh.</summary>
    private EventsNpcForm? TakeEventsNpcForm(NpcSpawnDefinition npc, int function)
    {
        var form = _eventsNpcForm;
        if (form is null || form.NpcId != npc.InteractionId || form.Function != function ||
            form.Expires < EventsNpcNow)
        {
            return null;
        }

        _eventsNpcForm = null;
        return form;
    }
}
