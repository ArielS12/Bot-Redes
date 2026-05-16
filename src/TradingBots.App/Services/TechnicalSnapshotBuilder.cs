using TradingBots.App.Models;

namespace TradingBots.App.Services;

public static class TechnicalSnapshotBuilder
{
    public const int MinBars = 35;

    public static TechnicalMarketSnapshot? FromBars(
        IReadOnlyList<KlineBar> bars,
        string symbol,
        string interval)
    {
        if (bars.Count < MinBars)
        {
            return null;
        }

        var closes = bars.Select(x => x.Close).ToList();
        var highs = bars.Select(x => x.High).ToList();
        var lows = bars.Select(x => x.Low).ToList();
        var quoteVolumes = bars.Select(x => x.QuoteVolume).ToList();
        var (bbLower, bbMiddle, bbUpper, bbPercent) = TechnicalIndicators.CalculateBollinger(closes);
        var (macdLine, macdSignal, macdHist, prevMacdHist) = TechnicalIndicators.CalculateMacd(closes);

        return new TechnicalMarketSnapshot
        {
            Symbol = symbol,
            LastPrice = closes[^1],
            EmaFast = TechnicalIndicatorMath.CalculateEma(closes, 9),
            EmaSlow = TechnicalIndicatorMath.CalculateEma(closes, 21),
            Rsi14 = TechnicalIndicators.CalculateRsi(closes, 14),
            MacdLine = macdLine,
            MacdSignal = macdSignal,
            MacdHistogram = macdHist,
            PreviousMacdHistogram = prevMacdHist,
            RelativeVolume = TechnicalIndicators.CalculateRelativeVolume(quoteVolumes, 20),
            AtrPercent = TechnicalIndicatorMath.CalculateAtrPercent(highs, lows, closes, 14),
            VolatilityPercent = TechnicalIndicators.CalculateVolatilityPercent(closes, 20),
            BbLower = bbLower,
            BbMiddle = bbMiddle,
            BbUpper = bbUpper,
            BbPercent = bbPercent,
            Interval = interval
        };
    }
}
