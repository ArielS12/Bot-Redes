using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public sealed class PullbackStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.Pullback;

    private const decimal MaxEntryRsi = 52m;
    private const decimal MinEntryRsi = 22m;

    /// <summary>
    /// Pullback = dip en tendencia superior: RSI en zona baja + MACD girando.
    /// No exigir EMA rapida>=lenta en 1m (eso es continuation, no pullback).
    /// </summary>
    public bool ShouldBuy(TechnicalMarketSnapshot technical) =>
        technical.Rsi14 is >= MinEntryRsi and <= MaxEntryRsi &&
        (technical.MacdHistogram > technical.PreviousMacdHistogram ||
         technical.MacdLine >= technical.MacdSignal);

    public bool ShouldSell(TechnicalMarketSnapshot technical) =>
        technical.Rsi14 >= 62m ||
        technical.MacdLine < technical.MacdSignal;

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (50m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 1000m +
        (technical.RelativeVolume * 5m);

    /// <summary>Confirmacion de tendencia en 15m; el dip ocurre en 1m.</summary>
    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15)
    {
        _ = tf5;
        return tf15.EmaFast >= tf15.EmaSlow;
    }

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback 1m: RSI {t.Rsi14:0.#} fuera de zona de pullback ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (!(t.MacdHistogram > t.PreviousMacdHistogram || t.MacdLine >= t.MacdSignal))
        {
            return "Pullback 1m: sin giro MACD (histograma expandiendo o linea>=signal).";
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

        var volatilityOk = technical.VolatilityPercent <= StrategySignalConstants.MaxVolatilityPercentForEntry ||
                           technical.AtrPercent <= StrategySignalConstants.MaxAtrPercentForEntry;
        if (!volatilityOk)
        {
            return $"Regimen: volatilidad/ATR 1m altos (vol%={technical.VolatilityPercent:0.##}, ATR%={technical.AtrPercent:0.##}).";
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
