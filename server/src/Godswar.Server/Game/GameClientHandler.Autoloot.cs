using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// W3: Plump Wheat 4468 ("Activates the autoloot function (4 hours)") and Golden Leaf 4469 ("(7 days)").
/// The autoloot itself runs in the client (AFK window, Autoloot tab: it sends the normal pick-ups). The client
/// learns the time left from an S2C 10357 (EnterUiReady number): 8 bytes, u32 minutes at offset 4 (handler
/// 0x4df570 -> 0x4743f0 keeps the minutes at +0x168, counts them down each minute and shows its own
/// "Autoloot" line; at zero it sends C2S 10357 again). 0 would print the "autoloot closed" text, so the server
/// only ever sends a positive time: on use, after world entry, and in answer to the client's 10357 if time is
/// left. Stored in public.autoloot_state (runtime DDL). Parcel Stone 4103: every bag is already open (the
/// server always sends 4 pages = 96 slots), so it is not used up and one info line says so.
/// </summary>
internal sealed partial class GameClientHandler
{
    private const int PlumpWheat = 4468;
    private const int GoldenLeaf = 4469;
    private const int ParcelStone = 4103;
    private DateTimeOffset? _autolootSentExpiry;

    /// <summary>A 10357 within 90 s of the expiry this session last sent is the client's "time is up" ping.</summary>
    private bool IsAutolootExpiryPing()
    {
        if (_autolootSentExpiry is not { } expiry ||
            Math.Abs((DateTimeOffset.UtcNow - expiry).TotalSeconds) > 90) return false;
        _autolootSentExpiry = null;
        return true;
    }

    private async Task<bool> UseAutolootItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var add = itemId == PlumpWheat ? TimeSpan.FromHours(4) : TimeSpan.FromDays(7); // item text
        var expires = await RunEventsNpcAsync(async tx =>
        {
            if (!await tx.ConsumeAsync(itemId, 1)) { tx.Rollback(); return (object?)null; }
            return await tx.ScalarAsync("""
                INSERT INTO public.autoloot_state (character_id, expires_at) VALUES (@c, now() + @a)
                ON CONFLICT (character_id) DO UPDATE
                    SET expires_at = GREATEST(public.autoloot_state.expires_at, now()) + @a
                RETURNING extract(epoch FROM expires_at)::bigint;
                """, ("a", add));
        }, (object?)null, cancellationToken);
        Console.WriteLine($"[autoloot] use character={_character!.Name} item={itemId} expires={expires ?? "refused"}");
        if (expires is null) { await SendKitBagRefreshAsync(cancellationToken); return true; }
        await RefreshAfterEventsNpcAsync([], cancellationToken);
        await SendAutolootMinutesAsync(DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(expires)), cancellationToken);
        return true;
    }

    /// <summary>After EnterUiReady (world entry, revive, or the client's own "time is up" 10357).</summary>
    private async Task SendStoredAutolootAsync(CancellationToken cancellationToken)
    {
        if (_character is null || !Finalise2Rates.On(Finalise2RateKeys.Autoloot)) return;
        var epoch = await RunEventsNpcAsync(async tx => await tx.ScalarAsync(
            "SELECT extract(epoch FROM expires_at)::bigint FROM public.autoloot_state WHERE character_id = @c AND expires_at > now();"),
            (object?)null, cancellationToken);
        if (epoch is null) return;
        await SendAutolootMinutesAsync(DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(epoch)), cancellationToken);
    }

    private async Task SendAutolootMinutesAsync(DateTimeOffset expires, CancellationToken cancellationToken)
    {
        var minutes = (long)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalMinutes);
        if (minutes <= 0) return;
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.EnterUiReady);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)Math.Min(minutes, uint.MaxValue));
        await _session.SendAsync(packet, cancellationToken, "AutolootMinutes");
        _autolootSentExpiry = DateTimeOffset.UtcNow.AddMinutes(minutes); // what the client counts down to
        Console.WriteLine($"[autoloot] minutes character={_character?.Name} left={minutes}");
    }

    private async Task<bool> UseParcelStoneAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[parcel-stone] character={_character!.Name} all bags already open");
        await SendKitBagRefreshAsync(cancellationToken);
        await SendInfoLineAsync("All your bags are already open (96 slots); the Parcel Stone was not used.",
            InfoTabOther, cancellationToken);
        return true;
    }
}
