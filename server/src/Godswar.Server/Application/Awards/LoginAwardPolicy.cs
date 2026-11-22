using System.Text.Json;

namespace Godswar.Server.Application.Awards;

// W4-B (the admin 2026-11-12): "can we set awards for logging on between certain dates?" and "allow awards to go to
// warehouse event awarder". Pure rules shared by the login grant, the Prize Chest and the checks.

internal sealed record LoginAwardItem(int ItemId, int Quantity, short Quality, short Grade, bool Bound);

internal sealed record LoginAwardBuff(int StatusId, long? DurationSeconds);

internal sealed record LoginAward(
    int Id,
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool PerAccount,
    bool ToPrizeChest,
    IReadOnlyList<LoginAwardItem> Items,
    int Silver,
    int Gold,
    int BindingGold,
    IReadOnlyList<LoginAwardBuff> Buffs,
    int? TitleId,
    int MinLevel);

/// <summary>What one login award did for one character.</summary>
internal sealed record LoginAwardGrant(
    LoginAward Award,
    bool ToPrizeChest,
    bool BagWasFull,
    IReadOnlyList<(int ItemId, int Quantity)> BagItems,
    bool TitleGranted,
    int BuffsQueued);

internal static class LoginAwardPolicy
{
    /// <summary>the admin's rule: no stack above 99, so one award row (and one chest row) holds at most 99.</summary>
    public const int MaxItemQuantity = 99;
    public const int MaxItems = 16;
    public const int MaxBuffs = 8;
    public const int MaxNameLength = 80;

    /// <summary>Start inclusive, end exclusive, both UTC instants.</summary>
    public static bool IsOpen(LoginAward award, DateTimeOffset now) =>
        now >= award.StartsAt && now < award.EndsAt;

    public static bool IsEligible(LoginAward award, DateTimeOffset now, int level) =>
        IsOpen(award, now) && level >= award.MinLevel;

    /// <summary>The once-only ledger key: one claim per account or per character.</summary>
    public static string ClaimKey(LoginAward award, int accountId, int characterId)
    {
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        return award.PerAccount ? $"a:{accountId}" : $"c:{characterId}";
    }

    /// <summary>
    /// Reads the items column: [{item_id, quantity 1-99, quality 0-30 (1), grade 0-30 (1), bound 0|1 (1)}].
    /// A malformed list grants nothing (the award is skipped and logged), it never grants a guess.
    /// </summary>
    public static bool TryParseItems(string json, out IReadOnlyList<LoginAwardItem> items, out string error)
    {
        items = [];
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "items must be a list";
                return false;
            }

            var list = new List<LoginAwardItem>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !TryInt(row, "item_id", null, out var itemId) || itemId <= 0 ||
                    !TryInt(row, "quantity", 1, out var quantity) || quantity is < 1 or > MaxItemQuantity ||
                    !TryInt(row, "quality", 1, out var quality) || quality is < 0 or > 30 ||
                    !TryInt(row, "grade", 1, out var grade) || grade is < 0 or > 30 ||
                    !TryInt(row, "bound", 1, out var bound) || bound is < 0 or > 1)
                {
                    error = "each item needs item_id > 0, quantity 1-99, quality and grade 0-30, bound 0|1";
                    return false;
                }

                list.Add(new LoginAwardItem(itemId, quantity, (short)quality, (short)grade, bound == 1));
            }

            if (list.Count > MaxItems)
            {
                error = $"at most {MaxItems} items";
                return false;
            }

            items = list;
            return true;
        }
        catch (JsonException exception)
        {
            error = "items is not JSON: " + exception.Message;
            return false;
        }
    }

    /// <summary>Reads the buffs column: [{status_id &gt; 0, duration_seconds 1-4294967295 or absent}].</summary>
    public static bool TryParseBuffs(string json, out IReadOnlyList<LoginAwardBuff> buffs, out string error)
    {
        buffs = [];
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "buffs must be a list";
                return false;
            }

            var list = new List<LoginAwardBuff>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !TryInt(row, "status_id", null, out var statusId) ||
                    statusId <= 0)
                {
                    error = "each buff needs status_id > 0";
                    return false;
                }

                long? duration = null;
                if (row.TryGetProperty("duration_seconds", out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) ||
                        seconds < 1 || seconds > uint.MaxValue)
                    {
                        error = "duration_seconds must be 1-4294967295";
                        return false;
                    }

                    duration = seconds;
                }

                list.Add(new LoginAwardBuff(statusId, duration));
            }

            if (list.Count > MaxBuffs)
            {
                error = $"at most {MaxBuffs} buffs";
                return false;
            }

            buffs = list;
            return true;
        }
        catch (JsonException exception)
        {
            error = "buffs is not JSON: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// The one chat line a player gets for an award (no pop-up). Kept short: the client cuts long lines.
    /// </summary>
    public static string Describe(LoginAwardGrant grant)
    {
        var award = grant.Award;
        var parts = new List<string>();
        var itemCount = award.Items.Sum(static item => item.Quantity);
        if (itemCount > 0) parts.Add(itemCount == 1 ? "1 item" : $"{itemCount} items");
        if (award.Silver > 0) parts.Add($"{award.Silver:N0} Silver");
        if (award.Gold > 0) parts.Add($"{award.Gold:N0} Gold");
        if (award.BindingGold > 0) parts.Add($"{award.BindingGold:N0} B-Gold");
        if (grant.TitleGranted) parts.Add("a title");
        if (grant.BuffsQueued > 0) parts.Add(grant.BuffsQueued == 1 ? "a buff" : $"{grant.BuffsQueued} buffs");
        var what = parts.Count == 0 ? "your reward" : string.Join(", ", parts);
        var where = grant.ToPrizeChest
            ? grant.BagWasFull
                ? " Your bag was full: the items wait at the Prize Chest by the warehouse."
                : " Collect it at the Prize Chest by the warehouse."
            : string.Empty;
        return Clip($"Login award {award.Name}: {what}.{where}", 200);
    }

    public static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    private static bool TryInt(JsonElement row, string name, int? fallback, out int value)
    {
        value = 0;
        if (!row.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (fallback is not { } defaultValue) return false;
            value = defaultValue;
            return true;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}

/// <summary>Who a server-wide buff reaches: camp 1 = Athens, 0 = Sparta (PopulationNotice), null = both.</summary>
internal readonly record struct BuffBroadcastFilter(int? Camp, int MinLevel, int MaxLevel)
{
    public static BuffBroadcastFilter Everyone => new(null, 1, 200);

    public bool IsValid => Camp is null or 0 or 1 && MinLevel is >= 1 and <= 200 && MaxLevel is >= 1 and <= 200 &&
        MinLevel <= MaxLevel;

    public bool Matches(byte camp, int level) =>
        (Camp is null || Camp == camp) && level >= MinLevel && level <= MaxLevel;
}

/// <summary>One Prize Chest row (kind item, silver, gold or binding_gold).</summary>
internal sealed record PrizeChestEntry(
    long Id,
    string Kind,
    int ItemId,
    int Quantity,
    short Quality,
    short Grade,
    bool Bound);

internal sealed record PrizeChestDraw(
    IReadOnlyList<(int ItemId, int Quantity)> Items,
    int Silver,
    int Gold,
    int BindingGold,
    int Waiting);

internal static class PrizeChestPolicy
{
    /// <summary>The Storage window's award tab has 40 cells (StorageUI.xml AwardBags C0-C39, ids 300010-300049).</summary>
    public const int VisibleCells = 40;

    public const string KindItem = "item";
    public const string KindSilver = "silver";
    public const string KindGold = "gold";
    public const string KindBindingGold = "binding_gold";

    /// <summary>The cells the client draws: the oldest 40 item rows in id order.</summary>
    public static IReadOnlyList<PrizeChestEntry> VisibleItems(IEnumerable<PrizeChestEntry> pending) =>
        pending.Where(static entry => entry.Kind == KindItem).OrderBy(static entry => entry.Id)
            .Take(VisibleCells).ToArray();

    /// <summary>The "B-Gold:" box of the award tab (MSG_STORAGE +4 in award mode), capped at int.MaxValue.</summary>
    public static int VisibleBindingGold(IEnumerable<PrizeChestEntry> pending) =>
        (int)Math.Min(int.MaxValue, pending.Where(static entry => entry.Kind == KindBindingGold)
            .Sum(static entry => (long)entry.Quantity));

    public static string Describe(PrizeChestDraw draw)
    {
        var parts = new List<string>();
        var count = draw.Items.Sum(static item => item.Quantity);
        if (count > 0) parts.Add(count == 1 ? "1 item" : $"{count} items");
        if (draw.Silver > 0) parts.Add($"{draw.Silver:N0} Silver");
        if (draw.Gold > 0) parts.Add($"{draw.Gold:N0} Gold");
        if (draw.BindingGold > 0) parts.Add($"{draw.BindingGold:N0} B-Gold");
        if (parts.Count == 0)
        {
            return draw.Waiting > 0
                ? $"Prize Chest: your bag is full. {draw.Waiting} prize(s) still wait here."
                : "The Prize Chest has nothing for you.";
        }

        var text = $"Prize Chest: you received {string.Join(", ", parts)}.";
        if (draw.Waiting > 0) text += $" {draw.Waiting} prize(s) still wait: make room in your bag and come back.";
        return LoginAwardPolicy.Clip(text, 200);
    }
}
