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

    /// <summary>Flota Live acotada: BTC/ETH USDT (USDC testnet iliquido).</summary>
    private static readonly HashSet<string> PreferredRecoverySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT", "ETHUSDT"
    };

    private static readonly HashSet<string> HardBlockedBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "SOL"
    };

    /// <summary>Bloqueo duro SOL hasta esta fecha UTC.</summary>
    public static readonly DateTime SolHardBlockUntilUtc = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsPreferredRecoverySymbol(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && PreferredRecoverySymbols.Contains(symbol.Trim());

    public static bool IsHardBlockedSymbol(string symbol, DateTime nowUtc)
    {
        if (nowUtc >= SolHardBlockUntilUtc || string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return TryGetBaseAsset(symbol, out var baseAsset) && HardBlockedBases.Contains(baseAsset);
    }

    /// <summary>PnL de sesión vs referencia (reinicio en start de flota recovery).</summary>
    public static decimal GetSessionRealizedPnl(TradingBot bot) =>
        bot.RealizedPnlUsdt - bot.AutoScaleReferencePnlUsdt;

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
