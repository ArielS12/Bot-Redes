using TradingBots.App.Models;

namespace TradingBots.App.Services;

/// <summary>
/// Liquidez/volumen y universo AutoPilot.
/// AutoPilot solo opera bases liquidas (majors + mid-caps); evita alts iliquidos que nunca pasan el tick.
/// </summary>
public static class EntryFilters
{
    private const decimal CoreMinQuoteVolume24h = 250_000m;
    private const decimal MidMinQuoteVolume24h = 400_000m;
    private const decimal AltMinQuoteVolume24h = 750_000m;

    private const decimal CoreMinRelativeVolume = 0.15m;
    private const decimal MidMinRelativeVolume = 0.22m;
    private const decimal AltMinRelativeVolume = 0.35m;

    /// <summary>Nucleo: umbrales mas permisivos.</summary>
    private static readonly HashSet<string> CoreBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTC", "ETH", "SOL", "BNB", "XRP", "ADA", "DOGE"
    };

    /// <summary>Universo permitido en AutoPilot (Pullback-only). Fuera de esto no se crean/reactivanslots.</summary>
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

    public static decimal GetMinRelativeVolume(string symbol)
    {
        if (!TryGetBaseAsset(symbol, out var baseAsset))
        {
            return AltMinRelativeVolume;
        }

        if (CoreBases.Contains(baseAsset))
        {
            return CoreMinRelativeVolume;
        }

        if (AutopilotBases.Contains(baseAsset))
        {
            return MidMinRelativeVolume;
        }

        return AltMinRelativeVolume;
    }

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
