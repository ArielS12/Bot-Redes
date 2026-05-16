using TradingBots.App.Models;

namespace TradingBots.App.Services;

public static class CandleAggregation
{
    public static List<KlineBar> Resample(IReadOnlyList<KlineBar> source, int intervalMinutes)
    {
        if (source.Count == 0 || intervalMinutes <= 1)
        {
            return source.ToList();
        }

        var buckets = new Dictionary<long, KlineBar>();
        foreach (var bar in source.OrderBy(x => x.OpenTimeUtc))
        {
            var ticks = bar.OpenTimeUtc.Ticks;
            var bucketStart = new DateTime(
                ticks - (ticks % TimeSpan.FromMinutes(intervalMinutes).Ticks),
                DateTimeKind.Utc);
            var key = bucketStart.Ticks;
            if (!buckets.TryGetValue(key, out var agg))
            {
                buckets[key] = new KlineBar
                {
                    OpenTimeUtc = bucketStart,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    QuoteVolume = bar.QuoteVolume
                };
            }
            else
            {
                agg.High = Math.Max(agg.High, bar.High);
                agg.Low = Math.Min(agg.Low, bar.Low);
                agg.Close = bar.Close;
                agg.QuoteVolume += bar.QuoteVolume;
            }
        }

        return buckets.Values.OrderBy(x => x.OpenTimeUtc).ToList();
    }

    public static List<KlineBar> SliceUpTo(IReadOnlyList<KlineBar> bars, DateTime inclusiveEndUtc, int maxCount)
    {
        var filtered = bars.Where(x => x.OpenTimeUtc <= inclusiveEndUtc).ToList();
        if (filtered.Count <= maxCount)
        {
            return filtered;
        }

        return filtered.TakeLast(maxCount).ToList();
    }
}
