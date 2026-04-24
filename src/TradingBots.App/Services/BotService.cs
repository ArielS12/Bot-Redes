using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBotService
{
    Task<IReadOnlyCollection<TradingBot>> GetBotsAsync();
    Task<PagedBotsResponse> GetBotsPageAsync(int page, int pageSize);
    Task<TradingBot?> GetBotAsync(Guid id);
    Task<TradingBot> CreateBotAsync(CreateOrUpdateBotRequest request);
    Task<TradingBot?> UpdateBotAsync(Guid id, CreateOrUpdateBotRequest request);
    Task<bool> SetBotStateAsync(Guid id, BotState state);
    Task<bool> SetAutoResumeBlockedAsync(Guid id, bool blocked);
    Task<ForceSellResponse> ForceSellAsync(Guid id);
    Task TickBotsAsync(IReadOnlyDictionary<string, MarketTicker> marketSnapshot);
    Task<IReadOnlyCollection<BotSignalDiagnosticsItem>> GetSignalDiagnosticsAsync(IEnumerable<Guid>? botIds = null);
}

public sealed class BotService(
    AppDbContext dbContext,
    IBinanceMarketService marketService,
    IBinanceTradeExecutionService tradeExecutionService,
    IBinanceSettingsService settingsService,
    ITradeMlService tradeMlService,
    ILogger<BotService> logger) : IBotService
{
    /// <summary>Mínimo de notional por orden de compra (coherente con filtros típicos MIN_NOTIONAL en Binance).</summary>
    private const decimal MinQuoteOrderUsdt = 10m;
    private const decimal MinQuoteVolume24hUsdt = 750_000m;
    private const decimal MinRelativeVolume = 0.7m;
    /// <summary>Pullback: no entrar si el dia ya se movio demasiado (evita perseguir extremos).</summary>
    private const decimal PullbackMaxAbsChange24hPercent = 12m;
    /// <summary>Pullback: separacion EMA como proxy de riesgo en entrada; Momentum la ignora (tendencia fuerte = gap amplio).</summary>
    private const decimal PullbackMaxEmaSpreadPercentOfPrice = 2.5m;
    private const decimal MaxAtrPercentForEntry = 2.8m;
    private const decimal MaxVolatilityPercentForEntry = 1.4m;
    private const decimal MinTrendSpreadPercentForEntry = 0.03m;
    private const decimal BaseRiskPercentPerTrade = 0.50m;
    private const int ExecutionFailureCircuitThreshold = 3;
    private static readonly TimeSpan ExecutionFailureCircuitDuration = TimeSpan.FromMinutes(20);
    private const int MinClosedTradesForAdaptive = 100;
    private static readonly TimeSpan RiskAdjustmentCooldown = TimeSpan.FromHours(6);
    private static readonly TimeSpan AutoScaleCooldown = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<Guid, int> BotExecutionFailures = new();
    private static readonly ConcurrentDictionary<string, DateTime> SymbolCircuitOpenUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyCollection<TradingBot>> GetBotsAsync() =>
        await dbContext.Bots
            .OrderByDescending(x => x.State)
            .ThenBy(x => x.Name)
            .ToListAsync();

    public async Task<PagedBotsResponse> GetBotsPageAsync(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var ordered = dbContext.Bots
            .OrderByDescending(x => x.State)
            .ThenBy(x => x.Name);
        var total = await ordered.CountAsync();
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedBotsResponse
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<TradingBot?> GetBotAsync(Guid id) =>
        await dbContext.Bots.FirstOrDefaultAsync(x => x.Id == id);

    public async Task<TradingBot> CreateBotAsync(CreateOrUpdateBotRequest request)
    {
        var bot = new TradingBot();
        ApplyRequest(bot, request);
        dbContext.Bots.Add(bot);
        await dbContext.SaveChangesAsync();
        return bot;
    }

    public async Task<TradingBot?> UpdateBotAsync(Guid id, CreateOrUpdateBotRequest request)
    {
        var bot = await dbContext.Bots.FirstOrDefaultAsync(x => x.Id == id);
        if (bot is null)
        {
            return null;
        }

        ApplyRequest(bot, request);
        await dbContext.SaveChangesAsync();
        return bot;
    }

    public async Task<bool> SetAutoResumeBlockedAsync(Guid id, bool blocked)
    {
        var bot = await dbContext.Bots.FirstOrDefaultAsync(x => x.Id == id);
        if (bot is null)
        {
            return false;
        }

        bot.AutoResumeBlocked = blocked;
        if (blocked && bot.State == BotState.Running)
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Usuario: bloqueo AutoPilot (no reactivar hasta desbloquear).";
            bot.UpdatedAtUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetBotStateAsync(Guid id, BotState state)
    {
        var bot = await dbContext.Bots.FirstOrDefaultAsync(x => x.Id == id);
        if (bot is null)
        {
            return false;
        }

        bot.State = state;
        bot.UpdatedAtUtc = DateTime.UtcNow;
        if (state == BotState.Running)
        {
            bot.LastRunningStartedAtUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync();

        return true;
    }

    public async Task<ForceSellResponse> ForceSellAsync(Guid id)
    {
        var bot = await dbContext.Bots.FirstOrDefaultAsync(x => x.Id == id);
        if (bot is null)
        {
            return new ForceSellResponse { Outcome = "not_found", Message = "Bot no encontrado." };
        }

        if (bot.PositionQuantity <= 0m)
        {
            return new ForceSellResponse { Outcome = "invalid", Message = "El bot no tiene posicion abierta." };
        }

        var symbol = !string.IsNullOrWhiteSpace(bot.PositionSymbol)
            ? bot.PositionSymbol
            : bot.Symbols.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return new ForceSellResponse { Outcome = "invalid", Message = "No hay simbolo asociado a la posicion." };
        }

        var qtyToSell = bot.PositionQuantity;
        var settings = await settingsService.GetActiveSettingsAsync();
        var mlEnabled = settings.MlEnabled;
        var realTradingEnabled = await tradeExecutionService.IsRealTradingEnabledAsync();

        TradeFillResult? fill;
        if (realTradingEnabled)
        {
            fill = await tradeExecutionService.MarketSellAsync(symbol, qtyToSell, bot.Id);
        }
        else
        {
            var market = await marketService.GetMarketOverviewAsync(new[] { symbol });
            var ticker = market.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (ticker is null || ticker.LastPrice <= 0m)
            {
                return new ForceSellResponse
                {
                    Outcome = "invalid",
                    Message = "No hay precio de mercado para simular la venta (paper)."
                };
            }

            fill = new TradeFillResult
            {
                ExecutedQuantity = qtyToSell,
                AveragePrice = ticker.LastPrice
            };
            dbContext.OrderAuditEvents.Add(new OrderAuditEvent
            {
                BotId = bot.Id,
                Symbol = symbol,
                Side = "SELL",
                Stage = "execution",
                Status = "simulated",
                Message = "Paper: forzar venta manual (cierre total).",
                RequestedQuoteQty = 0m,
                RequestedBaseQty = qtyToSell,
                ExecutedQty = fill.ExecutedQuantity,
                ExecutedPrice = fill.AveragePrice,
                LatencyMs = 0,
                IsLive = false,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (fill is null || fill.ExecutedQuantity <= 0m || fill.AveragePrice <= 0m)
        {
            var err = realTradingEnabled ? tradeExecutionService.GetLastExecutionError() : string.Empty;
            return new ForceSellResponse
            {
                Outcome = "invalid",
                Message = string.IsNullOrWhiteSpace(err) ? "No se pudo ejecutar la venta." : err
            };
        }

        var entryPx = bot.AverageEntryPrice > 0m ? bot.AverageEntryPrice : fill.AveragePrice;
        var realized = decimal.Round((fill.AveragePrice - entryPx) * fill.ExecutedQuantity, 2);
        bot.RealizedPnlUsdt += realized;
        bot.ConsecutiveLossTrades = realized < 0m ? bot.ConsecutiveLossTrades + 1 : 0;
        if (realized < 0m && bot.CooldownMinutesAfterLoss > 0)
        {
            bot.CooldownSymbol = symbol;
            bot.CooldownUntilUtc = DateTime.UtcNow.AddMinutes(bot.CooldownMinutesAfterLoss);
        }
        else if (realized >= 0m)
        {
            bot.CooldownSymbol = string.Empty;
            bot.CooldownUntilUtc = null;
        }

        bot.PositionQuantity = 0m;
        bot.UnrealizedPnlUsdt = 0m;
        bot.AverageEntryPrice = 0m;
        bot.PositionSymbol = string.Empty;
        bot.PositionOpenedAtUtc = null;
        bot.PeakPriceSinceEntry = 0m;
        bot.TakeProfit1Taken = false;
        bot.LastExecutionError = string.Empty;
        bot.UpdatedAtUtc = DateTime.UtcNow;

        dbContext.Trades.Add(new TradeExecution
        {
            BotId = bot.Id,
            Symbol = symbol,
            Side = "SELL",
            Price = fill.AveragePrice,
            Quantity = fill.ExecutedQuantity,
            RealizedPnlUsdt = realized,
            ExecutedAtUtc = DateTime.UtcNow
        });

        if (mlEnabled)
        {
            bot.MlRoundTripRealizedUsdt += realized;
            await tradeMlService.RecordExitAsync(bot.Id, symbol, bot.MlRoundTripRealizedUsdt);
            bot.MlRoundTripRealizedUsdt = 0m;
        }

        if (bot.ConsecutiveLossTrades >= Math.Max(1, bot.MaxConsecutiveLossTrades))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Bot pausado por racha de perdidas consecutivas (AutoPilot).";
        }

        if (bot.RealizedPnlUsdt <= -Math.Abs(bot.MaxDailyLossUsdt))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Bot pausado por perdida diaria maxima (AutoPilot).";
        }

        await dbContext.SaveChangesAsync();
        logger.LogInformation(
            "Forzar venta ejecutada: bot {BotId} {BotName} {Symbol} qty {Qty} @ {Price}",
            bot.Id,
            bot.Name,
            symbol,
            fill.ExecutedQuantity,
            fill.AveragePrice);

        return new ForceSellResponse
        {
            Outcome = "ok",
            Message = "Venta forzada ejecutada (cierre total a mercado).",
            QuantitySold = fill.ExecutedQuantity,
            AveragePrice = fill.AveragePrice
        };
    }

    public async Task TickBotsAsync(IReadOnlyDictionary<string, MarketTicker> marketSnapshot)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var mlEnabled = settings.MlEnabled;
        var mlShadowMode = settings.MlShadowMode;
        var mlMinProb = settings.MlMinWinProbability <= 0m ? 0.55m : settings.MlMinWinProbability;
        var mlMinSamples = settings.MlMinSamples <= 0 ? 80 : settings.MlMinSamples;
        var bots = await dbContext.Bots.Where(x => x.State == BotState.Running).ToListAsync();
        var botIds = bots.Select(x => x.Id).ToList();
        var recentSellTrades = await dbContext.Trades
            .Where(x => botIds.Contains(x.BotId) && x.Side == "SELL")
            .OrderByDescending(x => x.ExecutedAtUtc)
            .ToListAsync();
        var realTradingEnabled = await tradeExecutionService.IsRealTradingEnabledAsync();
        var symbols = bots.SelectMany(x => x.Symbols).Distinct().ToList();
        var technicalBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "1m", 150);
        var technical5mBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "5m", 150);
        var technical15mBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "15m", 150);
        foreach (var bot in bots)
        {
            var selected = bot.Symbols
                .Where(marketSnapshot.ContainsKey)
                .Select(symbol => new { Symbol = symbol, Ticker = marketSnapshot[symbol] })
                .Where(x => x.Ticker.LastPrice > 0)
                .ToList();

            if (selected.Count == 0)
            {
                continue;
            }

            var activeSymbol = bot.PositionQuantity > 0m && !string.IsNullOrWhiteSpace(bot.PositionSymbol) &&
                               marketSnapshot.ContainsKey(bot.PositionSymbol)
                ? bot.PositionSymbol
                : selected[0].Symbol;
            var activeTicker = marketSnapshot[activeSymbol];
            var activePrice = activeTicker.LastPrice;
            if (activePrice <= 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(bot.CooldownSymbol) &&
                bot.CooldownSymbol.Equals(activeSymbol, StringComparison.OrdinalIgnoreCase) &&
                bot.CooldownUntilUtc is not null &&
                DateTime.UtcNow < bot.CooldownUntilUtc.Value)
            {
                bot.UpdatedAtUtc = DateTime.UtcNow;
                continue;
            }

            if (bot.PositionQuantity > 0 && bot.AverageEntryPrice > 0)
            {
                if (bot.PositionOpenedAtUtc is null)
                {
                    bot.PositionOpenedAtUtc = DateTime.UtcNow;
                }
                var unrealized = (activePrice - bot.AverageEntryPrice) * bot.PositionQuantity;
                bot.UnrealizedPnlUsdt = decimal.Round(unrealized, 2);
                bot.PeakPriceSinceEntry = Math.Max(bot.PeakPriceSinceEntry, activePrice);
            }
            else
            {
                bot.UnrealizedPnlUsdt = 0m;
            }

            var buyCandidate = selected
                .Where(x => technicalBySymbol.ContainsKey(x.Symbol))
                .Select(x => new { x.Symbol, Snapshot = technicalBySymbol[x.Symbol] })
                .Where(x =>
                {
                    if (!technical5mBySymbol.TryGetValue(x.Symbol, out var tf5) || !technical15mBySymbol.TryGetValue(x.Symbol, out var tf15))
                    {
                        return false;
                    }

                    return ShouldBuy(bot.StrategyType, x.Snapshot) &&
                           PassesMultiTimeframeTrend(bot.StrategyType, tf5, tf15) &&
                           PassesLiquidityAndVolume(marketSnapshot[x.Symbol], x.Snapshot) &&
                           PassesRegimeFilter(bot.StrategyType, x.Snapshot);
                })
                .OrderByDescending(x => ScoreBuyCandidate(bot.StrategyType, x.Snapshot))
                .FirstOrDefault();
            var buySignal = buyCandidate is not null;
            var activeTechnical = technicalBySymbol.TryGetValue(activeSymbol, out var t) ? t : null;
            var sellSignal = activeTechnical is not null && ShouldSellBySignal(bot.StrategyType, activeTechnical);
            var takeProfitHit = bot.PositionQuantity > 0 && bot.AverageEntryPrice > 0 &&
                                ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m >= bot.TakeProfitPercent;
            var stopLossHit = bot.PositionQuantity > 0 && bot.AverageEntryPrice > 0 &&
                              ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m <= -bot.StopLossPercent;
            var pnlPct = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m
                : 0m;
            var trailingArmed = pnlPct >= bot.TrailingActivationPercent && bot.PeakPriceSinceEntry > 0m;
            var trailingStopHit = trailingArmed &&
                                  activePrice <= bot.PeakPriceSinceEntry * (1m - (bot.TrailingStopPercent / 100m));
            var timeStopHit = bot.PositionOpenedAtUtc is not null &&
                              bot.MaxHoldingMinutes > 0 &&
                              DateTime.UtcNow >= bot.PositionOpenedAtUtc.Value.AddMinutes(bot.MaxHoldingMinutes);
            var profitableNow = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m && activePrice > bot.AverageEntryPrice;
            var investedCapital = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? bot.PositionQuantity * bot.AverageEntryPrice
                : 0m;
            var exposureLimit = bot.BudgetUsdt * (Math.Clamp(bot.MaxExposurePercent, 1m, 100m) / 100m);
            var remainingBudget = Math.Max(0m, exposureLimit - investedCapital);
            var blockPullbackVolatileDay = false;
            var blockPullbackEmaSpread = false;
            if (buyCandidate is not null && bot.StrategyType == StrategyType.Pullback)
            {
                var buyTicker = marketSnapshot[buyCandidate.Symbol];
                var abs24 = Math.Abs(buyTicker.PriceChangePercent24h);
                blockPullbackVolatileDay = abs24 >= PullbackMaxAbsChange24hPercent;
                var buySnap = buyCandidate.Snapshot;
                if (buySnap.LastPrice > 0m)
                {
                    var emaSpreadPct = Math.Abs(buySnap.EmaFast - buySnap.EmaSlow) / buySnap.LastPrice * 100m;
                    blockPullbackEmaSpread = emaSpreadPct > PullbackMaxEmaSpreadPercentOfPrice;
                }
            }

            if (buySignal && !blockPullbackVolatileDay && !blockPullbackEmaSpread)
            {
                var symbol = buyCandidate!.Symbol;
                if (IsSymbolCircuitOpen(symbol))
                {
                    bot.LastExecutionError = $"Circuit breaker activo para {symbol}. Reintento diferido.";
                    continue;
                }
                var entryPrice = buyCandidate.Snapshot.LastPrice;
                var quoteToUse = ComputeRiskSizedQuote(bot, remainingBudget, buyCandidate.Snapshot);
                if (quoteToUse < MinQuoteOrderUsdt)
                {
                    bot.LastExecutionError =
                        $"Señal BUY en {symbol} pero notional {quoteToUse:0.##} USDT < mínimo {MinQuoteOrderUsdt:0.##} (aumenta Budget/Max por trade o revisa ATR/volatilidad en régimen).";
                }

                if (quoteToUse >= MinQuoteOrderUsdt)
                {
                    MlBuyEvaluation? mlEval = null;
                    if (mlEnabled)
                    {
                        mlEval = await tradeMlService.EvaluateBuyAsync(symbol, bot.StrategyType, buyCandidate.Snapshot, marketSnapshot[symbol], mlMinSamples);
                        if (!mlShadowMode && mlEval.Trained && mlEval.WinProbability < mlMinProb)
                        {
                            dbContext.OrderAuditEvents.Add(new OrderAuditEvent
                            {
                                BotId = bot.Id,
                                Symbol = symbol,
                                Side = "BUY",
                                Stage = "ml-filter",
                                Status = "blocked",
                                Message = $"ML bloquea entrada: p(win)={mlEval.WinProbability:0.000} < umbral {mlMinProb:0.000}.",
                                RequestedQuoteQty = quoteToUse,
                                IsLive = realTradingEnabled,
                                CreatedAtUtc = DateTime.UtcNow
                            });
                            bot.LastExecutionError = $"ML filtro: p(win) {mlEval.WinProbability:0.000} < {mlMinProb:0.000}";
                            continue;
                        }
                    }

                    if (realTradingEnabled)
                    {
                        var quoteAsset = ResolveQuoteAsset(symbol);
                        var freeQuoteBalance = await tradeExecutionService.GetQuoteAssetFreeBalanceAsync(quoteAsset);
                        // Usa capital real disponible en cuenta; evita ordenes que no alcanzan balance.
                        quoteToUse = Math.Min(quoteToUse, decimal.Round(freeQuoteBalance * 0.995m, 8, MidpointRounding.ToZero));
                    }

                    if (quoteToUse < MinQuoteOrderUsdt)
                    {
                        continue;
                    }

                    var fill = realTradingEnabled
                        ? await tradeExecutionService.MarketBuyAsync(symbol, quoteToUse, bot.Id)
                        : new TradeFillResult
                        {
                            ExecutedQuantity = decimal.Round(quoteToUse / entryPrice, 8, MidpointRounding.ToZero),
                            AveragePrice = entryPrice
                        };
                    if (!realTradingEnabled)
                    {
                        dbContext.OrderAuditEvents.Add(new OrderAuditEvent
                        {
                            BotId = bot.Id,
                            Symbol = symbol,
                            Side = "BUY",
                            Stage = "execution",
                            Status = "simulated",
                            Message = "Ejecucion paper BUY.",
                            RequestedQuoteQty = quoteToUse,
                            ExecutedQty = fill?.ExecutedQuantity ?? 0m,
                            ExecutedPrice = fill?.AveragePrice ?? 0m,
                            IsLive = false,
                            CreatedAtUtc = DateTime.UtcNow
                        });
                    }

                    if (fill is not null && fill.ExecutedQuantity > 0 && fill.AveragePrice > 0)
                    {
                        ResetExecutionFailure(bot);
                        var positionQtyBeforeBuy = bot.PositionQuantity;
                        var previousCost = bot.PositionQuantity * bot.AverageEntryPrice;
                        var fillCost = fill.ExecutedQuantity * fill.AveragePrice;
                        var newQuantity = bot.PositionQuantity + fill.ExecutedQuantity;
                        bot.PositionQuantity = newQuantity;
                        bot.AverageEntryPrice = newQuantity > 0m
                            ? decimal.Round((previousCost + fillCost) / newQuantity, 8, MidpointRounding.ToZero)
                            : 0m;
                        bot.PositionSymbol = symbol;
                        if (bot.PositionOpenedAtUtc is null)
                        {
                            bot.PositionOpenedAtUtc = DateTime.UtcNow;
                        }
                        bot.PeakPriceSinceEntry = Math.Max(bot.PeakPriceSinceEntry, fill.AveragePrice);
                        bot.TakeProfit1Taken = false;
                        bot.UnrealizedPnlUsdt = 0m;
                        bot.LastExecutionError = string.Empty;
                        dbContext.Trades.Add(new TradeExecution
                        {
                            BotId = bot.Id,
                            Symbol = symbol,
                            Side = "BUY",
                            Price = fill.AveragePrice,
                            Quantity = fill.ExecutedQuantity,
                            RealizedPnlUsdt = 0m,
                            ExecutedAtUtc = DateTime.UtcNow
                        });
                        if (mlEnabled && positionQtyBeforeBuy == 0m)
                        {
                            bot.MlRoundTripRealizedUsdt = 0m;
                            await tradeMlService.RecordEntryAsync(
                                bot.Id,
                                symbol,
                                bot.StrategyType,
                                fill.AveragePrice,
                                buyCandidate.Snapshot,
                                marketSnapshot[symbol],
                                mlEval?.WinProbability ?? 0.5m);
                        }
                    }
                    else if (realTradingEnabled)
                    {
                        bot.LastExecutionError = tradeExecutionService.GetLastExecutionError();
                        RegisterExecutionFailure(bot, symbol);
                    }
                }
            }
            else if (bot.PositionQuantity > 0m)
            {
                // Salidas de riesgo (SL/time-stop) deben poder ejecutarse incluso sin profit.
                var riskExit = stopLossHit || timeStopHit;
                // Salidas tacticas (senal/TP/trailing) se mantienen condicionadas a profit.
                var tacticalExit = profitableNow && (sellSignal || takeProfitHit || trailingStopHit || pnlPct >= bot.TakeProfit2Percent);
                var requestFullExit = riskExit || tacticalExit;
                var requestPartialTp = !bot.TakeProfit1Taken &&
                                       profitableNow &&
                                       pnlPct >= bot.TakeProfit1Percent &&
                                       bot.TakeProfit1SellPercent > 0m;
                var shouldExit = requestFullExit || requestPartialTp;
                if (!shouldExit)
                {
                    goto skipSell;
                }

                var qtyToSell = requestPartialTp && !requestFullExit
                    ? decimal.Round(
                        Math.Max(
                            bot.PositionQuantity * (Math.Clamp(bot.TakeProfit1SellPercent, 0m, 100m) / 100m),
                            Math.Min(bot.PositionQuantity, 0.000001m)),
                        8,
                        MidpointRounding.ToZero)
                    : bot.PositionQuantity;
                qtyToSell = Math.Min(qtyToSell, bot.PositionQuantity);

                var fill = realTradingEnabled
                    ? await tradeExecutionService.MarketSellAsync(activeSymbol, qtyToSell, bot.Id)
                    : new TradeFillResult
                    {
                        ExecutedQuantity = qtyToSell,
                        AveragePrice = activePrice
                    };
                if (!realTradingEnabled)
                {
                    dbContext.OrderAuditEvents.Add(new OrderAuditEvent
                    {
                        BotId = bot.Id,
                        Symbol = activeSymbol,
                        Side = "SELL",
                        Stage = "execution",
                        Status = "simulated",
                        Message = "Ejecucion paper SELL.",
                        RequestedBaseQty = qtyToSell,
                        ExecutedQty = fill?.ExecutedQuantity ?? 0m,
                        ExecutedPrice = fill?.AveragePrice ?? 0m,
                        IsLive = false,
                        CreatedAtUtc = DateTime.UtcNow
                    });
                }

                if (fill is not null && fill.ExecutedQuantity > 0 && fill.AveragePrice > 0)
                {
                    ResetExecutionFailure(bot);
                    var realized = (fill.AveragePrice - bot.AverageEntryPrice) * fill.ExecutedQuantity;
                    realized = decimal.Round(realized, 2);
                    bot.RealizedPnlUsdt += realized;
                    bot.ConsecutiveLossTrades = realized < 0m ? bot.ConsecutiveLossTrades + 1 : 0;
                    if (realized < 0m && bot.CooldownMinutesAfterLoss > 0)
                    {
                        bot.CooldownSymbol = activeSymbol;
                        bot.CooldownUntilUtc = DateTime.UtcNow.AddMinutes(bot.CooldownMinutesAfterLoss);
                    }
                    else if (realized >= 0m)
                    {
                        bot.CooldownSymbol = string.Empty;
                        bot.CooldownUntilUtc = null;
                    }
                    bot.PositionQuantity = decimal.Round(Math.Max(0m, bot.PositionQuantity - fill.ExecutedQuantity), 8, MidpointRounding.ToZero);
                    if (bot.PositionQuantity <= 0m)
                    {
                        bot.UnrealizedPnlUsdt = 0m;
                        bot.PositionQuantity = 0m;
                        bot.AverageEntryPrice = 0m;
                        bot.PositionSymbol = string.Empty;
                        bot.PositionOpenedAtUtc = null;
                        bot.PeakPriceSinceEntry = 0m;
                        bot.TakeProfit1Taken = false;
                    }
                    else if (requestPartialTp && !requestFullExit)
                    {
                        bot.TakeProfit1Taken = true;
                    }
                    bot.LastExecutionError = string.Empty;
                    dbContext.Trades.Add(new TradeExecution
                    {
                        BotId = bot.Id,
                        Symbol = activeSymbol,
                        Side = "SELL",
                        Price = fill.AveragePrice,
                        Quantity = fill.ExecutedQuantity,
                        RealizedPnlUsdt = realized,
                        ExecutedAtUtc = DateTime.UtcNow
                    });
                    if (mlEnabled)
                    {
                        bot.MlRoundTripRealizedUsdt += realized;
                        var positionClosed = bot.PositionQuantity <= 0m;
                        if (positionClosed)
                        {
                            await tradeMlService.RecordExitAsync(bot.Id, activeSymbol, bot.MlRoundTripRealizedUsdt);
                            bot.MlRoundTripRealizedUsdt = 0m;
                        }
                    }
                }
                else if (realTradingEnabled)
                {
                    bot.LastExecutionError = tradeExecutionService.GetLastExecutionError();
                    RegisterExecutionFailure(bot, activeSymbol);
                }
            }
            skipSell:;

            if (bot.ConsecutiveLossTrades >= Math.Max(1, bot.MaxConsecutiveLossTrades))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = "Bot pausado por racha de perdidas consecutivas (AutoPilot).";
                logger.LogWarning("Bot {BotName} detenido por racha de perdidas consecutivas ({LossCount}).", bot.Name, bot.ConsecutiveLossTrades);
            }

            ApplyEdgeThrottling(bot, recentSellTrades);

            var closedCount = recentSellTrades.Count(x => x.BotId == bot.Id);
            if (bot.IsAutoManaged &&
                closedCount >= MinClosedTradesForAdaptive &&
                bot.RealizedPnlUsdt >= bot.AutoScaleReferencePnlUsdt + 1m &&
                (bot.LastAutoScaleUtc is null || (DateTime.UtcNow - bot.LastAutoScaleUtc.Value) >= AutoScaleCooldown))
            {
                // Escalado gradual: aumenta budget si el bot ya demostro profit.
                bot.BudgetUsdt = Math.Min(bot.BudgetUsdt + 2m, 200m);
                bot.MaxPositionPerTradeUsdt = Math.Min(Math.Max(bot.BudgetUsdt * 0.20m, MinQuoteOrderUsdt), 40m);
                bot.AutoScaleReferencePnlUsdt = bot.RealizedPnlUsdt;
                bot.LastAutoScaleUtc = DateTime.UtcNow;
            }

            if (bot.RealizedPnlUsdt <= -Math.Abs(bot.MaxDailyLossUsdt))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = "Bot pausado por perdida diaria maxima (AutoPilot).";
                logger.LogWarning("Bot {BotName} detenido por max daily loss.", bot.Name);
            }

            bot.UpdatedAtUtc = DateTime.UtcNow;
        }

        // Retencion simple para no crecer indefinidamente.
        var threshold = DateTime.UtcNow.AddDays(-30);
        var oldAudit = await dbContext.OrderAuditEvents.Where(x => x.CreatedAtUtc < threshold).ToListAsync();
        if (oldAudit.Count > 0)
        {
            dbContext.OrderAuditEvents.RemoveRange(oldAudit);
        }

        await dbContext.SaveChangesAsync();
    }

    private void ApplyEdgeThrottling(TradingBot bot, List<TradeExecution> recentSellTrades)
    {
        var sample = recentSellTrades
            .Where(x => x.BotId == bot.Id)
            .Take(200)
            .ToList();
        if (sample.Count < MinClosedTradesForAdaptive)
        {
            bot.RollingExpectancyUsdt = 0m;
            bot.NegativeEdgeCycles = 0;
            return;
        }

        var wins = sample.Where(x => x.RealizedPnlUsdt > 0m).ToList();
        var losses = sample.Where(x => x.RealizedPnlUsdt < 0m).ToList();
        var winRate = wins.Count * 1m / sample.Count;
        var avgWin = wins.Count == 0 ? 0m : wins.Average(x => x.RealizedPnlUsdt);
        var avgLossAbs = losses.Count == 0 ? 0m : Math.Abs(losses.Average(x => x.RealizedPnlUsdt));
        var expectancy = (winRate * avgWin) - ((1m - winRate) * avgLossAbs);
        bot.RollingExpectancyUsdt = decimal.Round(expectancy, 4);

        if (expectancy < 0m)
        {
            bot.NegativeEdgeCycles++;
        }
        else
        {
            bot.NegativeEdgeCycles = 0;
        }

        if (!bot.IsAutoManaged)
        {
            return;
        }

        var canAdjustRisk = bot.LastRiskAdjustmentUtc is null ||
                            (DateTime.UtcNow - bot.LastRiskAdjustmentUtc.Value) >= RiskAdjustmentCooldown;
        if (bot.NegativeEdgeCycles >= 3 && canAdjustRisk)
        {
            // Baja riesgo progresivamente cuando el edge reciente es negativo.
            bot.MaxPositionPerTradeUsdt = decimal.Round(Math.Max(MinQuoteOrderUsdt, bot.MaxPositionPerTradeUsdt * 0.80m), 2);
            bot.LastRiskAdjustmentUtc = DateTime.UtcNow;
        }

        if (bot.NegativeEdgeCycles >= 6)
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Bot pausado por edge negativo persistente (expectancy rolling < 0).";
            logger.LogWarning("Bot {BotName} pausado por edge negativo persistente. Expectancy={Expectancy}", bot.Name, bot.RollingExpectancyUsdt);
        }
    }

    public async Task<IReadOnlyCollection<BotSignalDiagnosticsItem>> GetSignalDiagnosticsAsync(IEnumerable<Guid>? botIds = null)
    {
        IQueryable<TradingBot> q = dbContext.Bots;
        if (botIds is not null)
        {
            var set = botIds.ToHashSet();
            if (set.Count == 0)
            {
                return Array.Empty<BotSignalDiagnosticsItem>();
            }

            q = q.Where(b => set.Contains(b.Id));
        }

        var bots = await q.OrderBy(x => x.Name).ToListAsync();
        var allSymbols = bots.SelectMany(x => x.Symbols).Distinct().ToList();
        var market = await marketService.GetMarketOverviewAsync(allSymbols);
        var marketSnapshot = market.ToDictionary(x => x.Symbol, x => x);
        var technicalBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "1m", 150);
        var technical5mBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "5m", 150);
        var technical15mBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "15m", 150);
        var result = new List<BotSignalDiagnosticsItem>();

        foreach (var bot in bots)
        {
            var selected = bot.Symbols
                .Where(marketSnapshot.ContainsKey)
                .Where(technicalBySymbol.ContainsKey)
                .ToList();
            if (selected.Count == 0)
            {
                result.Add(new BotSignalDiagnosticsItem
                {
                    BotId = bot.Id,
                    BotName = bot.Name,
                    SignalLabel = "SIN_DATOS",
                    Reason = "Sin mercado/indicadores para simbolos configurados."
                });
                continue;
            }

            var activeSymbol = bot.PositionQuantity > 0m && !string.IsNullOrWhiteSpace(bot.PositionSymbol) &&
                               technicalBySymbol.ContainsKey(bot.PositionSymbol)
                ? bot.PositionSymbol
                : selected[0];
            var activePrice = marketSnapshot[activeSymbol].LastPrice;
            var activeTechnical = technicalBySymbol[activeSymbol];
            var investedCapital = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? bot.PositionQuantity * bot.AverageEntryPrice
                : 0m;
            var exposureLimit = bot.BudgetUsdt * (Math.Clamp(bot.MaxExposurePercent, 1m, 100m) / 100m);
            var remainingBudget = Math.Max(0m, exposureLimit - investedCapital);
            var profitableNow = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m && activePrice > bot.AverageEntryPrice;
            var sellSignal = ShouldSellBySignal(bot.StrategyType, activeTechnical);
            var takeProfitHit = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m &&
                                ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m >= bot.TakeProfitPercent;
            var stopLossHit = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m &&
                              ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m <= -bot.StopLossPercent;
            var pnlPct = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m
                : 0m;
            var trailingArmed = pnlPct >= bot.TrailingActivationPercent && bot.PeakPriceSinceEntry > 0m;
            var timeStopHit = bot.PositionOpenedAtUtc is not null &&
                              bot.MaxHoldingMinutes > 0 &&
                              DateTime.UtcNow >= bot.PositionOpenedAtUtc.Value.AddMinutes(bot.MaxHoldingMinutes);
            var tp1Ready = !bot.TakeProfit1Taken && pnlPct >= bot.TakeProfit1Percent;
            var tp2Ready = pnlPct >= bot.TakeProfit2Percent;
            var buyCandidate = selected
                .Select(symbol => new { Symbol = symbol, Snapshot = technicalBySymbol[symbol] })
                .Where(x =>
                    technical5mBySymbol.TryGetValue(x.Symbol, out var tf5) &&
                    technical15mBySymbol.TryGetValue(x.Symbol, out var tf15) &&
                    ShouldBuy(bot.StrategyType, x.Snapshot) &&
                    PassesMultiTimeframeTrend(bot.StrategyType, tf5, tf15) &&
                    PassesLiquidityAndVolume(marketSnapshot[x.Symbol], x.Snapshot) &&
                    PassesRegimeFilter(bot.StrategyType, x.Snapshot))
                .OrderByDescending(x => ScoreBuyCandidate(bot.StrategyType, x.Snapshot))
                .FirstOrDefault();

            var label = "ESPERANDO";
            var reason = string.Empty;
            if (bot.State != BotState.Running)
            {
                label = "DETENIDO";
                reason = "Bot en estado detenido.";
            }
            else if (bot.PositionQuantity <= 0m)
            {
                if (buyCandidate is not null)
                {
                    var blockPullbackVolatileDay = false;
                    var blockPullbackEmaSpread = false;
                    if (bot.StrategyType == StrategyType.Pullback)
                    {
                        var abs24 = Math.Abs(marketSnapshot[buyCandidate.Symbol].PriceChangePercent24h);
                        blockPullbackVolatileDay = abs24 >= PullbackMaxAbsChange24hPercent;
                        var buySnapDiag = buyCandidate.Snapshot;
                        if (buySnapDiag.LastPrice > 0m)
                        {
                            var emaSpreadPct = Math.Abs(buySnapDiag.EmaFast - buySnapDiag.EmaSlow) / buySnapDiag.LastPrice * 100m;
                            blockPullbackEmaSpread = emaSpreadPct > PullbackMaxEmaSpreadPercentOfPrice;
                        }
                    }

                    var quoteCandidate = Math.Min(Math.Max(0m, bot.MaxPositionPerTradeUsdt), remainingBudget);
                    if (blockPullbackVolatileDay || blockPullbackEmaSpread)
                    {
                        label = "ESPERANDO";
                        reason = blockPullbackVolatileDay
                            ? $"Pullback: |Δ24h| >= {PullbackMaxAbsChange24hPercent}% en {buyCandidate.Symbol} (filtro anti-extremo diario)."
                            : $"Pullback: separacion EMA 1m demasiado alta en {buyCandidate.Symbol} (proxy spread/tendencia).";
                    }
                    else if (quoteCandidate >= MinQuoteOrderUsdt)
                    {
                        label = "BUY_LISTO";
                        reason = $"Entrada valida en {buyCandidate.Symbol} (EMA/MACD/RSI alineados).";
                    }
                    else
                    {
                        reason = $"Sin presupuesto disponible para nueva compra (limite total/exposicion alcanzado o max por trade < {MinQuoteOrderUsdt:0.##} USDT).";
                    }
                }
                else
                {
                    reason = "Sin setup de entrada: condiciones EMA/MACD/RSI no alineadas.";
                    if (marketSnapshot.TryGetValue(activeSymbol, out var m) && m.QuoteVolume24h < MinQuoteVolume24hUsdt)
                    {
                        reason = "Bloqueado por liquidez: volumen 24h insuficiente.";
                    }
                    else if (technicalBySymbol.TryGetValue(activeSymbol, out var one) && one.RelativeVolume < MinRelativeVolume)
                    {
                        reason = "Bloqueado por volumen relativo bajo.";
                    }
                    else if (technical5mBySymbol.TryGetValue(activeSymbol, out var tf5) &&
                             technical15mBySymbol.TryGetValue(activeSymbol, out var tf15) &&
                             !PassesMultiTimeframeTrend(bot.StrategyType, tf5, tf15))
                    {
                        reason = "Bloqueado por confirmacion multi-timeframe (5m/15m).";
                    }
                }

                if (bot.CooldownUntilUtc is not null && DateTime.UtcNow < bot.CooldownUntilUtc.Value)
                {
                    reason = $"En cooldown por perdida hasta {bot.CooldownUntilUtc:O}.";
                }
            }
            else if (profitableNow && (sellSignal || takeProfitHit || stopLossHit))
            {
                label = "SELL_LISTO";
                reason = "Salida habilitada por señal tecnica o TP/SL con profit.";
            }
            else if (!profitableNow)
            {
                reason = "Hay posicion, pero aun sin profit sobre precio de entrada.";
            }
            else
            {
                reason = "Hay posicion con profit, esperando confirmacion de salida tecnica.";
            }

            result.Add(new BotSignalDiagnosticsItem
            {
                BotId = bot.Id,
                BotName = bot.Name,
                SignalLabel = label,
                Reason = string.IsNullOrWhiteSpace(bot.LastExecutionError) ? reason : $"{reason} Ultimo error: {bot.LastExecutionError}",
                ActiveSymbol = activeSymbol,
                ExitState = BuildExitState(bot, tp1Ready, tp2Ready, trailingArmed, timeStopHit, sellSignal)
            });
        }

        return result;
    }

    private static bool ShouldBuy(StrategyType strategy, TechnicalMarketSnapshot technical) =>
        strategy == StrategyType.Momentum
            ? technical.EmaFast > technical.EmaSlow &&
              technical.MacdLine > technical.MacdSignal &&
              technical.PreviousMacdHistogram <= 0m &&
              technical.MacdHistogram > 0m &&
              technical.Rsi14 >= 50m &&
              technical.Rsi14 <= 76m
            : technical.EmaFast >= technical.EmaSlow &&
              technical.Rsi14 <= 38m &&
              technical.MacdHistogram > technical.PreviousMacdHistogram;

    private static bool ShouldSellBySignal(StrategyType strategy, TechnicalMarketSnapshot technical) =>
        strategy == StrategyType.Momentum
            ? technical.EmaFast < technical.EmaSlow ||
              technical.MacdLine < technical.MacdSignal ||
              technical.Rsi14 >= 78m
            : technical.Rsi14 >= 58m ||
              technical.MacdLine < technical.MacdSignal;

    private static decimal ScoreBuyCandidate(StrategyType strategy, TechnicalMarketSnapshot technical) =>
        strategy == StrategyType.Momentum
            ? (technical.MacdHistogram * 1000m) + (technical.EmaFast - technical.EmaSlow) + technical.Rsi14 + (technical.RelativeVolume * 5m)
            : (50m - technical.Rsi14) + (technical.MacdHistogram - technical.PreviousMacdHistogram) * 1000m + (technical.RelativeVolume * 5m);

    private static bool PassesLiquidityAndVolume(MarketTicker ticker, TechnicalMarketSnapshot technical) =>
        ticker.QuoteVolume24h >= MinQuoteVolume24hUsdt && technical.RelativeVolume >= MinRelativeVolume;

    private static bool PassesRegimeFilter(StrategyType strategy, TechnicalMarketSnapshot technical)
    {
        if (technical.LastPrice <= 0m)
        {
            return false;
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        var trendOk = strategy == StrategyType.Momentum
            ? emaSpreadPct >= MinTrendSpreadPercentForEntry
            : emaSpreadPct <= PullbackMaxEmaSpreadPercentOfPrice;
        var volatilityOk = technical.VolatilityPercent <= MaxVolatilityPercentForEntry || technical.AtrPercent <= MaxAtrPercentForEntry;
        return trendOk && volatilityOk;
    }

    private static bool PassesMultiTimeframeTrend(StrategyType strategy, TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15) =>
        strategy == StrategyType.Momentum
            ? tf5.EmaFast > tf5.EmaSlow && tf15.EmaFast > tf15.EmaSlow && tf15.MacdLine >= (tf15.MacdSignal - 0.0002m)
            : tf15.EmaFast >= tf15.EmaSlow && tf5.Rsi14 <= 55m;

    private static string BuildExitState(TradingBot bot, bool tp1Ready, bool tp2Ready, bool trailingArmed, bool timeStopHit, bool sellSignal)
    {
        if (bot.PositionQuantity <= 0m)
        {
            return "SIN_POSICION";
        }

        var states = new List<string>();
        if (tp1Ready) states.Add("TP1_LISTO");
        if (tp2Ready) states.Add("TP2_LISTO");
        if (trailingArmed) states.Add("TRAILING_ARMADO");
        if (timeStopHit) states.Add("TIME_STOP_LISTO");
        if (sellSignal) states.Add("SENAL_SALIDA");
        return states.Count == 0 ? "MANTENER" : string.Join(" | ", states);
    }

    private static string ResolveQuoteAsset(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        if (upper.EndsWith("USDT", StringComparison.Ordinal)) return "USDT";
        if (upper.EndsWith("USDC", StringComparison.Ordinal)) return "USDC";
        if (upper.EndsWith("BUSD", StringComparison.Ordinal)) return "BUSD";
        return "USDT";
    }

    private static decimal ComputeRiskSizedQuote(TradingBot bot, decimal remainingBudget, TechnicalMarketSnapshot snapshot)
    {
        var perTradeLimit = Math.Max(0m, bot.MaxPositionPerTradeUsdt);
        var maxAllowed = Math.Min(perTradeLimit, Math.Max(0m, remainingBudget));
        if (maxAllowed <= 0m)
        {
            return 0m;
        }

        var stopDistancePct = Math.Max(0.20m, bot.StopLossPercent) / 100m;
        var volatilityPenalty = 1m + Math.Max(0m, snapshot.VolatilityPercent);
        var atrPenalty = 1m + Math.Max(0m, snapshot.AtrPercent / 2m);
        var riskBudgetUsdt = bot.BudgetUsdt * (BaseRiskPercentPerTrade / 100m) / Math.Max(1m, volatilityPenalty * atrPenalty);
        var quoteByRisk = stopDistancePct <= 0m ? maxAllowed : riskBudgetUsdt / stopDistancePct;
        var sized = decimal.Round(Math.Min(maxAllowed, Math.Max(0m, quoteByRisk)), 2, MidpointRounding.ToZero);

        // Con budgets pequeños (p.ej. 20 USDT) el riesgo porcentual puede quedar bajo el notional mínimo de Binance/paper;
        // si el tope lo permite, subimos al mínimo operable para no bloquear el ciclo indefinidamente.
        if (sized > 0m && sized < MinQuoteOrderUsdt && maxAllowed >= MinQuoteOrderUsdt)
        {
            sized = MinQuoteOrderUsdt;
        }

        return sized;
    }

    private bool IsSymbolCircuitOpen(string symbol)
    {
        if (!SymbolCircuitOpenUntilUtc.TryGetValue(symbol, out var until))
        {
            return false;
        }

        if (DateTime.UtcNow >= until)
        {
            SymbolCircuitOpenUntilUtc.TryRemove(symbol, out _);
            return false;
        }

        return true;
    }

    private void ResetExecutionFailure(TradingBot bot)
    {
        BotExecutionFailures.TryRemove(bot.Id, out _);
        if (!string.IsNullOrWhiteSpace(bot.PositionSymbol))
        {
            SymbolCircuitOpenUntilUtc.TryRemove(bot.PositionSymbol, out _);
        }
    }

    private void RegisterExecutionFailure(TradingBot bot, string symbol)
    {
        var failures = BotExecutionFailures.AddOrUpdate(bot.Id, 1, (_, oldValue) => oldValue + 1);
        if (failures < ExecutionFailureCircuitThreshold)
        {
            return;
        }

        var until = DateTime.UtcNow.Add(ExecutionFailureCircuitDuration);
        SymbolCircuitOpenUntilUtc[symbol] = until;
        bot.State = BotState.Stopped;
        bot.LastExecutionError = $"Circuit breaker: {failures} fallos consecutivos en {symbol}. Bot pausado hasta intervención manual.";
        bot.CooldownSymbol = symbol;
        bot.CooldownUntilUtc = until;
        logger.LogWarning("Circuit breaker activado en bot {BotName} para {Symbol}. Fallos={Failures}", bot.Name, symbol, failures);
    }

    private static void ApplyRequest(TradingBot bot, CreateOrUpdateBotRequest request)
    {
        bot.Name = request.Name.Trim();
        bot.BudgetUsdt = request.BudgetUsdt;
        bot.MaxPositionPerTradeUsdt = request.MaxPositionPerTradeUsdt;
        bot.StopLossPercent = request.StopLossPercent;
        bot.TakeProfitPercent = request.TakeProfitPercent;
        bot.TakeProfit1Percent = request.TakeProfit1Percent;
        bot.TakeProfit1SellPercent = request.TakeProfit1SellPercent;
        bot.TakeProfit2Percent = request.TakeProfit2Percent;
        bot.TrailingActivationPercent = request.TrailingActivationPercent;
        bot.TrailingStopPercent = request.TrailingStopPercent;
        bot.MaxHoldingMinutes = Math.Max(0, request.MaxHoldingMinutes);
        bot.MaxDailyLossUsdt = request.MaxDailyLossUsdt;
        bot.MaxExposurePercent = Math.Clamp(request.MaxExposurePercent, 1m, 100m);
        bot.CooldownMinutesAfterLoss = Math.Max(0, request.CooldownMinutesAfterLoss);
        bot.MaxConsecutiveLossTrades = Math.Max(1, request.MaxConsecutiveLossTrades);
        bot.Symbols = request.Symbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        bot.UpdatedAtUtc = DateTime.UtcNow;
    }
}
