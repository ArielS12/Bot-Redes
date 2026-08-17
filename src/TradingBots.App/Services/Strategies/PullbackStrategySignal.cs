using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public sealed class PullbackStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.Pullback;

    private const decimal MaxEntryRsi = 42m;
    private const decimal MinEntryRsi = 30m;
    private const decimal FailedBounceRsi = 28m;

    public bool ShouldBuy(TechnicalMarketSnapshot technical) =>
        technical.Rsi14 is >= MinEntryRsi and <= MaxEntryRsi &&
        technical.LastPrice > 0m &&
        technical.LastPrice <= technical.EmaSlow &&
        technical.MacdHistogram > technical.PreviousMacdHistogram &&
        technical.MacdLine <= technical.MacdSignal;

    public bool ShouldSell(TechnicalMarketSnapshot technical, TechnicalMarketSnapshot? tf5)
    {
        var histogramCollapsed = technical.MacdHistogram <= technical.PreviousMacdHistogram &&
                                 technical.MacdHistogram <= 0m;
        var rsiFailed = technical.Rsi14 < FailedBounceRsi;
        var tf5TrendBroke = tf5 is not null && tf5.EmaFast < tf5.EmaSlow;
        return histogramCollapsed || rsiFailed || tf5TrendBroke;
    }

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (50m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 1000m +
        (technical.RelativeVolume * 5m);

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15) =>
        tf15.EmaFast >= tf15.EmaSlow && tf5.EmaFast >= tf5.EmaSlow;

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback 1m: RSI {t.Rsi14:0.#} fuera de zona de pullback ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (t.LastPrice <= 0m || t.LastPrice > t.EmaSlow)
        {
            return $"Pullback 1m: precio {t.LastPrice:0.####} por encima de EMA21 {t.EmaSlow:0.####} (no es pullback a la media).";
        }

        if (t.MacdHistogram <= t.PreviousMacdHistogram)
        {
            return "Pullback 1m: histograma MACD sin expansion vs vela anterior.";
        }

        if (t.MacdLine > t.MacdSignal)
        {
            return "Pullback 1m: MACD ya cruzo al alza (el dip ya no esta activo).";
        }

        return "Condicion de entrada 1m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        _ = ticker;
        if (technical.LastPrice <= 0m)
        {
            return "Regimen: precio invalido en snapshot 1m.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct > StrategySignalConstants.PullbackMaxEmaSpreadPercentOfPrice)
        {
            return $"Regimen: separacion EMA 1m alta ({emaSpreadPct:0.###}% > {StrategySignalConstants.PullbackMaxEmaSpreadPercentOfPrice}%).";
        }

        if (technical.VolatilityPercent > StrategySignalConstants.MaxVolatilityPercentForEntry ||
            technical.AtrPercent > StrategySignalConstants.MaxAtrPercentForEntry)
        {
            return $"Regimen: volatilidad y ATR 1m altos (vol%={technical.VolatilityPercent:0.##}, ATR%={technical.AtrPercent:0.##}).";
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

        if (regime.PricePercentileIn90d > StrategySignalConstants.LongTermPullbackMaxPricePercentile90d)
        {
            return $"Regimen D1: precio alto en rango 90d (percentil {regime.PricePercentileIn90d:0.#}).";
        }

        if (!regime.DailyTrendUp && regime.LastClose < regime.DailyEma200 * 0.93m)
        {
            return "Regimen D1: tendencia bajista fuerte (cierre muy por debajo de EMA200).";
        }

        if (regime.DailyAtrPercentileVsYear > StrategySignalConstants.LongTermMaxAtrPercentileVsYear)
        {
            return $"Regimen D1: volatilidad extrema (ATR percentil anual {regime.DailyAtrPercentileVsYear:0.#}).";
        }

        return null;
    }

    public bool PassesLongTermRegime(LongTermRegimeSnapshot? regime) =>
        DescribeLongTermRegimeFailure(regime) is null;
}
