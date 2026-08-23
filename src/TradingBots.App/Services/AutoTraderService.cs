using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;
using TradingBots.App.Services.Strategies;

namespace TradingBots.App.Services;

public interface IAutoTraderService
{
    Task<int> CreateBotsFromSuggestionsAsync();
}

public sealed class AutoTraderService(
    AppDbContext dbContext,
    IMarketAdvisorService advisorService,
    IBinanceSettingsService settingsService,
    IBacktestGateService backtestGate) : IAutoTraderService
{
    private static readonly HashSet<string> AutopilotSymbolBlocklist = new(StringComparer.Ordinal)
    {
        "UUSDT", "UUSDC"
    };

    private const decimal MinAdjustedBuyScore = 7.2m;
    private const decimal MinSymbolBiasForStandardEntry = -0.20m;
    private const decimal MinRawScoreWhenBiasNegative = 7.2m;
    private const int SuggestionTtlMinutes = 10;
    private static readonly TimeSpan RecycleCooldownAfterOperationalStop = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan IdleSlotReleaseWindow = TimeSpan.FromHours(48);

    private const int SymbolQuarantineLookbackDays = 14;
    private const int MinSellsForSymbolQuarantine = 6;
    private const int MinSellsForFastQuarantine = 3;
    private const int FastQuarantineLookbackHours = 24;
    private const decimal QuarantineAvgLossToWinRatio = 1.2m;
    private const int MaxAutoBotsHardCap = 15;
    private const decimal MinNetProfitFloor = 0.35m;
    private const int FleetKillSwitchMinSells = 15;
    private const decimal FleetKillSwitchMinProfitFactor = 1.0m;
    private const decimal FleetKillSwitchMaxNetLossUsdt = -1.0m;
    /// <summary>Ignora SELL del regimen anterior (clip a +0.50%) al evaluar el kill-switch.</summary>
    private static readonly DateTime FleetEdgeResetUtc = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    public async Task<int> CreateBotsFromSuggestionsAsync()
    {
        var now = DateTime.UtcNow;
        var settings = await settingsService.GetActiveSettingsAsync();
        var maxAutoBots = Math.Clamp(settings.MaxAutoBots, 0, MaxAutoBotsHardCap);
        var fleetKillReason = await EvaluateFleetKillSwitchAsync();
        if (fleetKillReason is not null)
        {
            maxAutoBots = 0;
        }
        else if (maxAutoBots > 0 && !backtestGate.IsLiveReady)
        {
            maxAutoBots = 0;
        }

        var quarantine = await BuildSymbolQuarantineSetAsync(now);
        var existingAutoBots = await dbContext.Bots
            .Where(x => x.IsAutoManaged)
            .ToListAsync();

        var autoBotIds = existingAutoBots.Select(x => x.Id).ToList();
        var lastTradeByBot = await dbContext.Trades
            .Where(x => autoBotIds.Contains(x.BotId))
            .GroupBy(x => x.BotId)
            .Select(g => new { BotId = g.Key, LastTradeAtUtc = g.Max(t => t.ExecutedAtUtc) })
            .ToDictionaryAsync(x => x.BotId, x => x.LastTradeAtUtc);
        var tradeCountByBot = await dbContext.Trades
            .Where(x => autoBotIds.Contains(x.BotId))
            .GroupBy(x => x.BotId)
            .Select(g => new { BotId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BotId, x => x.Count);

        foreach (var bot in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     !x.AutoResumeBlocked &&
                     x.PositionQuantity <= 0m))
        {
            var sym = bot.Symbols.FirstOrDefault() ?? string.Empty;
            if (EntryFilters.IsHardBlockedSymbol(sym, now))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    $"AutoTrader: bloqueo duro {sym} hasta {EntryFilters.SolHardBlockUntilUtc:yyyy-MM-dd} UTC (anomalia/PF reciente).";
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
                continue;
            }

            if (fleetKillReason is not null)
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = fleetKillReason;
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
                continue;
            }

            if (!EntryFilters.IsPreferredRecoverySymbol(sym))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = "AutoTrader: flota acotada a BTCUSDT/ETHUSDT (calidad sobre cantidad).";
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
                continue;
            }

            if (quarantine.Contains(sym) && !EntryFilters.IsPreferredRecoverySymbol(sym))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    "AutoTrader: bot pausado por cuarentena de simbolo (ventas recientes: PnL neto negativo o perdidas medias > 1.2x ganancias medias).";
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
            }
        }

        // Pullback 1m retirado: detener legacy sin posicion.
        foreach (var bot in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     x.StrategyType == StrategyType.Pullback))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError =
                "AutoTrader: Pullback 1m retirado; flota usa PullbackHtf (15m/1h) tras gate de backtest.";
            bot.UpdatedAtUtc = now;
            bot.OutOfTopCycles = 0;
        }

        // Solo PullbackHtf: Momentum/MeanReversion pausados.
        foreach (var bot in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     (x.StrategyType == StrategyType.Momentum || x.StrategyType == StrategyType.MeanReversion)))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError =
                "AutoTrader: Momentum/MeanReversion pausados; flota concentrada en PullbackHtf.";
            bot.UpdatedAtUtc = now;
            bot.OutOfTopCycles = 0;
        }

        // AutoPilot solo majors/mid liquidos: liberar slots ocupados por alts iliquidos.
        foreach (var bot in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     !EntryFilters.IsAutopilotAllowedSymbol(x.Symbols.FirstOrDefault() ?? string.Empty)))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError =
                "AutoTrader: simbolo fuera del universo liquido AutoPilot (majors/mid-caps).";
            bot.UpdatedAtUtc = now;
            bot.OutOfTopCycles = 0;
        }

        await dbContext.SaveChangesAsync();

        var minSuggestionTime = now.AddMinutes(-SuggestionTtlMinutes);
        var suggestions = await advisorService.GetLatestSuggestionsAsync(30);
        var symbolBias = await BuildSymbolBiasMapAsync(now);
        var candidates = suggestions
            .Where(x => x.CreatedAtUtc >= minSuggestionTime)
            .GroupBy(x => x.Symbol)
            .Select(g => g.OrderByDescending(x => x.CreatedAtUtc).First())
            .Where(x => x.Signal == "BUY" &&
                        PassesQualityGate(x, symbolBias) &&
                        EntryFilters.IsPreferredRecoverySymbol(x.Symbol) &&
                        !EntryFilters.IsHardBlockedSymbol(x.Symbol, now) &&
                        (!quarantine.Contains(x.Symbol) || EntryFilters.IsPreferredRecoverySymbol(x.Symbol)) &&
                        !AutopilotSymbolBlocklist.Contains(x.Symbol) &&
                        TradingSymbolFilters.IsTradableVolatilePair(x.Symbol) &&
                        EntryFilters.IsAutopilotAllowedSymbol(x.Symbol))
            .OrderByDescending(x => EntryFilters.IsMajorSymbol(x.Symbol) ? 1 : 0)
            .ThenByDescending(x => GetAdjustedScore(x, symbolBias))
            .ToList();

        if (candidates.Count == 0 || maxAutoBots == 0)
        {
            return 0;
        }

        var minActiveBeforePause = TimeSpan.FromMinutes(Math.Clamp(settings.MinActiveBeforePauseMinutes <= 0 ? 20 : settings.MinActiveBeforePauseMinutes, 10, 90));
        var minStoppedBeforeReactivate = TimeSpan.FromMinutes(Math.Clamp(settings.MinStoppedBeforeReactivateMinutes <= 0 ? 5 : settings.MinStoppedBeforeReactivateMinutes, 2, 30));
        var minStoppedAfterRiskMinutes = Math.Clamp(settings.MinStoppedAfterRiskStopMinutes <= 0 ? 45 : settings.MinStoppedAfterRiskStopMinutes, 15, 240);
        var outOfTopCyclesToPause = Math.Clamp(settings.RebalanceOutOfTopCycles <= 0 ? 4 : settings.RebalanceOutOfTopCycles, 2, 6);
        var target = candidates.Take(maxAutoBots).ToList();
        var targetSymbols = target.Select(x => x.Symbol).ToHashSet(StringComparer.Ordinal);

        // Liberar slots: bots sin trades en 48h+ y fuera del top del analista.
        foreach (var idle in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     !x.AutoResumeBlocked &&
                     x.PositionQuantity <= 0m))
        {
            var sym = idle.Symbols.FirstOrDefault() ?? string.Empty;
            var trades = tradeCountByBot.GetValueOrDefault(idle.Id);
            if (trades > 0 ||
                string.IsNullOrWhiteSpace(sym) ||
                targetSymbols.Contains(sym) ||
                EntryFilters.IsPreferredRecoverySymbol(sym))
            {
                continue;
            }

            var started = idle.LastRunningStartedAtUtc ?? idle.CreatedAtUtc;
            if (now - started >= IdleSlotReleaseWindow)
            {
                idle.State = BotState.Stopped;
                idle.LastExecutionError =
                    "AutoTrader: slot liberado (0 trades en 48h+, fuera del top del analista).";
                idle.UpdatedAtUtc = now;
                idle.OutOfTopCycles = 0;
            }
        }

        foreach (var running in existingAutoBots.Where(x => x.State == BotState.Running))
        {
            if (running.AutoResumeBlocked || running.PositionQuantity > 0m)
            {
                continue;
            }

            var runningSymbol = running.Symbols.FirstOrDefault() ?? string.Empty;
            var activeAge = now - (running.LastRunningStartedAtUtc ?? running.CreatedAtUtc);
            if (!string.IsNullOrWhiteSpace(runningSymbol) &&
                !EntryFilters.IsPreferredRecoverySymbol(runningSymbol) &&
                !targetSymbols.Contains(runningSymbol) &&
                activeAge >= minActiveBeforePause)
            {
                running.OutOfTopCycles++;
                if (running.OutOfTopCycles >= outOfTopCyclesToPause)
                {
                    running.State = BotState.Stopped;
                    running.LastExecutionError = $"AutoTrader: bot pausado por rebalanceo (fuera del top del analista por {outOfTopCyclesToPause} ciclos).";
                    running.UpdatedAtUtc = now;
                    running.OutOfTopCycles = 0;
                }
            }
            else
            {
                running.OutOfTopCycles = 0;
            }
        }

        var effectiveRunning = FleetCapacityHelper.CountEffectiveRunning(existingAutoBots, now, lastTradeByBot);
        var capacity = Math.Max(0, maxAutoBots - effectiveRunning);

        var createdCount = 0;
        foreach (var candidate in target)
        {
            if (quarantine.Contains(candidate.Symbol) &&
                !EntryFilters.IsPreferredRecoverySymbol(candidate.Symbol))
            {
                continue;
            }

            if (candidate.SuggestedStrategy != StrategyType.PullbackHtf)
            {
                continue;
            }

            var alreadyRunning = existingAutoBots.Any(x =>
                x.State == BotState.Running &&
                x.Symbols.Contains(candidate.Symbol));
            if (alreadyRunning)
            {
                continue;
            }

            if (capacity == 0)
            {
                break;
            }

            var htf = StrategyExitProfiles.AutoPilotParams(StrategyType.PullbackHtf);
            var strategy = StrategyType.PullbackHtf;

            var recyclable = existingAutoBots
                .Where(x => x.State == BotState.Stopped &&
                            x.PositionQuantity <= 0m &&
                            !x.AutoResumeBlocked &&
                            x.Symbols.Contains(candidate.Symbol))
                .OrderByDescending(x => x.RealizedPnlUsdt)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            if (recyclable is not null)
            {
                var stoppedAge = now - recyclable.UpdatedAtUtc;
                var requiredCooldown = ResolveRecycleCooldown(recyclable, minStoppedBeforeReactivate, minStoppedAfterRiskMinutes);
                if (stoppedAge < requiredCooldown)
                {
                    continue;
                }

                recyclable.Name = $"AutoPilot-HTF-{candidate.Symbol}";
                recyclable.BudgetUsdt = 20m;
                recyclable.MaxPositionPerTradeUsdt = 20m;
                recyclable.StopLossPercent = htf.Sl;
                recyclable.TakeProfitPercent = htf.Tp;
                recyclable.TakeProfit1Percent = htf.Tp;
                recyclable.TakeProfit1SellPercent = 0m;
                recyclable.TakeProfit2Percent = htf.Tp;
                recyclable.TrailingActivationPercent = Math.Max(htf.TrailAct, MinNetProfitFloor);
                recyclable.TrailingStopPercent = htf.TrailStop;
                recyclable.MaxHoldingMinutes = htf.MaxHold;
                recyclable.MaxDailyLossUsdt = 2m;
                recyclable.MaxExposurePercent = 100m;
                recyclable.CooldownMinutesAfterLoss = 30;
                recyclable.MaxConsecutiveLossTrades = 3;
                recyclable.Symbols = [candidate.Symbol];
                recyclable.State = BotState.Running;
                recyclable.IsAutoManaged = true;
                // Sesión de pérdida: en recovery BTC/ETH partir desde PnL actual.
                recyclable.AutoScaleReferencePnlUsdt = EntryFilters.IsPreferredRecoverySymbol(candidate.Symbol)
                    ? recyclable.RealizedPnlUsdt
                    : 0m;
                recyclable.StrategyType = strategy;
                recyclable.LastExecutionError = string.Empty;
                recyclable.ConsecutiveLossTrades = 0;
                recyclable.RollingExpectancyUsdt = 0m;
                recyclable.NegativeEdgeCycles = 0;
                recyclable.OutOfTopCycles = 0;
                recyclable.LastRunningStartedAtUtc = DateTime.UtcNow;
                recyclable.UpdatedAtUtc = DateTime.UtcNow;
                capacity--;
                createdCount++;
                continue;
            }

            var startUtc = DateTime.UtcNow;
            dbContext.Bots.Add(new TradingBot
            {
                Name = $"AutoPilot-HTF-{candidate.Symbol}",
                BudgetUsdt = 20m,
                MaxPositionPerTradeUsdt = 20m,
                StopLossPercent = htf.Sl,
                TakeProfitPercent = htf.Tp,
                TakeProfit1Percent = htf.Tp,
                TakeProfit1SellPercent = 0m,
                TakeProfit2Percent = htf.Tp,
                TrailingActivationPercent = Math.Max(htf.TrailAct, MinNetProfitFloor),
                TrailingStopPercent = htf.TrailStop,
                MaxHoldingMinutes = htf.MaxHold,
                MaxDailyLossUsdt = 2m,
                MaxExposurePercent = 100m,
                CooldownMinutesAfterLoss = 30,
                MaxConsecutiveLossTrades = 3,
                Symbols = [candidate.Symbol],
                State = BotState.Running,
                IsAutoManaged = true,
                AutoScaleReferencePnlUsdt = 0m,
                StrategyType = strategy,
                OutOfTopCycles = 0,
                LastRunningStartedAtUtc = startUtc,
                UpdatedAtUtc = startUtc
            });
            existingAutoBots.Add(new TradingBot
            {
                Symbols = [candidate.Symbol],
                IsAutoManaged = true,
                State = BotState.Running
            });
            capacity--;
            createdCount++;
        }

        await dbContext.SaveChangesAsync();
        return createdCount;
    }

    private static decimal GetAdjustedScore(InvestmentSuggestion suggestion, IReadOnlyDictionary<string, decimal> symbolBias) =>
        suggestion.Score + (symbolBias.TryGetValue(suggestion.Symbol, out var b) ? b : 0m);

    private static bool PassesQualityGate(InvestmentSuggestion suggestion, IReadOnlyDictionary<string, decimal> symbolBias)
    {
        var bias = symbolBias.TryGetValue(suggestion.Symbol, out var b) ? b : 0m;
        var adjusted = suggestion.Score + bias;
        if (adjusted < MinAdjustedBuyScore)
        {
            return false;
        }

        if (bias < MinSymbolBiasForStandardEntry && suggestion.Score < MinRawScoreWhenBiasNegative)
        {
            return false;
        }

        return true;
    }

    private async Task<string?> EvaluateFleetKillSwitchAsync()
    {
        var pnls = await dbContext.Trades
            .AsNoTracking()
            .Where(x => x.Side == "SELL" && x.ExecutedAtUtc >= FleetEdgeResetUtc)
            .OrderByDescending(x => x.ExecutedAtUtc)
            .Take(FleetKillSwitchMinSells)
            .Select(x => x.RealizedPnlUsdt)
            .ToListAsync();
        if (pnls.Count < FleetKillSwitchMinSells)
        {
            return null;
        }

        var sumWins = pnls.Where(p => p > 0m).Sum();
        var sumLossAbs = Math.Abs(pnls.Where(p => p < 0m).Sum());
        var pf = sumLossAbs <= 0m ? (sumWins > 0m ? 999m : 0m) : sumWins / sumLossAbs;
        var net = pnls.Sum();
        if (pf < FleetKillSwitchMinProfitFactor || net <= FleetKillSwitchMaxNetLossUsdt)
        {
            return
                $"AutoTrader: kill-switch flota (ultimas {pnls.Count} SELL PF={pf:0.00} neto={net:0.##} USDT; umbral PF>={FleetKillSwitchMinProfitFactor:0.##} y neto>{FleetKillSwitchMaxNetLossUsdt:0.##}).";
        }

        return null;
    }

    private async Task<Dictionary<string, decimal>> BuildSymbolBiasMapAsync(DateTime nowUtc)
    {
        var fromUtc = nowUtc.AddHours(-12);
        var sellTrades = await dbContext.Trades
            .Where(x => x.Side == "SELL" && x.ExecutedAtUtc >= fromUtc)
            .ToListAsync();

        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var group in sellTrades.GroupBy(x => x.Symbol))
        {
            var count = group.Count();
            if (count < 2)
            {
                map[group.Key] = 0m;
                continue;
            }

            var wins = group.Count(x => x.RealizedPnlUsdt > 0m);
            var winRate = wins * 1m / count;
            var net = group.Sum(x => x.RealizedPnlUsdt);
            var bias = (winRate - 0.5m) * 1.4m;
            if (winRate < 0.45m) bias -= 0.22m;
            if (net > 0m) bias += 0.15m;
            if (net < 0m) bias -= 0.45m;
            map[group.Key] = Math.Clamp(decimal.Round(bias, 4), -1.2m, 1.2m);
        }

        return map;
    }

    private async Task<HashSet<string>> BuildSymbolQuarantineSetAsync(DateTime nowUtc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bloqueo duro SOL hasta fecha fija (anomalia -4.9% y peor bot de ventana).
        if (nowUtc < EntryFilters.SolHardBlockUntilUtc)
        {
            set.Add("SOLUSDT");
            set.Add("SOLUSDC");
        }

        var fastFrom = nowUtc.AddHours(-FastQuarantineLookbackHours);
        var fastRows = await dbContext.Trades
            .AsNoTracking()
            .Where(x => x.Side == "SELL" && x.ExecutedAtUtc >= fastFrom)
            .Select(x => new { x.Symbol, x.RealizedPnlUsdt })
            .ToListAsync();
        foreach (var g in fastRows.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            // Exigir sangrado real (>=0.50 USDT), no micro-rojo que apaga toda la flota.
            if (g.Count() >= MinSellsForFastQuarantine && g.Sum(x => x.RealizedPnlUsdt) <= -0.50m)
            {
                set.Add(g.Key);
            }
        }

        var fromUtc = nowUtc.AddDays(-SymbolQuarantineLookbackDays);
        var rows = await dbContext.Trades
            .AsNoTracking()
            .Where(x => x.Side == "SELL" && x.ExecutedAtUtc >= fromUtc)
            .Select(x => new { x.Symbol, x.RealizedPnlUsdt })
            .ToListAsync();

        foreach (var g in rows.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() < MinSellsForSymbolQuarantine)
            {
                continue;
            }

            var pnls = g.Select(x => x.RealizedPnlUsdt).ToList();
            var net = pnls.Sum();
            var wins = pnls.Where(p => p > 0m).ToList();
            var losses = pnls.Where(p => p < 0m).ToList();
            var grossWins = wins.Sum();
            var grossLossAbs = Math.Abs(losses.Sum());
            var profitFactor = grossLossAbs <= 0m
                ? (grossWins > 0m ? 999m : 0m)
                : grossWins / grossLossAbs;

            var badNet = net < 0m;
            var badPf = profitFactor < 1m;
            var heavyAvgLoss = false;
            if (wins.Count >= 2 && losses.Count >= 2)
            {
                var avgWin = wins.Average();
                var avgLossAbs = Math.Abs(losses.Average());
                if (avgWin > 0m && avgLossAbs > avgWin * QuarantineAvgLossToWinRatio)
                {
                    heavyAvgLoss = true;
                }
            }

            if (badNet || badPf || heavyAvgLoss)
            {
                set.Add(g.Key);
            }
        }

        return set;
    }

    private static TimeSpan ResolveRecycleCooldown(TradingBot stoppedBot, TimeSpan configuredReactivate, int minMinutesAfterRiskStop)
    {
        if (IsOperationalRecycleStop(stoppedBot.LastExecutionError))
        {
            return TimeSpan.FromTicks(Math.Min(
                RecycleCooldownAfterOperationalStop.Ticks,
                configuredReactivate.Ticks));
        }

        if (IsRiskDrivenRecycleStop(stoppedBot))
        {
            var riskFloor = TimeSpan.FromMinutes(minMinutesAfterRiskStop);
            return configuredReactivate >= riskFloor ? configuredReactivate : riskFloor;
        }

        return configuredReactivate;
    }

    private static bool IsRiskDrivenRecycleStop(TradingBot bot)
    {
        if (IsOperationalRecycleStop(bot.LastExecutionError))
        {
            return false;
        }

        var err = bot.LastExecutionError ?? string.Empty;
        if (err.Contains("edge negativo", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("racha de perdidas", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("perdida diaria maxima", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (bot.ConsecutiveLossTrades >= Math.Max(1, bot.MaxConsecutiveLossTrades))
        {
            return true;
        }

        if (bot.MaxDailyLossUsdt > 0m &&
            EntryFilters.GetSessionRealizedPnl(bot) <= -Math.Abs(bot.MaxDailyLossUsdt))
        {
            return true;
        }

        return false;
    }

    private static bool IsOperationalRecycleStop(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return false;
        }

        if (lastError.Contains("Supervisor:", StringComparison.OrdinalIgnoreCase) &&
            lastError.Contains("inactividad", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lastError.Contains("slot liberado", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!lastError.Contains("AutoTrader:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return lastError.Contains("rebalanceo", StringComparison.OrdinalIgnoreCase) ||
               lastError.Contains("cuarentena", StringComparison.OrdinalIgnoreCase) ||
               lastError.Contains("slot liberado", StringComparison.OrdinalIgnoreCase);
    }
}
