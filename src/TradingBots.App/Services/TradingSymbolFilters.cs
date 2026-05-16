namespace TradingBots.App.Services;

public static class TradingSymbolFilters
{
    private static readonly HashSet<string> StableAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "FDUSD", "BUSD", "USD1", "TUSD", "DAI", "EUR", "U"
    };

    public static bool IsStableStablePair(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var upper = symbol.Trim().ToUpperInvariant();
        string? quote = null;
        if (upper.EndsWith("USDT", StringComparison.Ordinal)) quote = "USDT";
        else if (upper.EndsWith("USDC", StringComparison.Ordinal)) quote = "USDC";
        else if (upper.EndsWith("FDUSD", StringComparison.Ordinal)) quote = "FDUSD";
        else if (upper.EndsWith("BUSD", StringComparison.Ordinal)) quote = "BUSD";
        else if (upper.EndsWith("USD1", StringComparison.Ordinal)) quote = "USD1";
        if (quote is null)
        {
            return false;
        }

        var baseAsset = upper[..^quote.Length];
        return StableAssets.Contains(baseAsset) && StableAssets.Contains(quote);
    }

    public static bool IsTradableVolatilePair(string symbol) =>
        !IsStableStablePair(symbol) &&
        (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ||
         symbol.EndsWith("USDC", StringComparison.OrdinalIgnoreCase));
}
