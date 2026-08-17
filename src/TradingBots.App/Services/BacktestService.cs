using TradingBots.App.Models;
using TradingBots.App.Services.Strategies;

namespace TradingBots.App.Services;

public interface IBacktestService
{
    Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken ct = default);
}

public sealed class BacktestService(
    IBinanceMarketService marketService,
    IStrategySignalRegistry strategySignals,
    IMarketHistoryService marketHistory) : IBacktestService
{
    private const int WarmupBars = 200;
    private const int SnapshotWindow = 150;
    private const decimal RoundTripCostBps = 20m;
    private const decimal MinNetProfitToExitPercent = 2.0m;
    private const decimal SoftBreakevenExitPercent = 0.25m;
    private const decimal SoftBreakevenArmPercentFallback = 1.5m;
    private const int EarlyInvalidationMinutes = 180;
    private const decimal EarlyInvalidationMinLossPercent = -0.25m;
    private const int MaxZombieHoldingMinutes = 480;

    public async Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken ct = default)
    {
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var from = DateTime.SpecifyKind(request.FromUtc, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(request.ToUtc, DateTimeKind.Utc);
        if (to <= from)
        {
            to = from.AddDays(7);
        }

        var fetchFrom = from.AddMinutes(-WarmupBars * 5);
        var bars1m = await marketService.FetchKlinesRangeAsync(symbol, "1m", fetchFrom, to, ct);
        if (bars1m.Count < WarmupBars + 10)
        {
            return new BacktestResult
            {
                Symbol = symbol,
                Strategy = request.Strategy,
                FromUtc = from,
                ToUtc = to,
                CohortReason = $"Datos insuficientes ({bars1m.Count} velas 1m)."
            };
        }

        var bars5m = CandleAggregation.Resample(bars1m, 5);
        var bars15m = CandleAggregation.Resample(bars1m, 15);
        var regimes = await marketHistory.GetRegimesAsync([symbol], ct);
        regimes.TryGetValue(symbol, out var regime);
        var signals = strategySignals.Get(request.Strategy);

        var trades = new List<BacktestTradeRecord>();
        decimal positionQty = 0m;
        decimal entryPrice = 0m;
        decimal peakPrice = 0m;
        DateTime? openedAt = null;

        var equity = 0m;
        var peakEquity = 0m;
        var maxDd = 0m;
        var processed = 0;
        var trailArm = request.TrailingActivationPercent > 0m
            ? request.TrailingActivationPercent
            : SoftBreakevenArmPercentFallback;

        for (var i = WarmupBars; i < bars1m.Count; i++)
        {
            var bar = bars1m[i];
            if (bar.OpenTimeUtc < from)
            {
                continue;
            }

            processed++;
            var win1m = CandleAggregation.SliceUpTo(bars1m, bar.OpenTimeUtc, SnapshotWindow);
            var win5m = CandleAggregation.SliceUpTo(bars5m, bar.OpenTimeUtc, SnapshotWindow);
            var win15m = CandleAggregation.SliceUpTo(bars15m, bar.OpenTimeUtc, SnapshotWindow);
            var snap1 = TechnicalSnapshotBuilder.FromBars(win1m, symbol, "1m");
            var snap5 = TechnicalSnapshotBuilder.FromBars(win5m, symbol, "5m");
            var snap15 = TechnicalSnapshotBuilder.FromBars(win15m, symbol, "15m");
            if (snap1 is null || snap5 is null || snap15 is null)
            {
                continue;
            }

            var ticker = new MarketTicker
            {
                Symbol = symbol,
                LastPrice = bar.Close,
                QuoteVolume24h = 1_000_000m,
                PriceChangePercent24h = 0m
            };

            if (positionQty > 0m)
            {
                peakPrice = Math.Max(peakPrice, bar.High);
                var pnlPct = entryPrice > 0m ? ((bar.Close - entryPrice) / entryPrice) * 100m : 0m;
                var mfePct = entryPrice > 0m ? ((peakPrice - entryPrice) / entryPrice) * 100m : 0m;
                var holdingMinutes = openedAt is null
                    ? 0
                    : (int)Math.Max(0, (bar.OpenTimeUtc - openedAt.Value).TotalMinutes);
                var trailingArmed = pnlPct >= Math.Max(trailArm, MinNetProfitToExitPercent);
                var trailingHit = trailingArmed && bar.Close <= peakPrice * (1m - request.TrailingStopPercent / 100m);
                var tpHit = pnlPct >= request.TakeProfitPercent;
                var slHit = pnlPct <= -request.StopLossPercent;
                var bounceInvalidated = signals.ShouldSell(snap1, snap5) &&
                                        (mfePct < MinNetProfitToExitPercent || pnlPct < 0m);
                var softBeHit = mfePct >= trailArm && pnlPct <= SoftBreakevenExitPercent;
                var earlyInvalidation = holdingMinutes >= EarlyInvalidationMinutes &&
                                        mfePct < MinNetProfitToExitPercent &&
                                        pnlPct <= EarlyInvalidationMinLossPercent;
                var zombieRed = holdingMinutes >= MaxZombieHoldingMinutes && pnlPct <= 0m;

                string? exitReason = null;
                if (slHit)
                {
                    exitReason = "stop_loss";
                }
                else if (earlyInvalidation)
                {
                    exitReason = "early_invalidation";
                }
                else if (bounceInvalidated)
                {
                    exitReason = "bounce_invalidation";
                }
                else if (softBeHit)
                {
                    exitReason = "soft_breakeven";
                }
                else if (zombieRed)
                {
                    exitReason = "time_stop";
                }
                else if (trailingHit)
                {
                    exitReason = "trailing";
                }
                else if (tpHit)
                {
                    exitReason = "take_profit";
                }

                if (exitReason is not null)
                {
                    var pnl = ComputeNetPnl(entryPrice, bar.Close, positionQty);
                    trades.Add(new BacktestTradeRecord
                    {
                        EntryUtc = openedAt ?? bar.OpenTimeUtc,
                        ExitUtc = bar.OpenTimeUtc,
                        EntryPrice = entryPrice,
                        ExitPrice = bar.Close,
                        Quantity = positionQty,
                        RealizedPnlUsdt = pnl,
                        ExitReason = exitReason
                    });
                    equity += pnl;
                    peakEquity = Math.Max(peakEquity, equity);
                    maxDd = Math.Max(maxDd, peakEquity - equity);
                    positionQty = 0m;
                    openedAt = null;
                }
            }
            else if (signals.ShouldBuy(snap1) &&
                     signals.PassesMultiTimeframeTrend(snap5, snap15) &&
                     signals.PassesShortRegimeFilter(snap1, ticker) &&
                     signals.PassesLongTermRegime(regime) &&
                     bar.Close > 0m)
            {
                positionQty = decimal.Round(request.QuotePerTradeUsdt / bar.Close, 8);
                entryPrice = bar.Close;
                peakPrice = bar.Close;
                openedAt = bar.OpenTimeUtc;
            }
        }

        if (positionQty > 0m && bars1m.Count > 0)
        {
            var last = bars1m[^1];
            var pnl = ComputeNetPnl(entryPrice, last.Close, positionQty);
            trades.Add(new BacktestTradeRecord
            {
                EntryUtc = openedAt ?? last.OpenTimeUtc,
                ExitUtc = last.OpenTimeUtc,
                EntryPrice = entryPrice,
                ExitPrice = last.Close,
                Quantity = positionQty,
                RealizedPnlUsdt = pnl,
                ExitReason = "end_of_range"
            });
            equity += pnl;
        }

        var wins = trades.Where(x => x.RealizedPnlUsdt > 0m).ToList();
        var losses = trades.Where(x => x.RealizedPnlUsdt < 0m).ToList();
        var sumWins = wins.Sum(x => x.RealizedPnlUsdt);
        var sumLossAbs = Math.Abs(losses.Sum(x => x.RealizedPnlUsdt));
        var pf = sumLossAbs <= 0m ? (sumWins > 0m ? 999m : 0m) : sumWins / sumLossAbs;
        var closed = trades.Count;
        var winRate = closed == 0 ? 0m : decimal.Round((wins.Count * 100m) / closed, 2);
        var expectancy = closed == 0 ? 0m : equity / closed;
        var liveGatePass = closed >= 30 && pf >= 1.15m && expectancy > 0m;
        var tier = liveGatePass ? "VERDE" :
            closed >= 100 && pf >= 1.0m ? "AMARILLO" : "ROJO";
        var tierReason = liveGatePass
            ? "Backtest: PF>=1.15, >=30 SELL, expectancy positiva (gate Live OK)."
            : closed >= 100 && pf >= 1.0m
                ? "Backtest: muestra intermedia o PF>=1, por debajo del gate Live 1.15."
                : "Backtest: muestra insuficiente o edge no confirmado (Live no debe comprar).";

        return new BacktestResult
        {
            Symbol = symbol,
            Strategy = request.Strategy,
            FromUtc = from,
            ToUtc = to,
            BarsProcessed = processed,
            ClosedTrades = closed,
            WinningTrades = wins.Count,
            WinRatePercent = winRate,
            NetPnlUsdt = decimal.Round(equity, 4),
            ProfitFactor = decimal.Round(pf, 4),
            MaxDrawdownUsdt = decimal.Round(maxDd, 4),
            AvgTradePnlUsdt = decimal.Round(expectancy, 4),
            CohortTier = tier,
            CohortReason = tierReason,
            Trades = trades
        };
    }

    private static decimal ComputeNetPnl(decimal entryPrice, decimal exitPrice, decimal quantity)
    {
        var gross = (exitPrice - entryPrice) * quantity;
        var fee = (entryPrice + exitPrice) * quantity * (RoundTripCostBps / 20_000m);
        return decimal.Round(gross - fee, 4);
    }
}
