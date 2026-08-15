using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using TradingBots.App.Data;
using TradingBots.App.Models;
using TradingBots.App.Services.Strategies;

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
    IPortfolioRiskService portfolioRiskService,
    IStrategySignalRegistry strategySignals,
    IMarketHistoryService marketHistory,
    IMarketStructureService marketStructure,
    ILogger<BotService> logger) : IBotService
{
    /// <summary>Mínimo de notional por orden de compra (coherente con filtros típicos MIN_NOTIONAL en Binance).</summary>
    private const decimal MinQuoteOrderUsdt = 10m;
    private const decimal BaseRiskPercentPerTrade = 0.50m;
    /// <summary>Coste estimado round-trip (fees+slippage) en basis points.</summary>
    private const decimal RoundTripCostBps = 20m;
    /// <summary>Beneficio minimo neto (%) para salidas tacticas / TP parcial.</summary>
    private const decimal MinNetProfitToExitPercent = 0.50m;
    /// <summary>Tras MFE >= MinNetProfit, salir si el PnL cae a este umbral o menos (soft BE fee-aware).</summary>
    private const decimal SoftBreakevenExitPercent = 0.05m;
    /// <summary>Minutos sin MFE suficiente y en rojo claro → invalidacion temprana.</summary>
    private const int EarlyInvalidationMinutes = 180;
    /// <summary>Solo invalidar si el PnL ya esta claramente en rojo (evita micro-cortes -0.05%).</summary>
    private const decimal EarlyInvalidationMinLossPercent = -0.25m;
    /// <summary>Profit minimo (%) para time-stop al MaxHolding (cubre fees mejor que -0.20%).</summary>
    private const decimal TimeStopFeeAwareMinProfitPercent = 0.35m;
    /// <summary>Techo absoluto de hold (zombie): forzar cierre siempre.</summary>
    private const int MaxZombieHoldingMinutes = 480;
    /// <summary>PnL % anomalo: forzar venta y circuit del simbolo.</summary>
    private const decimal AnomalyLossPercent = -3.0m;
    /// <summary>Techo duro: nunca diferir SL por debajo de este PnL %.</summary>
    private const decimal StopLossHardFloorPercent = -2.0m;
    /// <summary>Minutos maximos de gracia tras el primer toque de SL si hay esperanza de rebote.</summary>
    private const int StopLossDeferGraceMinutes = 15;
    /// <summary>Distancia maxima a Support90d (%) para considerar "cerca de soporte".</summary>
    private const decimal StopLossDeferNearSupportPercent = 1.5m;
    /// <summary>Cooldown minimo tras cerrar en profit (anti-churn AutoPilot).</summary>
    private const int MinCooldownMinutesAfterWinForAuto = 20;
    private const int ExecutionFailureCircuitThreshold = 3;
    private static readonly TimeSpan ExecutionFailureCircuitDuration = TimeSpan.FromMinutes(20);
    private const int MinClosedTradesForAdaptive = 100;
    private static readonly TimeSpan RiskAdjustmentCooldown = TimeSpan.FromHours(6);
    private static readonly TimeSpan AutoScaleCooldown = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<Guid, int> BotExecutionFailures = new();
    private static readonly ConcurrentDictionary<string, DateTime> SymbolCircuitOpenUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gracia SL: clave botId|openedAt -> instante UTC hasta el que se puede diferir.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> StopLossGraceUntilUtc = new(StringComparer.Ordinal);

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
            var sym = bot.Symbols.FirstOrDefault() ?? string.Empty;
            if (EntryFilters.IsPreferredRecoverySymbol(sym))
            {
                // Reinicia presupuesto de pérdida de sesión para flota recovery.
                bot.AutoScaleReferencePnlUsdt = bot.RealizedPnlUsdt;
                bot.LastExecutionError = string.Empty;
            }
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
        var realized = ComputeRealizedPnlUsdt(entryPx, fill.AveragePrice, fill.ExecutedQuantity);
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
        ClearStopLossGrace(bot);
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

        if (EntryFilters.GetSessionRealizedPnl(bot) <= -Math.Abs(bot.MaxDailyLossUsdt))
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
        var technicalBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "1m", 200);
        var technical5mBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "5m", 200);
        var technical15mBySymbol = await marketService.GetTechnicalSnapshotsAsync(symbols, "15m", 200);
        var regimeBySymbol = await marketHistory.GetRegimesAsync(symbols);
        var structureBySymbol = await marketStructure.GetStructuresAsync(symbols);
        foreach (var bot in bots)
        {
            var signals = strategySignals.Get(bot.StrategyType);
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

                    regimeBySymbol.TryGetValue(x.Symbol, out var regime);
                    return signals.ShouldBuy(x.Snapshot) &&
                           signals.PassesMultiTimeframeTrend(tf5, tf15) &&
                           EntryFilters.PassesLiquidityAndVolume(x.Symbol, marketSnapshot[x.Symbol], x.Snapshot) &&
                           signals.PassesShortRegimeFilter(x.Snapshot, marketSnapshot[x.Symbol]) &&
                           signals.PassesLongTermRegime(regime) &&
                           PassesMarketStructureForBuy(structureBySymbol.GetValueOrDefault(x.Symbol));
                })
                .OrderByDescending(x => signals.ScoreBuyCandidate(x.Snapshot) + ScoreMarketStructureBonus(structureBySymbol.GetValueOrDefault(x.Symbol)))
                .FirstOrDefault();
            var buySignal = buyCandidate is not null;
            var activeTechnical = technicalBySymbol.TryGetValue(activeSymbol, out var t) ? t : null;
            var sellSignal = activeTechnical is not null && signals.ShouldSell(activeTechnical);
            var effectiveStopPct = activeTechnical is not null
                ? ComputeEffectiveStopLossPercent(bot, activeTechnical)
                : bot.StopLossPercent;
            var takeProfitHit = bot.PositionQuantity > 0 && bot.AverageEntryPrice > 0 &&
                                ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m >= bot.TakeProfitPercent;
            var stopLossHit = bot.PositionQuantity > 0 && bot.AverageEntryPrice > 0 &&
                              ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m <= -effectiveStopPct;
            var pnlPct = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m
                : 0m;
            var roundTripCostPct = RoundTripCostBps / 100m;
            var mfePct = ComputeMaxFavorableExcursionPercent(bot);
            var holdingMinutes = GetHoldingMinutes(bot);
            var progressToTp = bot.TakeProfitPercent > 0m ? pnlPct / bot.TakeProfitPercent : 0m;
            // Soft BE: si el trade llego a +0.50% MFE, proteger giveback (no esperar SL).
            var softBreakevenArmed = bot.PositionQuantity > 0m && mfePct >= MinNetProfitToExitPercent;
            var softBreakevenHit = softBreakevenArmed && pnlPct <= SoftBreakevenExitPercent;
            // BE profundo legacy: 70% del camino al TP.
            var breakevenArmed = progressToTp >= 0.70m &&
                                 pnlPct > roundTripCostPct &&
                                 bot.PositionQuantity > 0m;
            var breakevenStopHit = breakevenArmed && activePrice < bot.AverageEntryPrice;
            var trailingArmed = pnlPct >= Math.Max(bot.TrailingActivationPercent, MinNetProfitToExitPercent) &&
                                bot.PeakPriceSinceEntry > 0m;
            var trailingStopHit = trailingArmed &&
                                  activePrice <= bot.PeakPriceSinceEntry * (1m - (bot.TrailingStopPercent / 100m));
            var configuredHoldMinutes = bot.MaxHoldingMinutes > 0 ? bot.MaxHoldingMinutes : 360;
            var timeExpiredConfigured = holdingMinutes >= configuredHoldMinutes;
            var timeExpiredZombie = holdingMinutes >= MaxZombieHoldingMinutes;
            var activeStructure = structureBySymbol.GetValueOrDefault(activeSymbol);
            var contextDefensiveExitHit = ShouldMarketStructureDefensiveExit(activeStructure, pnlPct);
            // Time-stop fee-aware: a MaxHolding solo con profit suficiente o defensiva; a 480m forzar.
            var timeStopHit = timeExpiredZombie ||
                              (timeExpiredConfigured &&
                               (contextDefensiveExitHit || pnlPct >= TimeStopFeeAwareMinProfitPercent));
            var earlyInvalidationHit = holdingMinutes >= EarlyInvalidationMinutes &&
                                       mfePct < MinNetProfitToExitPercent &&
                                       pnlPct <= EarlyInvalidationMinLossPercent;
            var anomalyLossHit = bot.PositionQuantity > 0m && pnlPct <= AnomalyLossPercent;
            var netProfitableEnough = pnlPct >= MinNetProfitToExitPercent;
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
                blockPullbackVolatileDay = abs24 >= StrategySignalConstants.PullbackMaxAbsChange24hPercent;
                var buySnap = buyCandidate.Snapshot;
                if (buySnap.LastPrice > 0m)
                {
                    var emaSpreadPct = Math.Abs(buySnap.EmaFast - buySnap.EmaSlow) / buySnap.LastPrice * 100m;
                    blockPullbackEmaSpread = emaSpreadPct > StrategySignalConstants.PullbackMaxEmaSpreadPercentOfPrice;
                }
            }

            // Evita promediar al alza involuntariamente: primero se cierra posicion, luego se permite nueva entrada.
            if (bot.PositionQuantity <= 0m && buySignal && !blockPullbackVolatileDay && !blockPullbackEmaSpread)
            {
                var symbol = buyCandidate!.Symbol;
                if (IsSymbolCircuitOpen(symbol))
                {
                    bot.LastExecutionError = $"Circuit breaker activo para {symbol}. Reintento diferido.";
                    continue;
                }

                var portfolioVerdict = await portfolioRiskService.EvaluateNewBuyAsync(symbol, marketSnapshot);
                if (!portfolioVerdict.Allowed)
                {
                    bot.LastExecutionError = portfolioVerdict.Reason;
                    if (mlEnabled && buyCandidate.Snapshot is not null)
                    {
                        await tradeMlService.RecordShadowSignalAsync(
                            bot.Id, symbol, bot.StrategyType, buyCandidate.Snapshot.LastPrice,
                            buyCandidate.Snapshot, marketSnapshot[symbol], portfolioVerdict.Reason, 0m);
                    }
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
                            await tradeMlService.RecordShadowSignalAsync(
                                bot.Id, symbol, bot.StrategyType, entryPrice,
                                buyCandidate.Snapshot, marketSnapshot[symbol],
                                bot.LastExecutionError, mlEval.WinProbability);
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
                string stopLossDeferReason = string.Empty;
                var stopLossExit = stopLossHit &&
                                   !ShouldDeferStopLoss(
                                       bot,
                                       pnlPct,
                                       mfePct,
                                       activeStructure,
                                       activeTechnical,
                                       out stopLossDeferReason);
                if (stopLossHit && !stopLossExit)
                {
                    bot.LastExecutionError = stopLossDeferReason;
                    logger.LogInformation(
                        "Bot {BotName}: SL diferido ({Reason}). PnL={PnlPct:0.##}%",
                        bot.Name,
                        stopLossDeferReason,
                        pnlPct);
                }

                if (anomalyLossHit)
                {
                    logger.LogCritical(
                        "Bot {BotName} {Symbol}: perdida anomala PnL={PnlPct:0.##}%. Forzando venta y circuit.",
                        bot.Name,
                        activeSymbol,
                        pnlPct);
                    SymbolCircuitOpenUntilUtc[activeSymbol] = DateTime.UtcNow.Add(ExecutionFailureCircuitDuration);
                    bot.LastExecutionError =
                        $"Salida forzada por perdida anomala ({pnlPct:0.##}% <= {AnomalyLossPercent:0.##}%).";
                }

                // Salidas de riesgo (SL/time-stop/contexto/BE/invalidacion/anomalia) sin exigir profit tactico.
                var riskExit = stopLossExit || breakevenStopHit || softBreakevenHit || timeStopHit ||
                               contextDefensiveExitHit || earlyInvalidationHit || anomalyLossHit;
                // Salidas tacticas: exigen beneficio neto sobre coste round-trip estimado.
                var tacticalExit = netProfitableEnough &&
                                   (sellSignal || takeProfitHit || trailingStopHit || pnlPct >= bot.TakeProfit2Percent);
                var requestFullExit = riskExit || tacticalExit;
                var requestPartialTp = !bot.TakeProfit1Taken &&
                                       netProfitableEnough &&
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
                    var realized = ComputeRealizedPnlUsdt(bot.AverageEntryPrice, fill.AveragePrice, fill.ExecutedQuantity);
                    realized = decimal.Round(realized, 2);
                    bot.RealizedPnlUsdt += realized;
                    bot.ConsecutiveLossTrades = realized < 0m ? bot.ConsecutiveLossTrades + 1 : 0;
                    var positionFullyClosed = bot.PositionQuantity - fill.ExecutedQuantity <= 0m;
                    if (positionFullyClosed && bot.IsAutoManaged)
                    {
                        var cooldownMin = realized < 0m
                            ? Math.Max(bot.CooldownMinutesAfterLoss, 25)
                            : MinCooldownMinutesAfterWinForAuto;
                        bot.CooldownSymbol = activeSymbol;
                        bot.CooldownUntilUtc = DateTime.UtcNow.AddMinutes(cooldownMin);
                    }
                    else if (realized < 0m && bot.CooldownMinutesAfterLoss > 0)
                    {
                        bot.CooldownSymbol = activeSymbol;
                        bot.CooldownUntilUtc = DateTime.UtcNow.AddMinutes(bot.CooldownMinutesAfterLoss);
                    }
                    else if (realized >= 0m && !bot.IsAutoManaged)
                    {
                        bot.CooldownSymbol = string.Empty;
                        bot.CooldownUntilUtc = null;
                    }
                    bot.PositionQuantity = decimal.Round(Math.Max(0m, bot.PositionQuantity - fill.ExecutedQuantity), 8, MidpointRounding.ToZero);
                    if (bot.PositionQuantity <= 0m)
                    {
                        ClearStopLossGrace(bot);
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

            if (EntryFilters.GetSessionRealizedPnl(bot) <= -Math.Abs(bot.MaxDailyLossUsdt))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = "Bot pausado por perdida acumulada maxima (AutoPilot).";
                logger.LogWarning("Bot {BotName} detenido por max accumulated loss.", bot.Name);
            }

            var todayStart = DateTime.UtcNow.Date;
            var dailyPnl = recentSellTrades
                .Where(x => x.BotId == bot.Id && x.ExecutedAtUtc >= todayStart)
                .Sum(x => x.RealizedPnlUsdt);
            if (bot.IsAutoManaged && dailyPnl <= -Math.Abs(bot.MaxDailyLossUsdt))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    $"Bot pausado por perdida diaria maxima ({dailyPnl:0.##} USDT hoy, limite {bot.MaxDailyLossUsdt:0.##}).";
                logger.LogWarning("Bot {BotName} detenido por max daily loss. DailyPnl={DailyPnl}", bot.Name, dailyPnl);
            }
        }

        await tradeMlService.ResolveShadowSignalsAsync(marketSnapshot);

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
        var technicalBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "1m", 200);
        var technical5mBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "5m", 200);
        var technical15mBySymbol = await marketService.GetTechnicalSnapshotsAsync(allSymbols, "15m", 200);
        var regimeBySymbol = await marketHistory.GetRegimesAsync(allSymbols);
        var structureBySymbol = await marketStructure.GetStructuresAsync(allSymbols);
        var result = new List<BotSignalDiagnosticsItem>();

        foreach (var bot in bots)
        {
            var signals = strategySignals.Get(bot.StrategyType);
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
            var sellSignal = signals.ShouldSell(activeTechnical);
            var takeProfitHit = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m &&
                                ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m >= bot.TakeProfitPercent;
            var pnlPct = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m
                ? ((activePrice - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m
                : 0m;
            var mfePct = ComputeMaxFavorableExcursionPercent(bot);
            var holdingMinutes = GetHoldingMinutes(bot);
            var effectiveStopPctDiag = ComputeEffectiveStopLossPercent(bot, activeTechnical);
            var stopLossHit = bot.PositionQuantity > 0m && bot.AverageEntryPrice > 0m &&
                              pnlPct <= -effectiveStopPctDiag;
            var softBreakevenArmed = bot.PositionQuantity > 0m && mfePct >= MinNetProfitToExitPercent;
            var softBreakevenHit = softBreakevenArmed && pnlPct <= SoftBreakevenExitPercent;
            var trailingArmed = pnlPct >= bot.TrailingActivationPercent && bot.PeakPriceSinceEntry > 0m;
            var configuredHoldMinutes = bot.MaxHoldingMinutes > 0 ? bot.MaxHoldingMinutes : 360;
            var timeExpiredConfigured = holdingMinutes >= configuredHoldMinutes;
            var timeExpiredZombie = holdingMinutes >= MaxZombieHoldingMinutes;
            var activeStructure = structureBySymbol.GetValueOrDefault(activeSymbol);
            var contextDefensiveExitHit = ShouldMarketStructureDefensiveExit(activeStructure, pnlPct);
            var timeStopHit = timeExpiredZombie ||
                              (timeExpiredConfigured &&
                               (contextDefensiveExitHit || pnlPct >= TimeStopFeeAwareMinProfitPercent));
            var timeStopFeeGated = timeExpiredConfigured &&
                                   !timeStopHit &&
                                   !timeExpiredZombie;
            var earlyInvalidationHit = holdingMinutes >= EarlyInvalidationMinutes &&
                                       mfePct < MinNetProfitToExitPercent &&
                                       pnlPct <= EarlyInvalidationMinLossPercent;
            var anomalyLossHit = bot.PositionQuantity > 0m && pnlPct <= AnomalyLossPercent;
            var stopLossDeferReason = string.Empty;
            var stopLossDeferred = stopLossHit &&
                                   ShouldDeferStopLoss(bot, pnlPct, mfePct, activeStructure, activeTechnical, out stopLossDeferReason);
            var tp1Ready = !bot.TakeProfit1Taken && pnlPct >= bot.TakeProfit1Percent;
            var tp2Ready = pnlPct >= bot.TakeProfit2Percent;
            var buyCandidate = selected
                .Select(symbol => new { Symbol = symbol, Snapshot = technicalBySymbol[symbol] })
                .Where(x =>
                    technical5mBySymbol.TryGetValue(x.Symbol, out var tf5) &&
                    technical15mBySymbol.TryGetValue(x.Symbol, out var tf15) &&
                    signals.ShouldBuy(x.Snapshot) &&
                    signals.PassesMultiTimeframeTrend(tf5, tf15) &&
                    EntryFilters.PassesLiquidityAndVolume(x.Symbol, marketSnapshot[x.Symbol], x.Snapshot) &&
                    signals.PassesShortRegimeFilter(x.Snapshot, marketSnapshot[x.Symbol]) &&
                    signals.PassesLongTermRegime(regimeBySymbol.GetValueOrDefault(x.Symbol)) &&
                    PassesMarketStructureForBuy(structureBySymbol.GetValueOrDefault(x.Symbol)))
                .OrderByDescending(x => signals.ScoreBuyCandidate(x.Snapshot) + ScoreMarketStructureBonus(structureBySymbol.GetValueOrDefault(x.Symbol)))
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
                        blockPullbackVolatileDay = abs24 >= StrategySignalConstants.PullbackMaxAbsChange24hPercent;
                        var buySnapDiag = buyCandidate.Snapshot;
                        if (buySnapDiag.LastPrice > 0m)
                        {
                            var emaSpreadPct = Math.Abs(buySnapDiag.EmaFast - buySnapDiag.EmaSlow) / buySnapDiag.LastPrice * 100m;
                            blockPullbackEmaSpread = emaSpreadPct > StrategySignalConstants.PullbackMaxEmaSpreadPercentOfPrice;
                        }
                    }

                    var quoteCandidate = Math.Min(Math.Max(0m, bot.MaxPositionPerTradeUsdt), remainingBudget);
                    if (blockPullbackVolatileDay || blockPullbackEmaSpread)
                    {
                        label = "ESPERANDO";
                        reason = blockPullbackVolatileDay
                            ? $"Pullback: |Δ24h| >= {StrategySignalConstants.PullbackMaxAbsChange24hPercent}% en {buyCandidate.Symbol} (filtro anti-extremo diario)."
                            : $"Pullback: separacion EMA 1m demasiado alta en {buyCandidate.Symbol} (proxy spread/tendencia).";
                    }
                    else if (quoteCandidate >= MinQuoteOrderUsdt)
                    {
                        label = "BUY_LISTO";
                        var structure = structureBySymbol.GetValueOrDefault(buyCandidate.Symbol);
                        var context = structure?.HasData == true ? $" Contexto: {structure.Summary}." : string.Empty;
                        reason = $"Entrada valida en {buyCandidate.Symbol} (EMA/MACD/RSI alineados).{context}";
                    }
                    else
                    {
                        reason = $"Sin presupuesto disponible para nueva compra (limite total/exposicion alcanzado o max por trade < {MinQuoteOrderUsdt:0.##} USDT).";
                    }
                }
                else
                {
                    reason = "Sin setup de entrada.";
                    if (!marketSnapshot.TryGetValue(activeSymbol, out var m))
                    {
                        reason = "Sin ticker de mercado para el simbolo activo.";
                    }
                    else if (m.QuoteVolume24h < EntryFilters.GetMinQuoteVolume24h(activeSymbol))
                    {
                        reason = EntryFilters.DescribeLiquidityBlock(activeSymbol, m, activeTechnical)
                                 ?? "Bloqueado por liquidez: volumen 24h insuficiente.";
                    }
                    else if (activeTechnical.RelativeVolume < EntryFilters.GetMinRelativeVolume(activeSymbol))
                    {
                        reason = EntryFilters.DescribeLiquidityBlock(activeSymbol, m, activeTechnical)
                                 ?? "Bloqueado por volumen relativo bajo.";
                    }
                    else if (!technical5mBySymbol.TryGetValue(activeSymbol, out var tf5) ||
                             !technical15mBySymbol.TryGetValue(activeSymbol, out var tf15))
                    {
                        reason = "Sin datos 15m para confirmacion de tendencia.";
                    }
                    else if (!signals.ShouldBuy(activeTechnical))
                    {
                        reason = signals.DescribeBuySignalGap(activeTechnical);
                    }
                    else if (!signals.PassesMultiTimeframeTrend(tf5, tf15))
                    {
                        reason = "Bloqueado por tendencia 15m (EMA rapida debajo de la lenta).";
                    }
                    else
                    {
                        regimeBySymbol.TryGetValue(activeSymbol, out var longRegime);
                        var regimeMsg = signals.DescribeShortRegimeFailure(activeTechnical, m)
                            ?? signals.DescribeLongTermRegimeFailure(longRegime)
                            ?? DescribeMarketStructureBuyBlock(structureBySymbol.GetValueOrDefault(activeSymbol));
                        reason = regimeMsg
                            ?? "Ningun simbolo del bot cumple todos los filtros a la vez (revisa otros pares en la lista).";
                    }
                }

                if (bot.CooldownUntilUtc is not null && DateTime.UtcNow < bot.CooldownUntilUtc.Value)
                {
                    reason = $"En cooldown por perdida hasta {bot.CooldownUntilUtc:O}.";
                }
            }
            else if (anomalyLossHit)
            {
                label = "SELL_LISTO";
                reason = $"Salida forzada por perdida anomala ({pnlPct:0.##}% <= {AnomalyLossPercent:0.##}%).";
            }
            else if (softBreakevenHit)
            {
                label = "SOFT_BE_LISTO";
                reason =
                    $"Soft breakeven: MFE {mfePct:0.##}% >= {MinNetProfitToExitPercent:0.##}% y PnL actual {pnlPct:0.##}% <= {SoftBreakevenExitPercent:0.##}%.";
            }
            else if (earlyInvalidationHit)
            {
                label = "INVALIDACION_180";
                reason =
                    $"Invalidacion temprana: {holdingMinutes} min sin MFE >= {MinNetProfitToExitPercent:0.##}% y PnL {pnlPct:0.##}% <= {EarlyInvalidationMinLossPercent:0.##}%.";
            }
            else if (stopLossDeferred)
            {
                label = "SL_DIFERIDO";
                reason = stopLossDeferReason;
            }
            else if (timeStopFeeGated)
            {
                label = "TIME_STOP_FEE_GATE";
                reason =
                    $"Hold {holdingMinutes} min >= {configuredHoldMinutes}: esperando PnL >= {TimeStopFeeAwareMinProfitPercent:0.##}% " +
                    $"(actual {pnlPct:0.##}%) o defensiva; forzado a {MaxZombieHoldingMinutes} min.";
            }
            else if (stopLossHit || timeStopHit || contextDefensiveExitHit)
            {
                label = "SELL_LISTO";
                reason = stopLossHit
                    ? "Salida de riesgo por stop loss."
                    : timeStopHit
                        ? (timeExpiredZombie
                            ? $"Salida forzada por hold zombie ({holdingMinutes} >= {MaxZombieHoldingMinutes} min)."
                            : "Salida de riesgo por tiempo maximo en posicion (fee-aware).")
                        : DescribeMarketStructureDefensiveExit(activeStructure, pnlPct) ?? "Salida defensiva por contexto 30-90d.";
            }
            else if (profitableNow && (sellSignal || takeProfitHit))
            {
                label = "SELL_LISTO";
                reason = "Salida habilitada por señal tecnica o take profit con profit.";
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
                ExitState = BuildExitState(
                    bot,
                    tp1Ready,
                    tp2Ready,
                    trailingArmed,
                    timeStopHit,
                    sellSignal,
                    stopLossHit && !stopLossDeferred,
                    contextDefensiveExitHit,
                    stopLossDeferred,
                    softBreakevenHit,
                    earlyInvalidationHit,
                    timeStopFeeGated)
            });
        }

        return result;
    }

    private static decimal ComputeEffectiveStopLossPercent(TradingBot bot, TechnicalMarketSnapshot snapshot)
    {
        var atrBased = snapshot.AtrPercent * 1.5m;
        var floor = 1.2m;
        var ceiling = Math.Max(floor, bot.StopLossPercent);
        return Math.Clamp(Math.Max(atrBased, floor), floor, ceiling);
    }

    private static bool PassesMarketStructureForBuy(MarketStructureSnapshot? structure) =>
        DescribeMarketStructureBuyBlock(structure) is null;

    private static decimal ScoreMarketStructureBonus(MarketStructureSnapshot? structure)
    {
        if (structure is null || !structure.HasData)
        {
            return 0m;
        }

        var bonus = Math.Clamp(structure.ContextScore, -0.8m, 1.2m);
        if (structure.HasBullishFlag)
        {
            bonus += 0.35m;
        }

        return decimal.Round(Math.Clamp(bonus, -0.8m, 1.5m), 4);
    }

    private static string? DescribeMarketStructureBuyBlock(MarketStructureSnapshot? structure)
    {
        if (structure is null || !structure.HasData)
        {
            return null;
        }

        if (structure.IsOverextended &&
            !structure.HasBullishFlag &&
            (structure.PricePercentile90d >= 92m || structure.Change30dPercent >= 65m))
        {
            return $"Contexto 30-90d: sobreextension sin bandera/consolidacion ({structure.Summary}).";
        }

        if (!structure.HasBullishFlag &&
            structure.DistanceToResistancePercent is > 0m and < 2.5m)
        {
            return $"Contexto 30-90d: precio muy cerca de resistencia 90d ({structure.DistanceToResistancePercent:0.#}% de margen).";
        }

        if (!structure.HasBullishFlag && structure.ContextScore <= -0.75m)
        {
            return $"Contexto 30-90d desfavorable ({structure.Summary}).";
        }

        return null;
    }

    private static bool ShouldMarketStructureDefensiveExit(MarketStructureSnapshot? structure, decimal pnlPct) =>
        DescribeMarketStructureDefensiveExit(structure, pnlPct) is not null;

    private static string? DescribeMarketStructureDefensiveExit(MarketStructureSnapshot? structure, decimal pnlPct)
    {
        if (structure is null || !structure.HasData)
        {
            return null;
        }

        if (pnlPct > -0.35m)
        {
            return null;
        }

        if (structure.HasBullishFlag && structure.ContextScore > -0.25m)
        {
            return null;
        }

        if (pnlPct <= -0.8m &&
            structure.ContextScore <= -0.75m &&
            !structure.HasBullishFlag)
        {
            return $"Salida defensiva: posicion en perdida ({pnlPct:0.##}%) y contexto 30-90d desfavorable ({structure.Summary}).";
        }

        if (pnlPct <= -0.5m &&
            structure.IsOverextended &&
            !structure.HasBullishFlag &&
            structure.PricePercentile90d >= 90m)
        {
            return $"Salida defensiva: perdida ({pnlPct:0.##}%) tras sobreextension sin bandera ({structure.Summary}).";
        }

        if (pnlPct <= -0.6m &&
            structure.DistanceToResistancePercent is > 0m and < 2.5m &&
            structure.ContextScore <= 0m)
        {
            return $"Salida defensiva: perdida ({pnlPct:0.##}%) con poco margen hasta resistencia 90d ({structure.DistanceToResistancePercent:0.#}%).";
        }

        return null;
    }

    private static string BuildExitState(
        TradingBot bot,
        bool tp1Ready,
        bool tp2Ready,
        bool trailingArmed,
        bool timeStopHit,
        bool sellSignal,
        bool stopLossHit,
        bool contextDefensiveExitHit,
        bool stopLossDeferred = false,
        bool softBreakevenHit = false,
        bool earlyInvalidationHit = false,
        bool timeStopFeeGated = false)
    {
        if (bot.PositionQuantity <= 0m)
        {
            return "SIN_POSICION";
        }

        var states = new List<string>();
        if (tp1Ready) states.Add("TP1_LISTO");
        if (tp2Ready) states.Add("TP2_LISTO");
        if (trailingArmed) states.Add("TRAILING_ARMADO");
        if (softBreakevenHit) states.Add("SOFT_BE_LISTO");
        if (earlyInvalidationHit) states.Add("INVALIDACION_180");
        if (timeStopFeeGated) states.Add("TIME_STOP_FEE_GATE");
        if (timeStopHit) states.Add("TIME_STOP_LISTO");
        if (stopLossDeferred) states.Add("SL_DIFERIDO");
        if (stopLossHit) states.Add("STOP_LOSS_LISTO");
        if (contextDefensiveExitHit) states.Add("CONTEXTO_DEFENSIVO");
        if (sellSignal) states.Add("SENAL_SALIDA");
        return states.Count == 0 ? "MANTENER" : string.Join(" | ", states);
    }

    /// <summary>
    /// Diferir SL si hay esperanza de rebote (estructura 30-90d + tecnicos 1m),
    /// con gracia de 15 min y techo duro -2%.
    /// </summary>
    private static bool ShouldDeferStopLoss(
        TradingBot bot,
        decimal pnlPct,
        decimal mfePct,
        MarketStructureSnapshot? structure,
        TechnicalMarketSnapshot? technical,
        out string reason)
    {
        reason = string.Empty;
        if (pnlPct <= StopLossHardFloorPercent)
        {
            ClearStopLossGrace(bot);
            reason = $"SL forzado: techo duro {StopLossHardFloorPercent:0.##}% alcanzado ({pnlPct:0.##}%).";
            return false;
        }

        // Sin MFE minimo no hay "esperanza de proteger gains": vender al primer toque de SL.
        if (mfePct < MinNetProfitToExitPercent)
        {
            ClearStopLossGrace(bot);
            reason = $"SL sin diferir: MFE {mfePct:0.##}% < {MinNetProfitToExitPercent:0.##}% (trade nunca fue suficientemente verde).";
            return false;
        }

        if (!HasStopLossBounceHope(structure, technical, out var hopeDetail))
        {
            reason = $"SL sin diferir: sin esperanza de rebote ({hopeDetail}).";
            return false;
        }

        var now = DateTime.UtcNow;
        var key = BuildStopLossGraceKey(bot);
        var graceUntil = StopLossGraceUntilUtc.AddOrUpdate(
            key,
            _ => now.AddMinutes(StopLossDeferGraceMinutes),
            (_, existing) => existing);

        if (now >= graceUntil)
        {
            reason = $"SL sin diferir: gracia de {StopLossDeferGraceMinutes} min agotada ({hopeDetail}).";
            return false;
        }

        reason =
            $"SL tocado ({pnlPct:0.##}%); diferido por evaluacion historica/tecnica hasta {graceUntil:HH:mm:ss} UTC " +
            $"o {StopLossHardFloorPercent:0.##}% ({hopeDetail}).";
        return true;
    }

    private static bool HasStopLossBounceHope(
        MarketStructureSnapshot? structure,
        TechnicalMarketSnapshot? technical,
        out string detail)
    {
        var structureHope = false;
        var structureBits = new List<string>();
        if (structure is { HasData: true })
        {
            var nearSupport = structure.DistanceToSupportPercent is >= 0m and <= StopLossDeferNearSupportPercent;
            var bullishOk = structure.HasBullishFlag && structure.ContextScore > -0.5m;
            var nearRangeLow = structure.PricePercentile90d <= 25m;
            var hopeless = structure.ContextScore <= -0.75m && !structure.HasBullishFlag && structure.PricePercentile90d > 50m;

            if (hopeless)
            {
                detail = $"contexto 30-90d desfavorable ({structure.Summary})";
                return false;
            }

            structureHope = nearSupport || bullishOk || nearRangeLow;
            if (nearSupport) structureBits.Add($"cerca soporte 90d ({structure.DistanceToSupportPercent:0.#}%)");
            if (bullishOk) structureBits.Add("bandera alcista");
            if (nearRangeLow) structureBits.Add($"pct90d={structure.PricePercentile90d:0.#}");
        }

        var technicalHope = false;
        var techBits = new List<string>();
        if (technical is not null)
        {
            var rsiOversold = technical.Rsi14 <= 35m;
            var macdTurningUp = technical.MacdHistogram > technical.PreviousMacdHistogram;
            var rsiRecovering = technical.Rsi14 <= 42m && macdTurningUp;
            var macdRisingFromNeg = technical.MacdHistogram < 0m && macdTurningUp;
            technicalHope = rsiOversold || rsiRecovering || macdRisingFromNeg;
            if (rsiOversold) techBits.Add($"RSI {technical.Rsi14:0.#}");
            if (rsiRecovering || macdRisingFromNeg) techBits.Add("MACD histograma mejorando");
        }

        // Requiere estructura + tecnicos cuando hay datos de estructura; si no hay estructura, solo tecnicos fuertes.
        var ok = structure is { HasData: true }
            ? structureHope && technicalHope
            : technicalHope && technical is not null && technical.Rsi14 <= 30m &&
              technical.MacdHistogram > technical.PreviousMacdHistogram;

        if (!ok)
        {
            detail = structure is { HasData: true }
                ? $"estructura={structureHope}; tecnicos={technicalHope}"
                : "sin estructura 30-90d y tecnicos insuficientes";
            return false;
        }

        var parts = structureBits.Concat(techBits).ToList();
        detail = parts.Count > 0 ? string.Join(", ", parts) : "rebote plausible";
        return true;
    }

    private static decimal ComputeMaxFavorableExcursionPercent(TradingBot bot)
    {
        if (bot.PositionQuantity <= 0m || bot.AverageEntryPrice <= 0m || bot.PeakPriceSinceEntry <= 0m)
        {
            return 0m;
        }

        return ((bot.PeakPriceSinceEntry - bot.AverageEntryPrice) / bot.AverageEntryPrice) * 100m;
    }

    private static int GetHoldingMinutes(TradingBot bot)
    {
        if (bot.PositionOpenedAtUtc is null)
        {
            return 0;
        }

        return Math.Max(0, (int)(DateTime.UtcNow - bot.PositionOpenedAtUtc.Value).TotalMinutes);
    }

    private static string BuildStopLossGraceKey(TradingBot bot)
    {
        var opened = bot.PositionOpenedAtUtc?.ToString("O") ?? "none";
        return $"{bot.Id:N}|{opened}";
    }

    private static void ClearStopLossGrace(TradingBot bot)
    {
        StopLossGraceUntilUtc.TryRemove(BuildStopLossGraceKey(bot), out _);
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

        // Budgets pequenos (AutoPilot ~20 USDT): usar el cupo completo por trade para evitar overtrading a 10 USDT.
        if (bot.BudgetUsdt <= 25m && maxAllowed >= MinQuoteOrderUsdt)
        {
            return decimal.Round(maxAllowed, 2, MidpointRounding.ToZero);
        }

        var stopDistancePct = Math.Max(0.20m, bot.StopLossPercent) / 100m;
        var volatilityPenalty = 1m + Math.Max(0m, snapshot.VolatilityPercent);
        var atrPenalty = 1m + Math.Max(0m, snapshot.AtrPercent / 2m);
        var riskBudgetUsdt = bot.BudgetUsdt * (BaseRiskPercentPerTrade / 100m) / Math.Max(1m, volatilityPenalty * atrPenalty);
        var quoteByRisk = stopDistancePct <= 0m ? maxAllowed : riskBudgetUsdt / stopDistancePct;
        var sized = decimal.Round(Math.Min(maxAllowed, Math.Max(0m, quoteByRisk)), 2, MidpointRounding.ToZero);

        if (sized > 0m && sized < MinQuoteOrderUsdt && maxAllowed >= MinQuoteOrderUsdt)
        {
            sized = MinQuoteOrderUsdt;
        }

        return sized;
    }

    /// <summary>PnL realizado restando coste round-trip estimado (fees+slippage).</summary>
    private static decimal ComputeRealizedPnlUsdt(decimal entryPrice, decimal exitPrice, decimal quantity)
    {
        if (quantity <= 0m || entryPrice <= 0m || exitPrice <= 0m)
        {
            return 0m;
        }

        var gross = (exitPrice - entryPrice) * quantity;
        var estimatedFee = (entryPrice + exitPrice) * quantity * (RoundTripCostBps / 20_000m);
        return decimal.Round(gross - estimatedFee, 2);
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


