using TradingBots.App.Models;

namespace TradingBots.App.Services;

/// <summary>
/// Liquidez y universo AutoPilot: solo nucleo liquido (7 bases) para evitar churn en alts.
/// </summary>
public static class EntryFilters
{
    private const decimal CoreMinQuoteVolume24h = 200_000m;
    private const decimal AltMinQuoteVolume24h = 750_000m;

    private const decimal AltMinRelativeVolume = 0.30m;

    /// <summary>Universo AutoPilot = solo majors de alta liquidez.</summary>
    private static readonly HashSet<string> CoreBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "BNB", "XRP", "ADA", "DOGE"
    };

    public static bool TryGetBaseAsset(string symbol, out string baseAsset)
    {
        baseAsset = string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var upper = symbol.Trim().ToUpperInvariant();
        if (upper.EndsWith("USDT", StringComparison.Ordinal) && upper.Length > 4)
        {
            baseAsset = upper[..^4];
            return true;
        }

        if (upper.EndsWith("USDC", StringComparison.Ordinal) && upper.Length > 4)
        {
            baseAsset = upper[..^4];
            return true;
        }

        return false;
    }

    public static bool IsMajorSymbol(string symbol) =>
        TryGetBaseAsset(symbol, out var baseAsset) && CoreBases.Contains(baseAsset);

    public static bool IsAutopilotAllowedSymbol(string symbol) => IsMajorSymbol(symbol);

    public static decimal GetMinQuoteVolume24h(string symbol) =>
        IsMajorSymbol(symbol) ? CoreMinQuoteVolume24h : AltMinQuoteVolume24h;

    public static decimal GetMinRelativeVolume(string symbol) =>
        IsAutopilotAllowedSymbol(symbol) ? 0m : AltMinRelativeVolume;

    public static bool PassesLiquidityAndVolume(string symbol, MarketTicker ticker, TechnicalMarketSnapshot technical)
    {
        if (ticker.QuoteVolume24h < GetMinQuoteVolume24h(symbol))
        {
            return false;
        }

        var minRel = GetMinRelativeVolume(symbol);
        return minRel <= 0m || technical.RelativeVolume >= minRel;
    }

    public static string? DescribeLiquidityBlock(string symbol, MarketTicker ticker, TechnicalMarketSnapshot technical)
    {
        var minVol = GetMinQuoteVolume24h(symbol);
        if (ticker.QuoteVolume24h < minVol)
        {
            return $"Bloqueado por liquidez: volumen 24h insuficiente (min {minVol:0} USDT).";
        }

        var minRel = GetMinRelativeVolume(symbol);
        if (minRel > 0m && technical.RelativeVolume < minRel)
        {
            return $"Bloqueado por volumen relativo bajo (min {minRel:0.##}).";
        }

        return null;
    }
}
