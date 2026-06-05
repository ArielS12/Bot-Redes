using System.Collections.Concurrent;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IMarketStructureService
{
    Task<IReadOnlyDictionary<string, MarketStructureSnapshot>> GetStructuresAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default);
}

public sealed class MarketStructureService(
    IBinanceMarketService marketService,
    ILogger<MarketStructureService> logger) : IMarketStructureService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, MarketStructureSnapshot>> GetStructuresAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        var list = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x) && TradingSymbolFilters.IsTradableVolatilePair(x))
            .Distinct()
            .ToList();
        var result = new Dictionary<string, MarketStructureSnapshot>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var symbol in list)
        {
            ct.ThrowIfCancellationRequested();
            if (Cache.TryGetValue(symbol, out var cached) && cached.ExpiresAtUtc > now)
            {
                result[symbol] = cached.Snapshot;
                continue;
            }

            var snapshot = await BuildStructureAsync(symbol, ct);
            Cache[symbol] = new CacheEntry(snapshot, now.Add(CacheTtl));
            result[symbol] = snapshot;
        }

        return result;
    }

    private async Task<MarketStructureSnapshot> BuildStructureAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var daily = await marketService.FetchKlinesAsync(symbol, "1d", 120);
            var fourHour = await marketService.FetchKlinesAsync(symbol, "4h", 180);
            ct.ThrowIfCancellationRequested();

            if (daily.Count < 35 || fourHour.Count < 36)
            {
                return new MarketStructureSnapshot
                {
                    Symbol = symbol,
                    HasData = false,
                    Summary = "Contexto largo insuficiente"
                };
            }

            return BuildFromCandles(symbol, daily, fourHour);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo calcular estructura de mercado para {Symbol}", symbol);
            return new MarketStructureSnapshot
            {
                Symbol = symbol,
                HasData = false,
                Summary = "Sin contexto largo"
            };
        }
    }

    private static MarketStructureSnapshot BuildFromCandles(
        string symbol,
        IReadOnlyList<KlineBar> daily,
        IReadOnlyList<KlineBar> fourHour)
    {
        var last = daily[^1].Close;
        var lookback90 = daily.TakeLast(Math.Min(90, daily.Count)).ToList();
        var lookback30 = daily.TakeLast(Math.Min(30, daily.Count)).ToList();
        var high90 = lookback90.Max(x => x.High);
        var low90 = lookback90.Min(x => x.Low);
        var range90 = high90 - low90;
        var percentile90 = range90 <= 0m ? 50m : decimal.Round((last - low90) / range90 * 100m, 2);
        var change30 = PercentChange(lookback30[0].Open, last);
        var change90 = PercentChange(lookback90[0].Open, last);
        var closes = daily.Select(x => x.Close).ToList();
        var ema20 = TechnicalIndicatorMath.CalculateEma(closes, Math.Min(20, closes.Count));
        var ema50 = TechnicalIndicatorMath.CalculateEma(closes, Math.Min(50, closes.Count));
        var isUptrend = ema20 >= ema50 && change30 > 0m;

        var trendScore = 0m;
        if (isUptrend) trendScore += 0.75m;
        if (change30 is >= 3m and <= 45m) trendScore += 0.45m;
        if (change90 > 0m) trendScore += 0.25m;
        if (percentile90 is >= 45m and <= 88m) trendScore += 0.35m;

        var aboveEma20 = ema20 > 0m ? PercentChange(ema20, last) : 0m;
        var overextensionPenalty = 0m;
        if (percentile90 > 94m) overextensionPenalty += 0.55m;
        if (change30 > 65m) overextensionPenalty += 0.55m;
        if (aboveEma20 > 18m) overextensionPenalty += 0.35m;
        var isOverextended = overextensionPenalty >= 0.55m;

        var flagScore = ScoreBullishFlag(fourHour, out var hasBullishFlag);
        var contextScore = decimal.Round(Math.Clamp(trendScore + flagScore - overextensionPenalty, -1.5m, 2.5m), 4);
        var distanceToSupport = low90 > 0m ? PercentChange(low90, last) : 0m;
        var distanceToResistance = last > 0m ? PercentChange(last, high90) : 0m;
        var summary = BuildSummary(isUptrend, hasBullishFlag, isOverextended, percentile90, change30, contextScore);

        return new MarketStructureSnapshot
        {
            Symbol = symbol,
            HasData = true,
            ContextScore = contextScore,
            TrendScore = decimal.Round(trendScore, 4),
            BullishFlagScore = decimal.Round(flagScore, 4),
            OverextensionPenalty = decimal.Round(overextensionPenalty, 4),
            Change30dPercent = decimal.Round(change30, 2),
            Change90dPercent = decimal.Round(change90, 2),
            PricePercentile90d = percentile90,
            Support90d = low90,
            Resistance90d = high90,
            DistanceToSupportPercent = decimal.Round(distanceToSupport, 2),
            DistanceToResistancePercent = decimal.Round(distanceToResistance, 2),
            IsUptrend = isUptrend,
            IsOverextended = isOverextended,
            HasBullishFlag = hasBullishFlag,
            Summary = summary
        };
    }

    private static decimal ScoreBullishFlag(IReadOnlyList<KlineBar> fourHour, out bool hasBullishFlag)
    {
        hasBullishFlag = false;
        if (fourHour.Count < 54)
        {
            return 0m;
        }

        var impulse = fourHour.Skip(Math.Max(0, fourHour.Count - 54)).Take(30).ToList();
        var consolidation = fourHour.TakeLast(24).ToList();
        var impulseLow = impulse.Min(x => x.Low);
        var impulseHigh = impulse.Max(x => x.High);
        var impulseMove = PercentChange(impulseLow, impulseHigh);
        var consHigh = consolidation.Max(x => x.High);
        var consLow = consolidation.Min(x => x.Low);
        var lastClose = consolidation[^1].Close;
        var consRange = consLow > 0m ? PercentChange(consLow, consHigh) : 100m;
        var pullbackFromImpulseHigh = impulseHigh > 0m ? PercentChange(impulseHigh, consLow) : 0m;
        var nearBreakout = consHigh > 0m && lastClose >= consHigh * 0.97m;
        var avgImpulseVol = impulse.Select(x => x.QuoteVolume).DefaultIfEmpty(0m).Average();
        var avgConsVol = consolidation.Select(x => x.QuoteVolume).DefaultIfEmpty(0m).Average();
        var volumeCooling = avgImpulseVol <= 0m || avgConsVol <= avgImpulseVol * 1.15m;

        var score = 0m;
        if (impulseMove >= 8m) score += 0.45m;
        if (consRange is > 1.5m and <= 18m) score += 0.3m;
        if (pullbackFromImpulseHigh is <= -2m and >= -22m) score += 0.25m;
        if (nearBreakout) score += 0.25m;
        if (volumeCooling) score += 0.15m;

        hasBullishFlag = score >= 0.85m;
        return hasBullishFlag ? score : Math.Min(score, 0.45m);
    }

    private static decimal PercentChange(decimal from, decimal to) =>
        from <= 0m ? 0m : ((to - from) / from) * 100m;

    private static string BuildSummary(
        bool isUptrend,
        bool hasBullishFlag,
        bool isOverextended,
        decimal percentile90,
        decimal change30,
        decimal contextScore)
    {
        var regime = isUptrend ? "tendencia 30d alcista" : "tendencia 30d lateral/bajista";
        var flag = hasBullishFlag ? ", bandera alcista probable" : string.Empty;
        var extended = isOverextended ? ", ojo sobreextension" : string.Empty;
        return $"{regime}{flag}{extended}. pct90d={percentile90:0.#}, 30d={change30:0.#}%, ctx={contextScore:0.00}";
    }

    private sealed record CacheEntry(MarketStructureSnapshot Snapshot, DateTime ExpiresAtUtc);
}
