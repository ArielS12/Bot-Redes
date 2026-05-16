namespace TradingBots.App.Services;

public static class TechnicalIndicatorMath
{
    public static decimal CalculateEma(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        if (values.Count < period)
        {
            return values[^1];
        }

        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        for (var i = period; i < values.Count; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
        }

        return decimal.Round(ema, 8);
    }

    public static decimal CalculateAtrPercent(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period)
    {
        var count = Math.Min(highs.Count, Math.Min(lows.Count, closes.Count));
        if (count <= period + 1)
        {
            return 0m;
        }

        var trs = new List<decimal>(count - 1);
        for (var i = 1; i < count; i++)
        {
            var tr = Math.Max(
                highs[i] - lows[i],
                Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            trs.Add(Math.Max(0m, tr));
        }

        var atr = trs.Skip(Math.Max(0, trs.Count - period)).Take(period).DefaultIfEmpty(0m).Average();
        var last = closes[^1];
        return last <= 0m ? 0m : decimal.Round((atr / last) * 100m, 4);
    }

    public static decimal PercentileRank(IReadOnlyList<decimal> samples, decimal value)
    {
        if (samples.Count == 0)
        {
            return 50m;
        }

        var belowOrEqual = samples.Count(x => x <= value);
        return decimal.Round((belowOrEqual * 100m) / samples.Count, 2);
    }
}
