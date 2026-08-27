using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Pullback 15m con tendencia 1h. Entradas mas alcanzables que v1 (que dio 0 trades),
/// pero sin chase: precio cerca de EMA21, RSI en dip, histograma MACD expandiendo.
/// </summary>
public sealed class PullbackHtfStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.PullbackHtf;

    private const decimal MaxEntryRsi = 55m;
    private const decimal MinEntryRsi = 32m;
    private const decimal FailedBounceRsi = 28m;
    /// <summary>Permite precio hasta +0.35% sobre EMA21 (pullback cercano, no chase).</summary>
    private const decimal MaxPriceAboveEmaSlowPercent = 0.35m;

    public bool ShouldBuy(TechnicalMarketSnapshot technical)
    {
        if (technical.Rsi14 is < MinEntryRsi or > MaxEntryRsi || technical.LastPrice <= 0m || technical.EmaSlow <= 0m)
        {
            return false;
        }

        var maxPrice = technical.EmaSlow * (1m + MaxPriceAboveEmaSlowPercent / 100m);
        var nearOrBelowEma = technical.LastPrice <= maxPrice;
        var macdExpanding = technical.MacdHistogram > technical.PreviousMacdHistogram;
        return nearOrBelowEma && macdExpanding;
    }

    public bool ShouldSell(TechnicalMarketSnapshot technical, TechnicalMarketSnapshot? tf1h)
    {
        var histogramCollapsed = technical.MacdHistogram <= technical.PreviousMacdHistogram &&
                                 technical.MacdHistogram <= 0m;
        var rsiFailed = technical.Rsi14 < FailedBounceRsi;
        var hourlyTrendBroke = tf1h is not null && tf1h.EmaFast < tf1h.EmaSlow;
        var lostEmaStructure = technical.LastPrice > 0m &&
                               technical.EmaSlow > 0m &&
                               technical.LastPrice < technical.EmaSlow * 0.985m &&
                               technical.MacdHistogram < 0m;
        return histogramCollapsed || rsiFailed || hourlyTrendBroke || lostEmaStructure;
    }

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (52m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 600m +
        (technical.RelativeVolume * 3m);

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf15, TechnicalMarketSnapshot tf1h) =>
        tf1h.EmaFast >= tf1h.EmaSlow &&
        (tf15.EmaFast >= tf15.EmaSlow || tf15.MacdLine >= tf15.MacdSignal);

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback HTF 15m: RSI {t.Rsi14:0.#} fuera de zona ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (t.LastPrice <= 0m || t.EmaSlow <= 0m)
        {
            return "Pullback HTF 15m: precio/EMA invalidos.";
        }

        var maxPrice = t.EmaSlow * (1m + MaxPriceAboveEmaSlowPercent / 100m);
        if (t.LastPrice > maxPrice)
        {
            return $"Pullback HTF 15m: precio {t.LastPrice:0.####} lejos de EMA21 {t.EmaSlow:0.####} (>+{MaxPriceAboveEmaSlowPercent:0.##}%).";
        }

        if (t.MacdHistogram <= t.PreviousMacdHistogram)
        {
            return "Pullback HTF 15m: histograma MACD sin expansion vs vela anterior.";
        }

        return "Condicion de entrada 15m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        if (technical.LastPrice <= 0m)
        {
            return "Regimen HTF: precio invalido en snapshot 15m.";
        }

        // Evita comprar dias de pump extremo (mismo patron que sangro la flota 1m).
        if (Math.Abs(ticker.PriceChangePercent24h) >= 6.0m)
        {
            return $"Regimen HTF: |Δ24h|={Math.Abs(ticker.PriceChangePercent24h):0.##}% demasiado extremo.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct > 4.0m)
        {
            return $"Regimen HTF: separacion EMA 15m alta ({emaSpreadPct:0.###}%).";
        }

        if (technical.VolatilityPercent > 3.5m || technical.AtrPercent > 4.0m)
        {
            return $"Regimen HTF: volatilidad/ATR 15m altos (vol%={technical.VolatilityPercent:0.##}, ATR%={technical.AtrPercent:0.##}).";
        }

        return null;
    }

    public bool PassesShortRegimeFilter(TechnicalMarketSnapshot technical, MarketTicker ticker) =>
        DescribeShortRegimeFailure(technical, ticker) is null;

    public string? DescribeLongTermRegimeFailure(LongTermRegimeSnapshot? regime)
    {
        if (regime is null || !regime.HasData)
        {
            return null;
        }

        if (!regime.DailyTrendUp && regime.LastClose < regime.DailyEma200 * 0.97m)
        {
            return "Regimen D1 HTF: tendencia bajista (cierre bajo EMA200).";
        }

        if (regime.DailyAtrPercentileVsYear > StrategySignalConstants.LongTermMaxAtrPercentileVsYear)
        {
            return $"Regimen D1 HTF: volatilidad extrema (ATR percentil {regime.DailyAtrPercentileVsYear:0.#}).";
        }

        return null;
    }

    public bool PassesLongTermRegime(LongTermRegimeSnapshot? regime) =>
        DescribeLongTermRegimeFailure(regime) is null;
}
