using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>Reversion a la media: RSI bajo + precio cerca de banda inferior de Bollinger.</summary>
public sealed class MeanReversionStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.MeanReversion;

    public bool ShouldBuy(TechnicalMarketSnapshot t) =>
        t.Rsi14 <= 32m &&
        t.LastPrice > 0m &&
        t.LastPrice <= t.BbLower * 1.003m &&
        t.MacdHistogram > t.PreviousMacdHistogram &&
        t.RelativeVolume >= 0.5m;

    public bool ShouldSell(TechnicalMarketSnapshot t, TechnicalMarketSnapshot? tf5) =>
        t.Rsi14 >= 58m ||
        (t.BbMiddle > 0m && t.LastPrice >= t.BbMiddle) ||
        t.MacdLine < t.MacdSignal ||
        (tf5 is not null && tf5.EmaFast < tf5.EmaSlow);

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot t) =>
        (40m - t.Rsi14) + (0.5m - t.BbPercent) * 20m +
        (t.MacdHistogram - t.PreviousMacdHistogram) * 800m;

    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15) =>
        tf5.Rsi14 <= 48m && tf15.Rsi14 <= 52m;

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 > 32m)
        {
            return $"MeanRev 1m: RSI {t.Rsi14:0.#} no en sobreventa (max 32).";
        }

        if (t.LastPrice > t.BbLower * 1.003m)
        {
            return "MeanRev 1m: precio no cerca de banda inferior Bollinger.";
        }

        if (t.MacdHistogram <= t.PreviousMacdHistogram)
        {
            return "MeanRev 1m: MACD sin expansion.";
        }

        return "Condicion MeanReversion no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        _ = ticker;
        if (technical.VolatilityPercent > StrategySignalConstants.MaxVolatilityPercentForEntry &&
            technical.AtrPercent > StrategySignalConstants.MaxAtrPercentForEntry)
        {
            return $"Regimen: volatilidad alta (vol%={technical.VolatilityPercent:0.##}).";
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

        if (regime.DailyAtrPercentileVsYear > StrategySignalConstants.LongTermMaxAtrPercentileVsYear)
        {
            return $"Regimen D1: ATR percentil anual {regime.DailyAtrPercentileVsYear:0.#}.";
        }

        return null;
    }

    public bool PassesLongTermRegime(LongTermRegimeSnapshot? regime) =>
        DescribeLongTermRegimeFailure(regime) is null;
}
