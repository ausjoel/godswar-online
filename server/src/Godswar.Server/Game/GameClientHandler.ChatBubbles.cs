using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W1 dead items: Chat Bubble 1 / 2 (4110 / 4111, Magic.ini skills 4501 / 4502, ScriptID ChangeChatBubble1 / 2; undo
/// skill 4500 = ChangeChatBubble0). The 2008 server kept a per-player ucChatBubbleSytle and wrote it into every
/// MSG_TALK; 2.51's chat frame carries the same four bytes as its channel word, so the style is its top byte.
/// The items' text says "for 3 hours" (ExpiredTime "0,0,0,3,0,0"). Value 1 = the red bubble (seen in the client); value 2, the blue one, draws the normal bubble in this client (values 2-4 tried). Stored in public.chat_bubble_state (runtime DDL).
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int CypressLeave = 4140;
    private const int ChatBubbleOne = 4110;
    private const int ChatBubbleTwo = 4111;
    private static readonly TimeSpan ChatBubbleLifetime = TimeSpan.FromHours(3); // item text: "for 3 hours"
    private byte _chatBubbleStyle;
    private DateTimeOffset _chatBubbleExpires = DateTimeOffset.MinValue;
    private bool _chatBubbleLoaded;

    private async Task<byte> CurrentChatBubbleStyleAsync(CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.ChatBubbles) || _character is null) return 0;
        if (!_chatBubbleLoaded)
        {
            _chatBubbleLoaded = true;
            var row = await RunEventsNpcAsync(async tx => await tx.ScalarAsync(
                "SELECT style::text || ',' || extract(epoch FROM expires_at)::bigint FROM public.chat_bubble_state WHERE character_id = @c;"),
                (object?)null, cancellationToken) as string;
            if (row is not null)
            {
                var parts = row.Split(',');
                _chatBubbleStyle = byte.Parse(parts[0]);
                _chatBubbleExpires = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1]));
            }
        }
        return DateTimeOffset.UtcNow < _chatBubbleExpires ? _chatBubbleStyle : (byte)0;
    }

    private async Task<bool> UseChatBubbleAsync(int itemId, CancellationToken cancellationToken)
    {
        var style = (byte)(itemId == ChatBubbleOne ? 1 : 2);
        var expires = DateTimeOffset.UtcNow + ChatBubbleLifetime;
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(itemId, 1)) { tx.Rollback(); return false; }
            await tx.ScalarAsync("""
                INSERT INTO public.chat_bubble_state (character_id, style, expires_at) VALUES (@c, @s, @e)
                ON CONFLICT (character_id) DO UPDATE SET style = EXCLUDED.style, expires_at = EXCLUDED.expires_at;
                """, ("s", (short)style), ("e", expires));
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[chat-bubble] character={_character!.Name} item={itemId} style={style} done={done}");
        if (!done) { await SendKitBagRefreshAsync(cancellationToken); return true; }
        _chatBubbleLoaded = true;
        _chatBubbleStyle = style;
        _chatBubbleExpires = expires;
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendGainLogAsync($"Chat bubble {style} for 3 hours", cancellationToken);
        return true;
    }

    /// <summary>W1 Cypress Leave: "Masked Man" for 2 hours, then nearby players get the new appearance.</summary>
    private async Task<bool> UseCypressLeaveAsync(CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + MaskedNames.Lifetime;
        var done = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(CypressLeave, 1)) { tx.Rollback(); return false; }
            await tx.ScalarAsync("""
                INSERT INTO public.masked_state (character_id, expires_at) VALUES (@c, @e)
                ON CONFLICT (character_id) DO UPDATE SET expires_at = EXCLUDED.expires_at;
                """, ("e", until));
            return true;
        }, false, cancellationToken);
        Console.WriteLine($"[cypress-leave] character={_character!.Name} done={done}");
        if (!done) { await SendKitBagRefreshAsync(cancellationToken); return true; }
        MaskedNames.Set(_character.Id, until);
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await BroadcastEquipmentRefreshAsync("cypress-leave", cancellationToken);
        await SendGainLogAsync("Masked Man for 2 hours", cancellationToken);
        return true;
    }
}
