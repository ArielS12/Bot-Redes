using TradingBots.App.Models;

namespace TradingBots.App.Services;

/// <summary>Umbrales de entrada por liquidez/volumen. Majors (BTC/ETH/SOL/BNB) mas permisivos.</summary>
public static class EntryFilters
{
    private const decimal DefaultMinQuoteVolume24h = 750_000m;
    private const decimal MajorMinQuoteVolume24h = 300_000m;
    private const decimal AltMinQuoteVolume24h = 500_000m;

    private const decimal DefaultMinRelativeVolume = 0.45m;
    private const decimal MajorMinRelativeVolume = 0.28m;
    private const decimal AltMinRelativeVolume = 0.55m;

    private static readonly HashSet<string> MajorBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "BNB"
    };

    public static bool IsMajorSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var upper = symbol.Trim().ToUpperInvariant();
        foreach (var baseAsset in MajorBases)
        {
            if (upper.StartsWith(baseAsset, StringComparison.Ordinal) &&
                (upper.EndsWith("USDT", StringComparison.Ordinal) || upper.EndsWith("USDC", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public static decimal GetMinQuoteVolume24h(string symbol) =>
        IsMajorSymbol(symbol) ? MajorMinQuoteVolume24h : AltMinQuoteVolume24h;

    public static decimal GetMinRelativeVolume(string symbol) =>
        IsMajorSymbol(symbol) ? MajorMinRelativeVolume : AltMinRelativeVolume;

    public static bool PassesLiquidityAndVolume(string symbol, MarketTicker ticker, TechnicalMarketSnapshot technical) =>
        ticker.QuoteVolume24h >= GetMinQuoteVolume24h(symbol) &&
        technical.RelativeVolume >= GetMinRelativeVolume(symbol);

    public static string? DescribeLiquidityBlock(string symbol, MarketTicker ticker, TechnicalMarketSnapshot technical)
    {
        var minVol = GetMinQuoteVolume24h(symbol);
        if (ticker.QuoteVolume24h < minVol)
        {
            return $"Bloqueado por liquidez: volumen 24h insuficiente (min {minVol:0} USDT).";
        }

        var minRel = GetMinRelativeVolume(symbol);
        if (technical.RelativeVolume < minRel)
        {
            return $"Bloqueado por volumen relativo bajo (min {minRel:0.##}).";
        }

        return null;
    }
}
