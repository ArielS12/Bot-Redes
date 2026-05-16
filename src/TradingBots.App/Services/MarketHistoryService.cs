using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IMarketHistoryService
{
    Task SyncSymbolsAsync(IEnumerable<string> symbols, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, LongTermRegimeSnapshot>> GetRegimesAsync(IEnumerable<string> symbols, CancellationToken ct = default);
}

public sealed class MarketHistoryService(
    AppDbContext db,
    IBinanceMarketService marketService,
    ILogger<MarketHistoryService> logger) : IMarketHistoryService
{
    private const int DailyCandleLimit = 365;
    private const int HourlyCandleLimit = 720;
    private static readonly SemaphoreSlim SyncGate = new(2, 2);

    public async Task SyncSymbolsAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var list = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x) && TradingSymbolFilters.IsTradableVolatilePair(x))
            .Distinct()
            .ToList();
        if (list.Count == 0)
        {
            return;
        }

        foreach (var symbol in list)
        {
            ct.ThrowIfCancellationRequested();
            await SyncGate.WaitAsync(ct);
            try
            {
                await SyncIntervalAsync(symbol, "1d", DailyCandleLimit, ct);
                await SyncIntervalAsync(symbol, "1h", HourlyCandleLimit, ct);
            }
            finally
            {
                SyncGate.Release();
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, LongTermRegimeSnapshot>> GetRegimesAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        var list = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        var result = new Dictionary<string, LongTermRegimeSnapshot>(StringComparer.Ordinal);
        foreach (var symbol in list)
        {
            ct.ThrowIfCancellationRequested();
            result[symbol] = await BuildRegimeAsync(symbol, ct);
        }

        return result;
    }

    private async Task SyncIntervalAsync(string symbol, string interval, int targetCount, CancellationToken ct)
    {
        var latest = await db.MarketCandles
            .Where(x => x.Symbol == symbol && x.Interval == interval)
            .OrderByDescending(x => x.OpenTimeUtc)
            .Select(x => x.OpenTimeUtc)
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<KlineBar> bars;
        if (latest == default)
        {
            bars = await marketService.FetchKlinesAsync(symbol, interval, targetCount);
        }
        else
        {
            var startMs = new DateTimeOffset(latest.AddMilliseconds(1)).ToUnixTimeMilliseconds();
            bars = await marketService.FetchKlinesAsync(symbol, interval, Math.Min(targetCount, 500), startMs);
        }

        if (bars.Count == 0)
        {
            return;
        }

        var existingTimes = await db.MarketCandles
            .Where(x => x.Symbol == symbol && x.Interval == interval)
            .Select(x => x.OpenTimeUtc)
            .ToListAsync(ct);
        var existingSet = existingTimes.ToHashSet();

        var toAdd = bars
            .Where(b => !existingSet.Contains(b.OpenTimeUtc))
            .Select(b => new MarketCandle
            {
                Symbol = symbol,
                Interval = interval,
                OpenTimeUtc = b.OpenTimeUtc,
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                QuoteVolume = b.QuoteVolume
            })
            .ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        db.MarketCandles.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Historial {Symbol} {Interval}: +{Count} velas", symbol, interval, toAdd.Count);

        await TrimOldCandlesAsync(symbol, interval, targetCount + 30, ct);
    }

    private async Task TrimOldCandlesAsync(string symbol, string interval, int keep, CancellationToken ct)
    {
        var total = await db.MarketCandles.CountAsync(x => x.Symbol == symbol && x.Interval == interval, ct);
        if (total <= keep)
        {
            return;
        }

        var threshold = await db.MarketCandles
            .Where(x => x.Symbol == symbol && x.Interval == interval)
            .OrderByDescending(x => x.OpenTimeUtc)
            .Skip(keep)
            .Select(x => x.OpenTimeUtc)
            .FirstOrDefaultAsync(ct);
        if (threshold == default)
        {
            return;
        }

        await db.MarketCandles
            .Where(x => x.Symbol == symbol && x.Interval == interval && x.OpenTimeUtc < threshold)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<LongTermRegimeSnapshot> BuildRegimeAsync(string symbol, CancellationToken ct)
    {
        var daily = await db.MarketCandles
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Interval == "1d")
            .OrderBy(x => x.OpenTimeUtc)
            .ToListAsync(ct);
        if (daily.Count < 60)
        {
            return new LongTermRegimeSnapshot { Symbol = symbol, HasData = false };
        }

        var closes = daily.Select(x => x.Close).ToList();
        var highs = daily.Select(x => x.High).ToList();
        var lows = daily.Select(x => x.Low).ToList();
        var last = daily[^1];
        var lookback90 = daily.TakeLast(Math.Min(90, daily.Count)).ToList();
        var high90 = lookback90.Max(x => x.High);
        var low90 = lookback90.Min(x => x.Low);
        var range = high90 - low90;
        var percentile90 = range <= 0m
            ? 50m
            : decimal.Round(((last.Close - low90) / range) * 100m, 2);

        var atrSeries = new List<decimal>(daily.Count);
        for (var i = 14; i < daily.Count; i++)
        {
            var sliceH = highs.Take(i + 1).ToList();
            var sliceL = lows.Take(i + 1).ToList();
            var sliceC = closes.Take(i + 1).ToList();
            atrSeries.Add(TechnicalIndicatorMath.CalculateAtrPercent(sliceH, sliceL, sliceC, 14));
        }

        var currentAtr = atrSeries.Count > 0 ? atrSeries[^1] : 0m;
        var atrPercentile = TechnicalIndicatorMath.PercentileRank(atrSeries, currentAtr);
        var ema50 = TechnicalIndicatorMath.CalculateEma(closes, 50);
        var ema200 = TechnicalIndicatorMath.CalculateEma(closes, Math.Min(200, closes.Count));

        return new LongTermRegimeSnapshot
        {
            Symbol = symbol,
            HasData = true,
            LastClose = last.Close,
            DailyEma50 = ema50,
            DailyEma200 = ema200,
            PricePercentileIn90d = percentile90,
            DailyAtrPercent = currentAtr,
            DailyAtrPercentileVsYear = atrPercentile,
            DailyTrendUp = ema50 >= ema200,
            LastDailyOpenUtc = last.OpenTimeUtc
        };
    }
}
