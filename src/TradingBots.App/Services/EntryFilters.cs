using TradingBots.App.Models;

namespace TradingBots.App.Services;

/// <summary>
/// Liquidez y universo AutoPilot.
/// En testnet Live el RelVol 1m silenciaba incluso BTC: para simbolos AutoPilot solo exigimos volumen 24h.
/// </summary>
public static class EntryFilters
{
    private const decimal CoreMinQuoteVolume24h = 200_000m;
    private const decimal MidMinQuoteVolume24h = 300_000m;
    private const decimal AltMinQuoteVolume24h = 750_000m;

    /// <summary>Solo aplica a simbolos fuera del universo AutoPilot.</summary>
    private const decimal AltMinRelativeVolume = 0.30m;

    private static readonly HashSet<string> CoreBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "BNB", "XRP", "ADA", "DOGE"
    };

    private static readonly HashSet<string> AutopilotBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "BNB", "XRP", "ADA", "DOGE", "LINK", "AVAX", "LTC", "DOT",
        "ATOM", "NEAR", "APT", "ARB", "OP", "SUI", "UNI", "AAVE", "FIL", "INJ", "TIA",
        "SEI", "TON", "TRX", "BCH", "ETC", "POL", "MATIC"
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

    public static bool IsAutopilotAllowedSymbol(string symbol) =>
        TryGetBaseAsset(symbol, out var baseAsset) &&
        AutopilotBases.Contains(baseAsset) &&
        !baseAsset.StartsWith("1000", StringComparison.Ordinal);

    public static decimal GetMinQuoteVolume24h(string symbol)
    {
        if (!TryGetBaseAsset(symbol, out var baseAsset))
        {
            return AltMinQuoteVolume24h;
        }

        if (CoreBases.Contains(baseAsset))
        {
            return CoreMinQuoteVolume24h;
        }

        if (AutopilotBases.Contains(baseAsset))
        {
            return MidMinQuoteVolume24h;
        }

        return AltMinQuoteVolume24h;
    }

    /// <summary>
    /// RelVol hard-gate desactivado en universo AutoPilot (0 = no exige).
    /// Fuera de AutoPilot se mantiene un minimo blando para no abrir basura manual.
    /// </summary>
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
