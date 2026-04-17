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
}

public sealed class MlBuyEvaluation
{
    public decimal WinProbability { get; set; }
    public bool Trained { get; set; }
    public int Samples { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class TradeMlService(AppDbContext dbContext) : ITradeMlService
{
    private DateTime? _lastTrainedUtc;
    private double[]? _weights;
    private int _trainedSamples;
    private readonly object _modelLock = new();

    public async Task<MlBuyEvaluation> EvaluateBuyAsync(
        string symbol,
        StrategyType strategy,
        TechnicalMarketSnapshot snapshot,
        MarketTicker ticker,
        int minSamples,
        CancellationToken ct = default)
    {
        var closed = await dbContext.MlTradeObservations
            .Where(x => x.ClosedAtUtc != null && x.IsWin != null)
            .OrderByDescending(x => x.ClosedAtUtc)
            .Take(3000)
            .ToListAsync(ct);

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
            Note = $"Modelo logistic online ({closed.Count} muestras)."
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
        dbContext.MlTradeObservations.Add(new MlTradeObservation
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
            QuoteVolume24h = decimal.Round(ticker.QuoteVolume24h, 2)
        });
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task RecordExitAsync(Guid botId, string symbol, decimal realizedPnlUsdt, CancellationToken ct = default)
    {
        var open = await dbContext.MlTradeObservations
            .Where(x => x.BotId == botId && x.Symbol == symbol && x.ClosedAtUtc == null)
            .OrderByDescending(x => x.EntryAtUtc)
            .FirstOrDefaultAsync(ct);
        if (open is null)
        {
            return;
        }

        open.ClosedAtUtc = DateTime.UtcNow;
        open.RealizedPnlUsdt = decimal.Round(realizedPnlUsdt, 4);
        open.IsWin = realizedPnlUsdt > 0m;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<MlRuntimeSummary> GetSummaryAsync(BinanceConnectionSettings settings, CancellationToken ct = default)
    {
        var total = await dbContext.MlTradeObservations.CountAsync(ct);
        var closed = await dbContext.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin != null, ct);
        var wins = await dbContext.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin == true, ct);
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
    }

    public async Task<MlDiagnosticsView> GetDiagnosticsAsync(BinanceConnectionSettings settings, CancellationToken ct = default)
    {
        var minSamples = settings.MlMinSamples <= 0 ? 80 : settings.MlMinSamples;
        var total = await dbContext.MlTradeObservations.CountAsync(ct);
        var closedCount = await dbContext.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin != null, ct);
        var wins = await dbContext.MlTradeObservations.CountAsync(x => x.ClosedAtUtc != null && x.IsWin == true, ct);
        var winRate = closedCount == 0 ? 0m : decimal.Round((wins * 100m) / closedCount, 2);

        var closedRows = await dbContext.MlTradeObservations
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
            EnsureTrained(closedRows);
            ran = true;
            note = $"Entrenamiento logistic ejecutado en esta peticion sobre {closedRows.Count} filas (tope 3000), igual que en EvaluateBuyAsync.";
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
            TrainingRanThisRequest = ran,
            ModelReady = _weights is not null,
            ClosedRowsUsedForTraining = ran ? closedRows.Count : 0,
            LastTrainedUtc = _lastTrainedUtc,
            Note = note
        };
    }

    private void EnsureTrained(List<MlTradeObservation> rows)
    {
        lock (_modelLock)
        {
            if (_weights is not null && _trainedSamples == rows.Count && _lastTrainedUtc is not null &&
                (DateTime.UtcNow - _lastTrainedUtc.Value) < TimeSpan.FromMinutes(30))
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
            _lastTrainedUtc = DateTime.UtcNow;
        }
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
        strategy == StrategyType.Momentum ? 1d : 0d
    ];

    private static double[] BuildFeaturesFromObservation(MlTradeObservation row) =>
    [
        1d,
        (double)decimal.Clamp(row.EmaGapPct / 2m, -1m, 1m),
        (double)decimal.Clamp((row.Rsi14 - 50m) / 30m, -1m, 1m),
        (double)decimal.Clamp(row.MacdHistogram * 25m, -1m, 1m),
        (double)decimal.Clamp((row.RelativeVolume - 1m) / 2m, -1m, 1m),
        (double)decimal.Clamp(row.PriceChangePercent24h / 20m, -1m, 1m),
        row.StrategyType == StrategyType.Momentum ? 1d : 0d
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
