using TradingBots.App.Models;
using TradingBots.App.Services.Strategies;

namespace TradingBots.App.Services;

public interface IBacktestGateService
{
    bool IsLiveReady { get; }
    string Summary { get; }
    DateTime? EvaluatedAtUtc { get; }
    IReadOnlyList<BacktestResult> SymbolResults { get; }
    Task EvaluateAsync(CancellationToken ct = default);
}

/// <summary>
/// Gate Live: BTC+ETH Pullback HTF 30d deben pasar PF&gt;=1.15 con muestra &gt;=30 SELL.
/// </summary>
public sealed class BacktestGateService(
    IServiceScopeFactory scopeFactory,
    ILogger<BacktestGateService> logger) : IBacktestGateService
{
    private static readonly string[] GateSymbols = ["BTCUSDT", "ETHUSDT"];
    private readonly object _lock = new();
    private bool _liveReady;
    private string _summary = "Gate HTF pendiente de evaluacion.";
    private DateTime? _evaluatedAtUtc;
    private List<BacktestResult> _results = [];

    public bool IsLiveReady
    {
        get { lock (_lock) return _liveReady; }
    }

    public string Summary
    {
        get { lock (_lock) return _summary; }
    }

    public DateTime? EvaluatedAtUtc
    {
        get { lock (_lock) return _evaluatedAtUtc; }
    }

    public IReadOnlyList<BacktestResult> SymbolResults
    {
        get { lock (_lock) return _results.ToList(); }
    }

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var backtest = scope.ServiceProvider.GetRequiredService<IBacktestService>();

        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-30);
        var htfParams = StrategyExitProfiles.AutoPilotParams(StrategyType.PullbackHtf);
        var batch = new List<BacktestResult>();

        foreach (var symbol in GateSymbols)
        {
            ct.ThrowIfCancellationRequested();
            var result = await backtest.RunAsync(new BacktestRequest
            {
                Symbol = symbol,
                Strategy = StrategyType.PullbackHtf,
                FromUtc = from,
                ToUtc = to,
                StopLossPercent = htfParams.Sl,
                TakeProfitPercent = htfParams.Tp,
                TrailingActivationPercent = htfParams.TrailAct,
                TrailingStopPercent = htfParams.TrailStop,
                MaxHoldingMinutes = htfParams.MaxHold,
                QuotePerTradeUsdt = 20m
            }, ct);
            batch.Add(result);
            logger.LogInformation(
                "Gate HTF {Symbol}: PF={Pf:0.00} trades={Trades} tier={Tier}",
                symbol,
                result.ProfitFactor,
                result.ClosedTrades,
                result.CohortTier);
        }

        var allPass = batch.Count == GateSymbols.Length &&
                      batch.All(x => x.CohortTier == "VERDE");
        var summary = allPass
            ? "Gate HTF OK: BTC+ETH pasan PF>=1.15 (30d). Se puede subir MaxAutoBots."
            : string.Join(" | ", batch.Select(x =>
                $"{x.Symbol} PF={x.ProfitFactor:0.00} n={x.ClosedTrades} ({x.CohortTier})"));

        lock (_lock)
        {
            _results = batch;
            _liveReady = allPass;
            _summary = summary;
            _evaluatedAtUtc = DateTime.UtcNow;
        }

        logger.LogInformation("Gate HTF evaluado: ready={Ready}. {Summary}", allPass, summary);
    }
}

public sealed class BacktestGateHostedService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var gate = scope.ServiceProvider.GetRequiredService<IBacktestGateService>();
            await gate.EvaluateAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            using var scope = scopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<BacktestGateHostedService>>();
            logger.LogWarning(ex, "Gate HTF en startup fallo; Live sigue en halt hasta re-evaluacion.");
        }
    }
}
