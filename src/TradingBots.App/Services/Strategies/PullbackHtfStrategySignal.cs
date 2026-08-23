using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Pullback en 15m con tendencia 1h: menos ruido, targets que cubren fees (~0.20% round-trip).
/// Entrada en dip a EMA21 15m; invalidacion si pierde estructura 15m/1h.
/// </summary>
public sealed class PullbackHtfStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.PullbackHtf;

    private const decimal MaxEntryRsi = 48m;
    private const decimal MinEntryRsi = 35m;
    private const decimal FailedBounceRsi = 30m;

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
        var hourlyTrendBroke = tf5 is not null && tf5.EmaFast < tf5.EmaSlow;
        return histogramCollapsed || rsiFailed || hourlyTrendBroke;
    }

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (50m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 800m +
        (technical.RelativeVolume * 4m);

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15) =>
        tf15.EmaFast >= tf15.EmaSlow && tf5.EmaFast >= tf5.EmaSlow;

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback HTF 15m: RSI {t.Rsi14:0.#} fuera de zona ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (t.LastPrice <= 0m || t.LastPrice > t.EmaSlow)
        {
            return $"Pullback HTF 15m: precio {t.LastPrice:0.####} por encima de EMA21 {t.EmaSlow:0.####}.";
        }

        if (t.MacdHistogram <= t.PreviousMacdHistogram)
        {
            return "Pullback HTF 15m: histograma MACD sin expansion vs vela anterior.";
        }

        if (t.MacdLine > t.MacdSignal)
        {
            return "Pullback HTF 15m: MACD ya cruzo al alza (dip consumido).";
        }

        return "Condicion de entrada 15m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        _ = ticker;
        if (technical.LastPrice <= 0m)
        {
            return "Regimen HTF: precio invalido en snapshot 15m.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct > 5.0m)
        {
            return $"Regimen HTF: separacion EMA 15m alta ({emaSpreadPct:0.###}%).";
        }

        if (technical.VolatilityPercent > 4.5m || technical.AtrPercent > 5.0m)
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

        if (!regime.DailyTrendUp && regime.LastClose < regime.DailyEma200 * 0.95m)
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
