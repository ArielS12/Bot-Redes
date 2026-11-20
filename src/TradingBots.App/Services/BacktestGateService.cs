using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
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
/// Gate Live HTF: BTC+ETH 45d. Pasa si ambos VERDE, o agregado PF&gt;=1.05 con n&gt;=20 y neto&gt;0.
/// Al pasar, asegura MaxAutoBots=2 y baja GlobalMaxDailyLoss a 3 USDT.
/// El FAIL ya no pone MaxAutoBots=0: la creacion de bots la gobierna la señal BUY del advisor.
/// Se reevalua periodicamente (diagnostico / semaforo).
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-45);
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
                QuotePerTradeUsdt = StrategyExitProfiles.HtfQuotePerTradeUsdt
            }, ct);
            batch.Add(result);
            logger.LogInformation(
                "Gate HTF {Symbol}: PF={Pf:0.00} trades={Trades} net={Net:0.00} tier={Tier}",
                symbol,
                result.ProfitFactor,
                result.ClosedTrades,
                result.NetPnlUsdt,
                result.CohortTier);
        }

        var bothVerde = batch.Count == GateSymbols.Length && batch.All(x => x.CohortTier == "VERDE");
        var combinedTrades = batch.Sum(x => x.ClosedTrades);
        var combinedNet = batch.Sum(x => x.NetPnlUsdt);
        var combinedWins = batch.Sum(x => x.WinningTrades);
        var aggregateOk = combinedTrades >= 20 &&
                          combinedNet > 0m &&
                          batch.All(x => x.ProfitFactor >= 1.05m && x.AvgTradePnlUsdt > 0m && x.ClosedTrades >= 6);
        // Sin soft-pass: si el edge es marginal, mejor halt que seguir sangrando.
        var allPass = bothVerde || aggregateOk;

        var summary = allPass
            ? $"Gate HTF OK (45d): reopen MaxAutoBots={StrategyExitProfiles.SafeLiveMaxAutoBots}. " +
              string.Join(" | ", batch.Select(x => $"{x.Symbol} PF={x.ProfitFactor:0.00} n={x.ClosedTrades}"))
            : string.Join(" | ", batch.Select(x =>
                $"{x.Symbol} PF={x.ProfitFactor:0.00} n={x.ClosedTrades} net={x.NetPnlUsdt:0.00} ({x.CohortTier})"));

        lock (_lock)
        {
            _results = batch;
            _liveReady = allPass;
            _summary = summary;
            _evaluatedAtUtc = DateTime.UtcNow;
        }

        await ApplyLiveFleetPolicyAsync(db, allPass, ct);
        logger.LogInformation(
            "Gate HTF evaluado: ready={Ready} combinedN={N} combinedNet={Net:0.00}. {Summary}",
            allPass, combinedTrades, combinedNet, summary);
        _ = combinedWins;
    }

    private async Task ApplyLiveFleetPolicyAsync(AppDbContext db, bool gatePass, CancellationToken ct)
    {
        var row = await db.BinanceSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null)
        {
            return;
        }

        if (gatePass)
        {
            // Reapertura controlada: max 2 bots, perdida diaria global 3 USDT.
            if (row.MaxAutoBots <= 0 || row.MaxAutoBots > StrategyExitProfiles.SafeLiveMaxAutoBots)
            {
                row.MaxAutoBots = StrategyExitProfiles.SafeLiveMaxAutoBots;
            }

            if (row.GlobalMaxDailyLossUsdt <= 0m || row.GlobalMaxDailyLossUsdt > 3m)
            {
                row.GlobalMaxDailyLossUsdt = 3m;
            }

            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Gate HTF PASS: MaxAutoBots={Max}, GlobalMaxDailyLoss={Loss}",
                row.MaxAutoBots, row.GlobalMaxDailyLossUsdt);
            return;
        }

        // Gate FAIL: informativo. No cortar MaxAutoBots; AutoTrader crea solo con señal BUY (verde).
        logger.LogWarning(
            "Gate HTF FAIL (informativo): MaxAutoBots={Max} se mantiene; creacion condicionada a BUY del advisor.",
            row.MaxAutoBots);
    }
}

public sealed class BacktestGateHostedService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReevaluationInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var gate = scope.ServiceProvider.GetRequiredService<IBacktestGateService>();
                await gate.EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                using var scope = scopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<BacktestGateHostedService>>();
                logger.LogWarning(ex, "Gate HTF fallo; reintento en {Hours}h.", ReevaluationInterval.TotalHours);
            }

            try
            {
                await Task.Delay(ReevaluationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
