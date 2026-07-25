using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IMarketAdvisorService
{
    Task AnalyzeMarketAsync(IReadOnlyCollection<MarketTicker> marketSnapshot);
    Task<List<InvestmentSuggestion>> GetLatestSuggestionsAsync(int take = 8);
}

public sealed class MarketAdvisorService(
    AppDbContext dbContext,
    IBinanceMarketService marketService,
    IMarketStructureService marketStructureService) : IMarketAdvisorService
{
    private const decimal MinAdvisorQuoteVolume24h = 1_000_000m;
    private const decimal MinMoverQuoteVolume24h = 250_000m;
    /// <summary>Rango de movers moderados (anti-chase: evitar pumps >= 6%).</summary>
    private const decimal MinMoverChange24hPercent = 1.5m;
    private const decimal MaxMoverChange24hPercent = 5.5m;
    private const decimal ChaseBlockChange24hPercent = 6m;
    private const decimal ChaseBlockRsi = 65m;
    private const decimal BuyScoreThreshold = 6.2m;
    private const int MaxAdvisorCandidates = 48;

    private static readonly HashSet<string> AdvisorSymbolExclusions = new(StringComparer.Ordinal)
    {
        "UUSDT", "UUSDC"
    };

    public async Task AnalyzeMarketAsync(IReadOnlyCollection<MarketTicker> marketSnapshot)
    {
        if (marketSnapshot.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var tradable = marketSnapshot
            .Where(IsTradableQuoteAsset)
            .Where(x => !AdvisorSymbolExclusions.Contains(x.Symbol))
            .ToList();
        var candidates = BuildCandidateUniverse(tradable);
        if (candidates.Count == 0)
        {
            return;
        }

        var symbols = candidates.Select(x => x.Symbol).ToList();
        var technical1m = await marketService.GetTechnicalSnapshotsAsync(symbols, "1m", 120);
        var technical5m = await marketService.GetTechnicalSnapshotsAsync(symbols, "5m", 120);
        var technical15m = await marketService.GetTechnicalSnapshotsAsync(symbols, "15m", 120);

        var preliminary = candidates
            .Select(x => BuildSuggestion(x, technical1m, technical5m, technical15m, null, now))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.Signal == "BUY")
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.PriceChangePercent24h)
            .Take(16)
            .ToList();
        var structureSymbols = preliminary.Select(x => x.Symbol).ToList();
        var structures = await marketStructureService.GetStructuresAsync(structureSymbols);

        var generated = candidates
            .Select(x =>
            {
                structures.TryGetValue(x.Symbol, out var structure);
                return BuildSuggestion(x, technical1m, technical5m, technical15m, structure, now);
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.Signal == "BUY")
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.PriceChangePercent24h)
            .Take(8)
            .ToList();
        if (generated.Count == 0)
        {
            return;
        }

        dbContext.InvestmentSuggestions.AddRange(generated);

        // Mantiene historial acotado para no crecer sin control.
        var threshold = now.AddDays(-7);
        var old = await dbContext.InvestmentSuggestions
            .Where(x => x.CreatedAtUtc < threshold)
            .ToListAsync();
        if (old.Count > 0)
        {
            dbContext.InvestmentSuggestions.RemoveRange(old);
        }

        await dbContext.SaveChangesAsync();
    }

    private static StrategyType DetectStrategy(MarketTicker ticker, TechnicalMarketSnapshot t1)
    {
        var abs = Math.Abs(ticker.PriceChangePercent24h);
        if (abs < 0.35m && t1.BbPercent < 0.28m && t1.Rsi14 < 45m)
        {
            return StrategyType.MeanReversion;
        }

        return ticker.PriceChangePercent24h >= 0m ? StrategyType.Momentum : StrategyType.Pullback;
    }

    public async Task<List<InvestmentSuggestion>> GetLatestSuggestionsAsync(int take = 8)
    {
        var recent = await dbContext.InvestmentSuggestions
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Max(take * 8, 32))
            .ToListAsync();

        return recent
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.CreatedAtUtc).First())
            .OrderByDescending(x => x.Signal == "BUY")
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.PriceChangePercent24h)
            .Take(take)
            .ToList();
    }

    private static bool IsTradableQuoteAsset(MarketTicker x) =>
        TradingSymbolFilters.IsTradableVolatilePair(x.Symbol);

    private static List<MarketTicker> BuildCandidateUniverse(List<MarketTicker> tradable)
    {
        var byLiquidity = tradable
            .Where(x => x.QuoteVolume24h >= MinAdvisorQuoteVolume24h)
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(24);

        var topMovers = tradable
            .Where(x => x.QuoteVolume24h >= MinMoverQuoteVolume24h)
            .Where(x =>
            {
                var abs = Math.Abs(x.PriceChangePercent24h);
                return abs >= MinMoverChange24hPercent && abs <= MaxMoverChange24hPercent;
            })
            .OrderByDescending(x => Math.Abs(x.PriceChangePercent24h))
            .Take(20);

        var liquidityWeightedMovers = tradable
            .Where(x => x.QuoteVolume24h >= MinMoverQuoteVolume24h)
            .Where(x => Math.Abs(x.PriceChangePercent24h) <= MaxMoverChange24hPercent)
            .OrderByDescending(x => Math.Abs(x.PriceChangePercent24h) * Log10Score(x.QuoteVolume24h))
            .Take(16);

        return byLiquidity
            .Concat(topMovers)
            .Concat(liquidityWeightedMovers)
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxAdvisorCandidates)
            .ToList();
    }

    private static decimal Log10Score(decimal value) =>
        value <= 0m ? 0m : (decimal)Math.Log10((double)value);

    private static string TrimRationale(string rationale) =>
        rationale.Length <= 500 ? rationale : rationale[..497] + "...";

    private static decimal ScoreFlatMarketPenalty(decimal change24h) =>
        Math.Abs(change24h) < 0.18m ? 0.28m : 0m;

    private static InvestmentSuggestion? BuildSuggestion(
        MarketTicker ticker,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf1,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf5,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf15,
        MarketStructureSnapshot? structure,
        DateTime now)
    {
        if (!tf1.TryGetValue(ticker.Symbol, out var t1) ||
            !tf5.TryGetValue(ticker.Symbol, out var t5) ||
            !tf15.TryGetValue(ticker.Symbol, out var t15))
        {
            return null;
        }

        if (AdvisorSymbolExclusions.Contains(ticker.Symbol))
        {
            return null;
        }

        var strategy = DetectStrategy(ticker, t1);
        if (strategy == StrategyType.MeanReversion)
        {
            // MeanReversion deshabilitado en advisor para AutoPilot.
            strategy = ticker.PriceChangePercent24h >= 0m ? StrategyType.Momentum : StrategyType.Pullback;
        }
        var trendStrength = ScoreTrend(t1, t5, t15);
        var momentumStrength = ScoreMomentum(t1, ticker.PriceChangePercent24h);
        var liquidityStrength = ScoreLiquidity(ticker.QuoteVolume24h, t1.RelativeVolume);
        var volatilityPenalty = ScoreVolatilityPenalty(ticker.PriceChangePercent24h, t1);
        var costPenalty = ScoreExecutionCostPenalty(ticker, t1);
        var flatPenalty = ScoreFlatMarketPenalty(ticker.PriceChangePercent24h);
        var contextScore = ScoreMarketContext(structure);
        var totalScore = Math.Max(0m, trendStrength + momentumStrength + liquidityStrength + contextScore - volatilityPenalty - costPenalty - flatPenalty);

        var confidence = totalScore >= 7.2m ? "ALTA" : totalScore >= 5.5m ? "MEDIA" : "BAJA";
        var chaseBlocked = Math.Abs(ticker.PriceChangePercent24h) >= ChaseBlockChange24hPercent &&
                           (t1.Rsi14 >= ChaseBlockRsi || structure?.IsOverextended == true);
        var signal = chaseBlocked
            ? (totalScore >= 3.35m ? "WATCH" : "HOLD")
            : totalScore >= BuyScoreThreshold
                ? "BUY"
                : totalScore >= 3.35m
                    ? "WATCH"
                    : "HOLD";
        if (chaseBlocked)
        {
            totalScore = Math.Min(totalScore, BuyScoreThreshold - 0.05m);
        }
        var contextText = structure?.HasData == true
            ? $"Contexto={contextScore:0.00} ({structure.Summary})"
            : "Contexto=sin historial 30-90d";
        var rationale = $"Confianza {confidence}. 24h={ticker.PriceChangePercent24h:0.##}%, Trend={trendStrength:0.00}, Momentum={momentumStrength:0.00}, Liquidez={liquidityStrength:0.00}, {contextText}, Riesgo={volatilityPenalty:0.00}, Coste={costPenalty:0.00}.";

        return new InvestmentSuggestion
        {
            Symbol = ticker.Symbol,
            Signal = signal,
            Score = decimal.Round(totalScore, 4),
            PriceChangePercent24h = ticker.PriceChangePercent24h,
            Rationale = TrimRationale(rationale),
            CreatedAtUtc = now,
            SuggestedStrategy = strategy
        };
    }

    private static decimal ScoreTrend(TechnicalMarketSnapshot t1, TechnicalMarketSnapshot t5, TechnicalMarketSnapshot t15)
    {
        decimal score = 0m;
        if (t1.EmaFast > t1.EmaSlow) score += 1.2m;
        if (t5.EmaFast > t5.EmaSlow) score += 1.5m;
        if (t15.EmaFast > t15.EmaSlow) score += 1.8m;
        if (t15.MacdLine >= t15.MacdSignal) score += 0.9m;
        return score;
    }

    private static decimal ScoreMomentum(TechnicalMarketSnapshot t1, decimal change24h)
    {
        decimal score = 0m;
        if (t1.MacdLine > t1.MacdSignal && t1.MacdHistogram > t1.PreviousMacdHistogram) score += 1.2m;
        if (t1.Rsi14 is >= 50m and <= 72m) score += 1.1m;
        if (change24h > 0m)
        {
            score += Math.Min(1.8m, change24h / 18m);
        }
        else
        {
            score += Math.Min(0.9m, Math.Abs(change24h) / 25m);
        }
        return score;
    }

    private static decimal ScoreMarketContext(MarketStructureSnapshot? structure)
    {
        if (structure is null || !structure.HasData)
        {
            return 0m;
        }

        var score = structure.ContextScore;
        if (structure.HasBullishFlag)
        {
            score += 0.35m;
        }

        if (structure.IsOverextended && !structure.HasBullishFlag)
        {
            score -= 0.35m;
        }

        if (structure.DistanceToResistancePercent is > 0m and < 3m)
        {
            score -= 0.25m;
        }

        if (structure.DistanceToSupportPercent is >= 3m and <= 18m)
        {
            score += 0.15m;
        }

        return decimal.Round(Math.Clamp(score, -1.5m, 2.5m), 4);
    }

    private static decimal ScoreLiquidity(decimal quoteVol24h, decimal relativeVolume)
    {
        var volScore = quoteVol24h >= 15_000_000m ? 1.4m : quoteVol24h >= 5_000_000m ? 1.0m : 0.6m;
        var relScore = relativeVolume >= 1.25m ? 1.0m : relativeVolume >= 0.95m ? 0.7m : 0.3m;
        return volScore + relScore;
    }

    private static decimal ScoreVolatilityPenalty(decimal change24h, TechnicalMarketSnapshot t1)
    {
        decimal penalty = 0m;
        if (Math.Abs(change24h) > 18m) penalty += 0.9m;
        if (t1.LastPrice > 0m)
        {
            var spreadProxy = (Math.Abs(t1.EmaFast - t1.EmaSlow) / t1.LastPrice) * 100m;
            if (spreadProxy > 2.2m) penalty += 0.8m;
        }

        return penalty;
    }

    private static decimal ScoreExecutionCostPenalty(MarketTicker ticker, TechnicalMarketSnapshot t1)
    {
        // Aproxima costes combinados (fee+slippage+spread proxy) para evitar setups con edge fragil.
        var feeAndSlipBps = 18m; // 0.18%
        var spreadProxyBps = 0m;
        if (t1.LastPrice > 0m)
        {
            var spreadProxyPct = (Math.Abs(t1.EmaFast - t1.EmaSlow) / t1.LastPrice) * 100m;
            spreadProxyBps = Math.Min(40m, spreadProxyPct * 20m);
        }

        var liquidityPenaltyBps = ticker.QuoteVolume24h < 5_000_000m ? 14m : 4m;
        var totalBps = feeAndSlipBps + spreadProxyBps + liquidityPenaltyBps;
        return Math.Min(1.3m, totalBps / 50m);
    }
}
