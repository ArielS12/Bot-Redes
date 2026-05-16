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
        decimal positionQty;
        decimal entryPrice;
        decimal peakPrice;
        DateTime? openedAt;
        positionQty = 0m;
        entryPrice = 0m;
        peakPrice = 0m;
        openedAt = null;

        var equity = 0m;
        var peakEquity = 0m;
        var maxDd = 0m;
        var processed = 0;

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
                var trailingArmed = pnlPct >= request.TrailingActivationPercent;
                var trailingHit = trailingArmed && bar.Close <= peakPrice * (1m - request.TrailingStopPercent / 100m);
                var tpHit = pnlPct >= request.TakeProfitPercent;
                var slHit = pnlPct <= -request.StopLossPercent;
                var timeHit = openedAt is not null && request.MaxHoldingMinutes > 0 &&
                                bar.OpenTimeUtc >= openedAt.Value.AddMinutes(request.MaxHoldingMinutes);
                var signalSell = signals.ShouldSell(snap1);
                var profitable = bar.Close > entryPrice;

                string? exitReason = null;
                if (slHit)
                {
                    exitReason = "stop_loss";
                }
                else if (timeHit)
                {
                    exitReason = "time_stop";
                }
                else if (trailingHit && profitable)
                {
                    exitReason = "trailing";
                }
                else if (tpHit && profitable)
                {
                    exitReason = "take_profit";
                }
                else if (signalSell && profitable)
                {
                    exitReason = "signal";
                }

                if (exitReason is not null)
                {
                    var pnl = decimal.Round((bar.Close - entryPrice) * positionQty, 4);
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
            var pnl = decimal.Round((last.Close - entryPrice) * positionQty, 4);
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
        var tier = closed >= 200 && pf > 1.2m && expectancy > 0m ? "VERDE" :
            closed >= 100 && pf >= 1.0m ? "AMARILLO" : "ROJO";
        var tierReason = tier switch
        {
            "VERDE" => "Backtest: muestra alta, PF>1.2, expectancy positiva.",
            "AMARILLO" => "Backtest: muestra intermedia o PF>=1.",
            _ => "Backtest: muestra insuficiente o edge no confirmado."
        };

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
}
