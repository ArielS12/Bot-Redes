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
    IBinanceMarketService marketService) : IMarketAdvisorService
{
    public async Task AnalyzeMarketAsync(IReadOnlyCollection<MarketTicker> marketSnapshot)
    {
        if (marketSnapshot.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var candidates = marketSnapshot
            .Where(IsTradableQuoteAsset)
            .Where(x => x.QuoteVolume24h >= 1_000_000m)
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(30)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var symbols = candidates.Select(x => x.Symbol).ToList();
        var technical1m = await marketService.GetTechnicalSnapshotsAsync(symbols, "1m", 120);
        var technical5m = await marketService.GetTechnicalSnapshotsAsync(symbols, "5m", 120);
        var technical15m = await marketService.GetTechnicalSnapshotsAsync(symbols, "15m", 120);

        var generated = candidates
            .Select(x => BuildSuggestion(x, technical1m, technical5m, technical15m, now))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.Score)
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

    private static StrategyType DetectStrategy(decimal change24h) =>
        change24h >= 0m ? StrategyType.Momentum : StrategyType.Pullback;

    public async Task<List<InvestmentSuggestion>> GetLatestSuggestionsAsync(int take = 8) =>
        await dbContext.InvestmentSuggestions
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .ToListAsync();

    private static bool IsTradableQuoteAsset(MarketTicker x) =>
        x.Symbol.EndsWith("USDT", StringComparison.Ordinal) || x.Symbol.EndsWith("USDC", StringComparison.Ordinal);

    private static InvestmentSuggestion? BuildSuggestion(
        MarketTicker ticker,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf1,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf5,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> tf15,
        DateTime now)
    {
        if (!tf1.TryGetValue(ticker.Symbol, out var t1) ||
            !tf5.TryGetValue(ticker.Symbol, out var t5) ||
            !tf15.TryGetValue(ticker.Symbol, out var t15))
        {
            return null;
        }

        var strategy = DetectStrategy(ticker.PriceChangePercent24h);
        var trendStrength = ScoreTrend(t1, t5, t15);
        var momentumStrength = ScoreMomentum(t1, ticker.PriceChangePercent24h);
        var liquidityStrength = ScoreLiquidity(ticker.QuoteVolume24h, t1.RelativeVolume);
        var volatilityPenalty = ScoreVolatilityPenalty(ticker.PriceChangePercent24h, t1);
        var costPenalty = ScoreExecutionCostPenalty(ticker, t1);
        var totalScore = Math.Max(0m, trendStrength + momentumStrength + liquidityStrength - volatilityPenalty - costPenalty);

        var confidence = totalScore >= 6.5m ? "ALTA" : totalScore >= 4.5m ? "MEDIA" : "BAJA";
        var signal = totalScore >= 4.8m ? "BUY" : totalScore >= 3.2m ? "WATCH" : "HOLD";
        var rationale = $"Confianza {confidence}. Trend={trendStrength:0.00}, Momentum={momentumStrength:0.00}, Liquidez={liquidityStrength:0.00}, Riesgo={volatilityPenalty:0.00}, Coste={costPenalty:0.00}.";

        return new InvestmentSuggestion
        {
            Symbol = ticker.Symbol,
            Signal = signal,
            Score = decimal.Round(totalScore, 4),
            PriceChangePercent24h = ticker.PriceChangePercent24h,
            Rationale = rationale,
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
        score += Math.Min(1.2m, Math.Abs(change24h) / 25m);
        return score;
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

        var liquidityPenaltyBps = ticker.QuoteVolume24h < 3_000_000m ? 14m : 4m;
        var totalBps = feeAndSlipBps + spreadProxyBps + liquidityPenaltyBps;
        return Math.Min(1.3m, totalBps / 50m);
    }
}
