namespace TradingBots.App;

internal static class BotIdsQuery
{
    public static List<Guid>? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var list = new List<Guid>();
        foreach (var p in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(p, out var g))
            {
                list.Add(g);
            }
        }

        return list.Count == 0 ? null : list;
    }
}
