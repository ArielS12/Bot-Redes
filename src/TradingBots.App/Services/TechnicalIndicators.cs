namespace TradingBots.App.Services;

public static class TechnicalIndicators
{
    public static (decimal lower, decimal middle, decimal upper, decimal percent) CalculateBollinger(
        IReadOnlyList<decimal> closes,
        int period = 20,
        decimal stdDevMult = 2m)
    {
        if (closes.Count < period)
        {
            var last = closes.Count > 0 ? closes[^1] : 0m;
            return (last, last, last, 0.5m);
        }

        var slice = closes.TakeLast(period).ToList();
        var middle = slice.Average();
        var variance = slice.Select(c => (c - middle) * (c - middle)).Average();
        var std = (decimal)Math.Sqrt((double)Math.Max(0m, variance));
        var upper = middle + stdDevMult * std;
        var lower = middle - stdDevMult * std;
        var lastClose = closes[^1];
        var percent = upper <= lower
            ? 0.5m
            : decimal.Round((lastClose - lower) / (upper - lower), 4);
        return (decimal.Round(lower, 8), decimal.Round(middle, 8), decimal.Round(upper, 8), percent);
    }

    public static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period)
        {
            return 50m;
        }

        decimal gain = 0m;
        decimal loss = 0m;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta > 0m)
            {
                gain += delta;
            }
            else
            {
                loss += Math.Abs(delta);
            }
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;
        for (var i = period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            avgGain = ((avgGain * (period - 1)) + Math.Max(0m, delta)) / period;
            avgLoss = ((avgLoss * (period - 1)) + Math.Max(0m, -delta)) / period;
        }

        if (avgLoss == 0m)
        {
            return 100m;
        }

        var rs = avgGain / avgLoss;
        return decimal.Round(100m - (100m / (1m + rs)), 4);
    }

    public static (decimal macdLine, decimal signalLine, decimal histogram, decimal previousHistogram) CalculateMacd(
        IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 35)
        {
            return (0m, 0m, 0m, 0m);
        }

        var ema12Series = BuildEmaSeries(closes, 12);
        var ema26Series = BuildEmaSeries(closes, 26);
        var minCount = Math.Min(ema12Series.Count, ema26Series.Count);
        var macdSeries = new List<decimal>(minCount);
        var offset12 = ema12Series.Count - minCount;
        var offset26 = ema26Series.Count - minCount;
        for (var i = 0; i < minCount; i++)
        {
            macdSeries.Add(ema12Series[i + offset12] - ema26Series[i + offset26]);
        }

        var signalSeries = BuildEmaSeries(macdSeries, 9);
        var aligned = Math.Min(macdSeries.Count, signalSeries.Count);
        var macdOffset = macdSeries.Count - aligned;
        var signalOffset = signalSeries.Count - aligned;
        var histogramSeries = new List<decimal>(aligned);
        for (var i = 0; i < aligned; i++)
        {
            histogramSeries.Add(macdSeries[i + macdOffset] - signalSeries[i + signalOffset]);
        }

        var lastHistogram = histogramSeries.Count > 0 ? histogramSeries[^1] : 0m;
        var previousHistogram = histogramSeries.Count > 1 ? histogramSeries[^2] : lastHistogram;
        return (
            decimal.Round(macdSeries[^1], 8),
            decimal.Round(signalSeries[^1], 8),
            decimal.Round(lastHistogram, 8),
            decimal.Round(previousHistogram, 8));
    }

    public static decimal CalculateRelativeVolume(IReadOnlyList<decimal> quoteVolumes, int lookback)
    {
        if (quoteVolumes.Count < lookback + 1)
        {
            return 1m;
        }

        var current = quoteVolumes[^1];
        var avg = quoteVolumes.Skip(Math.Max(0, quoteVolumes.Count - lookback - 1)).Take(lookback).DefaultIfEmpty(0m).Average();
        return avg <= 0m ? 1m : decimal.Round(current / avg, 4);
    }

    public static decimal CalculateVolatilityPercent(IReadOnlyList<decimal> closes, int lookback)
    {
        if (closes.Count < lookback + 1)
        {
            return 0m;
        }

        var returns = new List<decimal>(lookback);
        var start = closes.Count - lookback - 1;
        for (var i = start + 1; i < closes.Count; i++)
        {
            var prev = closes[i - 1];
            if (prev <= 0m)
            {
                continue;
            }

            returns.Add((closes[i] - prev) / prev);
        }

        if (returns.Count == 0)
        {
            return 0m;
        }

        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).DefaultIfEmpty(0m).Average();
        var std = (decimal)Math.Sqrt((double)Math.Max(0m, variance));
        return decimal.Round(std * 100m, 4);
    }

    private static List<decimal> BuildEmaSeries(IReadOnlyList<decimal> values, int period)
    {
        var series = new List<decimal>();
        if (values.Count < period)
        {
            return series;
        }

        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        series.Add(ema);
        for (var i = period; i < values.Count; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
            series.Add(ema);
        }

        return series;
    }
}
