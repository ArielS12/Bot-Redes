using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Pullback 15m / tendencia 1h. Entradas selectivas; salidas solo por rotura de estructura
/// (evita el clip por histograma que sangro bounce_invalidation en backtest).
/// </summary>
public sealed class PullbackHtfStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.PullbackHtf;

    private const decimal MaxEntryRsi = 50m;
    private const decimal MinEntryRsi = 34m;
    private const decimal FailedBounceRsi = 26m;

    public bool ShouldBuy(TechnicalMarketSnapshot technical) =>
        technical.Rsi14 is >= MinEntryRsi and <= MaxEntryRsi &&
        technical.LastPrice > 0m &&
        technical.EmaSlow > 0m &&
        technical.LastPrice <= technical.EmaSlow &&
        technical.MacdHistogram > technical.PreviousMacdHistogram &&
        technical.EmaFast >= technical.EmaSlow * 0.997m;

    /// <summary>
    /// tf1h = contexto superior. Solo invalidar rebote si la estructura se rompe de verdad.
    /// </summary>
    public bool ShouldSell(TechnicalMarketSnapshot technical, TechnicalMarketSnapshot? tf1h)
    {
        var hourlyTrendBroke = tf1h is not null && tf1h.EmaFast < tf1h.EmaSlow;
        var rsiCollapsed = technical.Rsi14 < FailedBounceRsi;
        var deepMacdFail = technical.MacdHistogram < technical.PreviousMacdHistogram &&
                           technical.MacdHistogram < -0.0001m * Math.Max(1m, technical.LastPrice) &&
                           technical.MacdLine < technical.MacdSignal;
        var lostEmaHard = technical.LastPrice > 0m &&
                          technical.EmaSlow > 0m &&
                          technical.LastPrice < technical.EmaSlow * 0.988m &&
                          technical.MacdHistogram < 0m;
        return hourlyTrendBroke || (rsiCollapsed && deepMacdFail) || lostEmaHard;
    }

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (50m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 700m +
        (technical.RelativeVolume * 3m);

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf15, TechnicalMarketSnapshot tf1h) =>
        tf1h.EmaFast >= tf1h.EmaSlow && tf15.EmaFast >= tf15.EmaSlow;

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback HTF 15m: RSI {t.Rsi14:0.#} fuera de zona ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (t.LastPrice <= 0m || t.EmaSlow <= 0m || t.LastPrice > t.EmaSlow)
        {
            return $"Pullback HTF 15m: precio {t.LastPrice:0.####} por encima de EMA21 {t.EmaSlow:0.####}.";
        }

        if (t.MacdHistogram <= t.PreviousMacdHistogram)
        {
            return "Pullback HTF 15m: histograma MACD sin expansion.";
        }

        if (t.EmaFast < t.EmaSlow * 0.997m)
        {
            return "Pullback HTF 15m: EMA9 demasiado por debajo de EMA21.";
        }

        return "Condicion de entrada 15m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        if (technical.LastPrice <= 0m)
        {
            return "Regimen HTF: precio invalido.";
        }

        if (Math.Abs(ticker.PriceChangePercent24h) >= 5.5m)
        {
            return $"Regimen HTF: |Δ24h|={Math.Abs(ticker.PriceChangePercent24h):0.##}% extremo.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct > 3.5m)
        {
            return $"Regimen HTF: separacion EMA alta ({emaSpreadPct:0.###}%).";
        }

        if (technical.VolatilityPercent > 3.2m || technical.AtrPercent > 3.8m)
        {
            return $"Regimen HTF: vol/ATR altos (vol%={technical.VolatilityPercent:0.##}).";
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

        if (!regime.DailyTrendUp)
        {
            return "Regimen D1 HTF: tendencia diaria no alcista.";
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
