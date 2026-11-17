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
        // Excluir vela en formacion para evitar repaint de señales (EMA/RSI/MACD mid-bar).
        var now = DateTime.UtcNow;
        var closedBars = bars
            .Where(x => x.CloseTimeUtc == default || x.IsClosed(now))
            .ToList();
        // Si CloseTimeUtc no viene informado (legacy), caer al penultimo bar cuando hay >= MinBars+1.
        if (closedBars.Count == bars.Count && bars.Count > MinBars && bars[^1].CloseTimeUtc == default)
        {
            closedBars = bars.Take(bars.Count - 1).ToList();
        }

        if (closedBars.Count < MinBars)
        {
            return null;
        }

        var closes = closedBars.Select(x => x.Close).ToList();
        var highs = closedBars.Select(x => x.High).ToList();
        var lows = closedBars.Select(x => x.Low).ToList();
        var quoteVolumes = closedBars.Select(x => x.QuoteVolume).ToList();
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
