using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface ITradeMlService
{
    Task<MlBuyEvaluation> EvaluateBuyAsync(
        string symbol,
        StrategyType strategy,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        int minSamples,
        CancellationToken ct = default);

    Task RecordEntryAsync(
        Guid botId,
        string symbol,
        StrategyType strategy,
        decimal entryPrice,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        decimal predictedWinProbability,
        CancellationToken ct = default);

    Task RecordExitAsync(Guid botId, string symbol, decimal realizedPnlUsdt, CancellationToken ct = default);
    Task<MlRuntimeSummary> GetSummaryAsync(BinanceConnectionSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Ejecuta el mismo pipeline de entrenamiento que una evaluacion BUY real y devuelve estado en esta peticion (util para diagnosticos en runtime).
    /// </summary>
    Task<MlDiagnosticsView> GetDiagnosticsAsync(BinanceConnectionSettings settings, CancellationToken ct = default);

    Task RecordShadowSignalAsync(
        Guid botId,
        string symbol,
        StrategyType strategy,
        decimal entryPrice,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        string rejectReason,
        decimal predictedWinProbability,
        CancellationToken ct = default);

    Task<int> ResolveShadowSignalsAsync(
        IReadOnlyDictionary<string, MarketTicker> marketSnapshot,
        CancellationToken ct = default);
}

public sealed class MlBuyEvaluation
{
    public decimal WinProbability { get; set; }
    public bool Trained { get; set; }
    public int Samples { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class TradeMlService(IServiceScopeFactory scopeFactory) : ITradeMlService
{
    private const decimal ShadowTpPercent = 1.5m;
    private const decimal ShadowSlPercent = 1.85m;
    private static readonly TimeSpan ShadowMinAge = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ShadowMaxAge = TimeSpan.FromMinutes(180);
    private static readonly TimeSpan TrainCooldown = TimeSpan.FromMinutes(30);
    private const int RetrainEveryNewCloses = 25;

    private DateTime? _lastTrainedUtc;
    private double[]? _weights;
    private int _trainedSamples;
    private int _closedCountAtLastTrain;
    private decimal _lastPrecision;
    private decimal _lastRecall;
    private decimal _lastBaselineWinRate;
    private readonly object _modelLock = new();

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }

    public async Task<MlBuyEvaluation> EvaluateBuyAsync(
        string symbol,
        StrategyType strategy,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        int minSamples,
        CancellationToken ct = default)
    {
        var closed = await WithDbAsync(async db =>
            await db.MlTradeObservations
                .Where(x => x.ClosedAtUtc != null && x.IsWin != null)
                .OrderByDescending(x => x.ClosedAtUtc)
                .Take(3000)
                .ToListAsync(ct), ct);

        if (closed.Count < minSamples)
        {
            var fallback = ComputeHeuristicProbability(snapshot, ticker, strategy);
            return new MlBuyEvaluation
            {
                WinProbability = fallback,
                Trained = false,
                Samples = closed.Count,
                Note = $"Muestra insuficiente ML ({closed.Count}/{minSamples})."
            };
        }

        EnsureTrained(closed);
        var features = BuildFeatures(snapshot, ticker, strategy);
        var probability = Predict(features);
        return new MlBuyEvaluation
        {
            WinProbability = decimal.Round((decimal)probability, 4),
            Trained = true,
            Samples = closed.Count,
            Note = $"Modelo logistic cacheado ({closed.Count} muestras)."
        };
    }

    public async Task RecordEntryAsync(
        Guid botId,
        string symbol,
        StrategyType strategy,
        decimal entryPrice,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        decimal predictedWinProbability,
        CancellationToken ct = default)
    {
        await WithDbAsync(async db =>
        {
            var hasOpen = await db.MlTradeObservations
                .AnyAsync(x => x.BotId == botId && x.Symbol == symbol && x.ClosedAtUtc == null && !x.IsShadow, ct);
            if (hasOpen)
            {
                return;
            }

            db.MlTradeObservations.Add(new MlTradeObservation
            {
                BotId = botId,
                Symbol = symbol,
                StrategyType = strategy,
                EntryAtUtc = DateTime.UtcNow,
                EntryPrice = entryPrice,
                PredictedWinProbability = decimal.Round(predictedWinProbability, 6),
                EmaGapPct = decimal.Round(NormEmaGapPct(snapshot), 6),
                Rsi14 = decimal.Round(snapshot.Rsi14, 6),
                MacdHistogram = decimal.Round(snapshot.MacdHistogram, 8),
                RelativeVolume = decimal.Round(snapshot.RelativeVolume, 6),
                PriceChangePercent24h = decimal.Round(ticker.PriceChangePercent24h, 6),
                QuoteVolume24h = decimal.Round(ticker.QuoteVolume24h, 2),
                IsWin = null
            });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task RecordExitAsync(Guid botId, string symbol, decimal realizedPnlUsdt, CancellationToken ct = default)
    {
        await WithDbAsync(async db =>
        {
            var open = await db.MlTradeObservations
                .Where(x => x.BotId == botId && x.Symbol == symbol && x.ClosedAtUtc == null && !x.IsShadow)
                .OrderByDescending(x => x.EntryAtUtc)
                .FirstOrDefaultAsync(ct);
            if (open is null)
            {
                return;
            }

            open.ClosedAtUtc = DateTime.UtcNow;
            open.RealizedPnlUsdt = decimal.Round(realizedPnlUsdt, 4);
            open.IsWin = realizedPnlUsdt > 0m;
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task RecordShadowSignalAsync(
        Guid botId,
        string symbol,
        StrategyType strategy,
        decimal entryPrice,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        string rejectReason,
        decimal predictedWinProbability,
        CancellationToken ct = default)
    {
        await WithDbAsync(async db =>
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            var recent = await db.MlTradeObservations
                .AnyAsync(x =>
                    x.IsShadow &&
                    x.BotId == botId &&
                    x.Symbol == symbol &&
                    x.ClosedAtUtc == null &&
                    x.EntryAtUtc >= cutoff, ct);
            if (recent)
            {
                return;
            }

            db.MlTradeObservations.Add(new MlTradeObservation
            {
                BotId = botId,
                Symbol = symbol,
                StrategyType = strategy,
                EntryAtUtc = DateTime.UtcNow,
                EntryPrice = entryPrice,
                PredictedWinProbability = decimal.Round(predictedWinProbability, 6),
                EmaGapPct = decimal.Round(NormEmaGapPct(snapshot), 6),
                Rsi14 = decimal.Round(snapshot.Rsi14, 6),
                MacdHistogram = decimal.Round(snapshot.MacdHistogram, 8),
                RelativeVolume = decimal.Round(snapshot.RelativeVolume, 6),
                PriceChangePercent24h = decimal.Round(ticker.PriceChangePercent24h, 6),
                QuoteVolume24h = decimal.Round(ticker.QuoteVolume24h, 2),
                IsShadow = true,
                RejectReason = rejectReason.Length > 200 ? rejectReason[..200] : rejectReason
            });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<int> ResolveShadowSignalsAsync(
        IReadOnlyDictionary<string, MarketTicker> marketSnapshot,
        CancellationToken ct = default)
    {
        return await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var open = await db.MlTradeObservations
                .Where(x => x.IsShadow && x.ClosedAtUtc == null && x.EntryAtUtc <= now - ShadowMinAge)
                .ToListAsync(ct);
            if (open.Count == 0)
            {
                return 0;
            }

            var resolved = 0;
            foreach (var row in open)
            {
                if (!marketSnapshot.TryGetValue(row.Symbol, out var ticker) || ticker.LastPrice <= 0m)
                {
                    continue;
                }

                var age = now - row.EntryAtUtc;
                var movePct = row.EntryPrice > 0m
                    ? (ticker.LastPrice - row.EntryPrice) / row.EntryPrice * 100m
                    : 0m;

                decimal hypotheticalPnl;
                bool? isWin;
                if (movePct >= ShadowTpPercent)
                {
                    hypotheticalPnl = decimal.Round(MinQuoteOrderUsdt * ShadowTpPercent / 100m, 4);
                    isWin = true;
                }
                else if (movePct <= -ShadowSlPercent)
                {
                    hypotheticalPnl = decimal.Round(-MinQuoteOrderUsdt * ShadowSlPercent / 100m, 4);
                    isWin = false;
                }
                else if (age >= ShadowMaxAge)
                {
                    hypotheticalPnl = decimal.Round(MinQuoteOrderUsdt * movePct / 100m, 4);
                    isWin = hypotheticalPnl > 0m;
                }
                else
                {
                    continue;
                }

                row.ClosedAtUtc = now;
                row.RealizedPnlUsdt = hypotheticalPnl;
                row.IsWin = isWin;
                resolved++;
            }

            if (resolved > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            return resolved;
        }, ct);
    }

    private const decimal MinQuoteOrderUsdt = 10m;

    public async Task<MlRuntimeSummary> GetSummaryAsync(BinanceConnectionSettings settings, CancellationToken ct = default)
    {
        return await WithDbAsync(async db =>
        {
            var total = await db.MlTradeObservations.CountAsync(ct);
            var closed = await db.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin != null, ct);
            var wins = await db.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin == true, ct);
            var winRate = closed == 0 ? 0m : decimal.Round((wins * 100m) / closed, 2);
            return new MlRuntimeSummary
            {
                Enabled = settings.MlEnabled,
                ShadowMode = settings.MlShadowMode,
                MinWinProbability = settings.MlMinWinProbability,
                MinSamples = settings.MlMinSamples,
                TotalSamples = total,
                ClosedSamples = closed,
                WinRatePercent = winRate,
                LastTrainedUtc = _lastTrainedUtc
            };
        }, ct);
    }

    public async Task<MlDiagnosticsView> GetDiagnosticsAsync(BinanceConnectionSettings settings, CancellationToken ct = default)
    {
        return await WithDbAsync(async db =>
        {
            var minSamples = settings.MlMinSamples <= 0 ? 80 : settings.MlMinSamples;
            var threshold = settings.MlMinWinProbability <= 0m ? 0.55m : settings.MlMinWinProbability;
            var total = await db.MlTradeObservations.CountAsync(ct);
            var closedCount = await db.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin != null, ct);
            var wins = await db.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin == true, ct);
            var winRate = closedCount == 0 ? 0m : decimal.Round((wins * 100m) / closedCount, 2);

            var closedRows = await db.MlTradeObservations
                .Where(x => x.ClosedAtUtc != null && x.IsWin != null)
                .OrderByDescending(x => x.ClosedAtUtc)
                .Take(3000)
                .ToListAsync(ct);

            var ran = false;
            var note = string.Empty;
            if (closedRows.Count < minSamples)
            {
                note = $"Entrenamiento no ejecutado: {closedRows.Count} observaciones cerradas < minimo {minSamples}.";
            }
            else
            {
                var before = _lastTrainedUtc;
                EnsureTrained(closedRows);
                EvaluateHoldoutMetrics(closedRows, threshold);
                ran = _weights is not null;
                note = before == _lastTrainedUtc && before is not null
                    ? $"Modelo cacheado (TTL {TrainCooldown.TotalMinutes:0} min / +{RetrainEveryNewCloses} cierres). Precision={_lastPrecision:0.#}% Recall={_lastRecall:0.#}% vs baseline {_lastBaselineWinRate:0.#}%."
                    : $"Entrenamiento logistic sobre {closedRows.Count} filas. Precision={_lastPrecision:0.#}% Recall={_lastRecall:0.#}% vs baseline {_lastBaselineWinRate:0.#}%.";
            }

            return new MlDiagnosticsView
            {
                Enabled = settings.MlEnabled,
                ShadowMode = settings.MlShadowMode,
                MinWinProbability = settings.MlMinWinProbability,
                MinSamples = minSamples,
                TotalSamples = total,
                ClosedSamples = closedCount,
                WinRatePercent = winRate,
                TrainingRanThisRequest = ran && (_lastTrainedUtc is not null),
                ModelReady = _weights is not null,
                ClosedRowsUsedForTraining = closedRows.Count,
                LastTrainedUtc = _lastTrainedUtc,
                PrecisionPercent = _lastPrecision,
                RecallPercent = _lastRecall,
                BaselineWinRatePercent = _lastBaselineWinRate,
                Note = note
            };
        }, ct);
    }

    private void EnsureTrained(List<MlTradeObservation> rows)
    {
        lock (_modelLock)
        {
            var newCloses = rows.Count - _closedCountAtLastTrain;
            if (_weights is not null &&
                _lastTrainedUtc is not null &&
                (DateTime.UtcNow - _lastTrainedUtc.Value) < TrainCooldown &&
                newCloses < RetrainEveryNewCloses)
            {
                return;
            }

            var w = new double[7]; // bias + 6 features
            const double lr = 0.05;
            const double l2 = 0.0005;
            for (var epoch = 0; epoch < 240; epoch++)
            {
                foreach (var row in rows)
                {
                    var x = BuildFeaturesFromObservation(row);
                    var y = row.IsWin == true ? 1d : 0d;
                    var p = Sigmoid(Dot(w, x));
                    var err = p - y;
                    for (var i = 0; i < w.Length; i++)
                    {
                        w[i] -= lr * (err * x[i] + l2 * w[i]);
                    }
                }
            }

            _weights = w;
            _trainedSamples = rows.Count;
            _closedCountAtLastTrain = rows.Count;
            _lastTrainedUtc = DateTime.UtcNow;
        }
    }

    private void EvaluateHoldoutMetrics(List<MlTradeObservation> rows, decimal threshold)
    {
        if (_weights is null || rows.Count < 40)
        {
            _lastPrecision = 0m;
            _lastRecall = 0m;
            _lastBaselineWinRate = 0m;
            return;
        }

        var holdout = rows.Take(Math.Max(40, rows.Count / 5)).ToList();
        var tp = 0;
        var fp = 0;
        var fn = 0;
        var actualWins = 0;
        foreach (var row in holdout)
        {
            var actualWin = row.IsWin == true;
            if (actualWin) actualWins++;
            var p = (decimal)Predict(BuildFeaturesFromObservation(row));
            var predictedBuy = p >= threshold;
            if (predictedBuy && actualWin) tp++;
            else if (predictedBuy && !actualWin) fp++;
            else if (!predictedBuy && actualWin) fn++;
        }

        _lastPrecision = tp + fp == 0 ? 0m : decimal.Round(100m * tp / (tp + fp), 2);
        _lastRecall = tp + fn == 0 ? 0m : decimal.Round(100m * tp / (tp + fn), 2);
        _lastBaselineWinRate = holdout.Count == 0 ? 0m : decimal.Round(100m * actualWins / holdout.Count, 2);
    }

    private decimal ComputeHeuristicProbability(TechnicalMarketSnapshot snapshot, MarketTicker ticker, StrategyType strategy)
    {
        var trend = NormEmaGapPct(snapshot);
        var momentum = decimal.Clamp((snapshot.Rsi14 - 45m) / 25m, -1m, 1m);
        var macd = decimal.Clamp(snapshot.MacdHistogram * 20m, -1m, 1m);
        var volume = decimal.Clamp((snapshot.RelativeVolume - 0.8m) / 1.5m, -1m, 1m);
        var volPenalty = decimal.Clamp(Math.Abs(ticker.PriceChangePercent24h) / 20m, 0m, 1m);
        var strategyBias = strategy == StrategyType.Momentum ? 0.05m : 0m;
        var score = 0.50m + (0.14m * trend) + (0.14m * momentum) + (0.10m * macd) + (0.08m * volume) - (0.08m * volPenalty) + strategyBias;
        return decimal.Clamp(decimal.Round(score, 4), 0.05m, 0.95m);
    }

    private double Predict(double[] features)
    {
        lock (_modelLock)
        {
            if (_weights is null)
            {
                return 0.5d;
            }
            return Sigmoid(Dot(_weights, features));
        }
    }

    private static double[] BuildFeatures(TechnicalMarketSnapshot snapshot, MarketTicker ticker, StrategyType strategy) =>
    [
        1d,
        (double)NormEmaGapPct(snapshot),
        (double)decimal.Clamp((snapshot.Rsi14 - 50m) / 30m, -1m, 1m),
        (double)decimal.Clamp(snapshot.MacdHistogram * 25m, -1m, 1m),
        (double)decimal.Clamp((snapshot.RelativeVolume - 1m) / 2m, -1m, 1m),
        (double)decimal.Clamp(ticker.PriceChangePercent24h / 20m, -1m, 1m),
        strategy == StrategyType.Momentum ? 1d : strategy == StrategyType.MeanReversion ? -1d : 0d
    ];

    private static double[] BuildFeaturesFromObservation(MlTradeObservation row) =>
    [
        1d,
        (double)decimal.Clamp(row.EmaGapPct / 2m, -1m, 1m),
        (double)decimal.Clamp((row.Rsi14 - 50m) / 30m, -1m, 1m),
        (double)decimal.Clamp(row.MacdHistogram * 25m, -1m, 1m),
        (double)decimal.Clamp((row.RelativeVolume - 1m) / 2m, -1m, 1m),
        (double)decimal.Clamp(row.PriceChangePercent24h / 20m, -1m, 1m),
        row.StrategyType == StrategyType.Momentum ? 1d : row.StrategyType == StrategyType.MeanReversion ? -1d : 0d
    ];

    private static decimal NormEmaGapPct(TechnicalMarketSnapshot snapshot)
    {
        if (snapshot.LastPrice <= 0m)
        {
            return 0m;
        }
        var gap = ((snapshot.EmaFast - snapshot.EmaSlow) / snapshot.LastPrice) * 100m;
        return decimal.Clamp(gap / 2m, -1m, 1m);
    }

    private static double Dot(IReadOnlyList<double> w, IReadOnlyList<double> x)
    {
        var sum = 0d;
        for (var i = 0; i < w.Count; i++)
        {
            sum += w[i] * x[i];
        }
        return sum;
    }

    private static double Sigmoid(double z)
    {
        if (z >= 0d)
        {
            var ez = Math.Exp(-z);
            return 1d / (1d + ez);
        }
        var enz = Math.Exp(z);
        return enz / (1d + enz);
    }
}
