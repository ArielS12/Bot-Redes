using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public sealed class MomentumStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.Momentum;

    public bool ShouldBuy(TechnicalMarketSnapshot technical) =>
        technical.EmaFast > technical.EmaSlow &&
        technical.MacdLine > technical.MacdSignal &&
        MomentumMacdHistogramEntryOk(technical) &&
        technical.Rsi14 >= 50m &&
        technical.Rsi14 <= 65m;

    public bool ShouldSell(TechnicalMarketSnapshot technical, TechnicalMarketSnapshot? tf5) =>
        technical.EmaFast < technical.EmaSlow ||
        technical.MacdLine < technical.MacdSignal ||
        technical.Rsi14 >= 78m ||
        (tf5 is not null && tf5.EmaFast < tf5.EmaSlow);

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (technical.MacdHistogram * 1000m) + (technical.EmaFast - technical.EmaSlow) + technical.Rsi14 +
        (technical.RelativeVolume * 5m);

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15) =>
        tf5.EmaFast > tf5.EmaSlow &&
        tf15.EmaFast > tf15.EmaSlow &&
        tf15.MacdLine >= (tf15.MacdSignal - 0.0005m);

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.EmaFast <= t.EmaSlow)
        {
            return "Momentum 1m: EMA rapida no por encima de la lenta.";
        }

        if (t.MacdLine <= t.MacdSignal)
        {
            return "Momentum 1m: linea MACD no por encima de la senal.";
        }

        if (!MomentumMacdHistogramEntryOk(t))
        {
            return "Momentum 1m: histograma MACD sin impulso (cruce por cero o expansion positiva).";
        }

        if (t.Rsi14 < 50m)
        {
            return $"Momentum 1m: RSI {t.Rsi14:0.#} bajo minimo de entrada (50).";
        }

        if (t.Rsi14 > 65m)
        {
            return $"Momentum 1m: RSI {t.Rsi14:0.#} sobre maximo de entrada (65).";
        }

        return "Condicion de entrada 1m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        if (technical.LastPrice <= 0m)
        {
            return "Regimen: precio invalido en snapshot 1m.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct < StrategySignalConstants.MinTrendSpreadPercentForEntry)
        {
            return $"Regimen: tendencia 1m debil (spread EMA {emaSpreadPct:0.###}% < min {StrategySignalConstants.MinTrendSpreadPercentForEntry}%).";
        }

        if (emaSpreadPct > StrategySignalConstants.MomentumMaxEmaSpreadPercentOfPrice)
        {
            return $"Regimen: spread EMA amplio anti-chase ({emaSpreadPct:0.###}% > {StrategySignalConstants.MomentumMaxEmaSpreadPercentOfPrice}%).";
        }

        var volatilityOk = technical.VolatilityPercent <= StrategySignalConstants.MaxVolatilityPercentForEntry ||
                           technical.AtrPercent <= StrategySignalConstants.MaxAtrPercentForEntry;
        if (!volatilityOk)
        {
            return $"Regimen: volatilidad/ATR 1m altos (vol%={technical.VolatilityPercent:0.##}, ATR%={technical.AtrPercent:0.##}).";
        }

        if (!(Math.Abs(ticker.PriceChangePercent24h) < StrategySignalConstants.MomentumMaxAbsChange24hPercentForEntry ||
              technical.Rsi14 <= StrategySignalConstants.MomentumMaxRsiOnStrongDailyMove))
        {
            return
                $"Regimen: dia extendido (|24h|={Math.Abs(ticker.PriceChangePercent24h):0.##}%) con RSI {technical.Rsi14:0.#} > {StrategySignalConstants.MomentumMaxRsiOnStrongDailyMove} (anti-chase).";
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

        if (!regime.DailyTrendUp && regime.LastClose < regime.DailyEma50)
        {
            return $"Regimen D1: tendencia bajista (cierre {regime.LastClose:0.####} < EMA50 {regime.DailyEma50:0.####}).";
        }

        if (regime.PricePercentileIn90d > StrategySignalConstants.LongTermMomentumMaxPricePercentile90d)
        {
            return $"Regimen D1: precio en percentil {regime.PricePercentileIn90d:0.#} del rango 90d (anti-chase).";
        }

        if (regime.DailyAtrPercentileVsYear > StrategySignalConstants.LongTermMaxAtrPercentileVsYear)
        {
            return $"Regimen D1: volatilidad extrema (ATR percentil anual {regime.DailyAtrPercentileVsYear:0.#}).";
        }

        return null;
    }

    public bool PassesLongTermRegime(LongTermRegimeSnapshot? regime) =>
        DescribeLongTermRegimeFailure(regime) is null;

    private static bool MomentumMacdHistogramEntryOk(TechnicalMarketSnapshot technical) =>
        (technical.PreviousMacdHistogram <= 0m && technical.MacdHistogram > 0m) ||
        (technical.MacdHistogram > 0m && technical.MacdHistogram > technical.PreviousMacdHistogram);
}
