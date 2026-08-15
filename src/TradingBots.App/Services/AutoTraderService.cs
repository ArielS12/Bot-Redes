using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IAutoTraderService
{
    Task<int> CreateBotsFromSuggestionsAsync();
}

public sealed class AutoTraderService(
    AppDbContext dbContext,
    IMarketAdvisorService advisorService,
    IBinanceSettingsService settingsService) : IAutoTraderService
{
    private static readonly HashSet<string> AutopilotSymbolBlocklist = new(StringComparer.Ordinal)
    {
        "UUSDT", "UUSDC"
    };

    private const decimal MinAdjustedBuyScore = 5.9m;
    private const decimal MinSymbolBiasForStandardEntry = -0.20m;
    private const decimal MinRawScoreWhenBiasNegative = 6.2m;
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
    /// <summary>SOL: anomalias y PF reciente malo — bloqueo duro temporal.</summary>
    private static readonly HashSet<string> HardBlockedBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "SOL"
    };
    private static readonly DateTime SolHardBlockUntilUtc = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

    public async Task<int> CreateBotsFromSuggestionsAsync()
    {
        var now = DateTime.UtcNow;
        var settings = await settingsService.GetActiveSettingsAsync();
        var maxAutoBots = Math.Clamp(settings.MaxAutoBots, 0, MaxAutoBotsHardCap);
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
            if (IsHardBlockedSymbol(sym, now))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    $"AutoTrader: bloqueo duro {sym} hasta {SolHardBlockUntilUtc:yyyy-MM-dd} UTC (anomalia/PF reciente).";
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
                continue;
            }

            if (quarantine.Contains(sym) && !IsPreferredRecoverySymbol(sym))
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    "AutoTrader: bot pausado por cuarentena de simbolo (ventas recientes: PnL neto negativo o perdidas medias > 1.2x ganancias medias).";
                bot.UpdatedAtUtc = now;
                bot.OutOfTopCycles = 0;
            }
        }

        // Solo Pullback: Momentum pausado tras 72h con PF post-cambio ~0.21.
        foreach (var bot in existingAutoBots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     (x.StrategyType == StrategyType.Momentum || x.StrategyType == StrategyType.MeanReversion)))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError =
                "AutoTrader: Momentum/MeanReversion pausados; flota concentrada en Pullback (edge post-fee).";
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
                        !IsHardBlockedSymbol(x.Symbol, now) &&
                        (!quarantine.Contains(x.Symbol) || IsPreferredRecoverySymbol(x.Symbol)) &&
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
            if (trades > 0 || string.IsNullOrWhiteSpace(sym) || targetSymbols.Contains(sym))
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
            if (quarantine.Contains(candidate.Symbol))
            {
                continue;
            }

            if (candidate.SuggestedStrategy != StrategyType.Pullback)
            {
                // Solo Pullback: Momentum/MeanReversion no se crean ni reciclan.
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

            const decimal sl = 1.2m;
            const decimal tp = 4.2m;
            var strategy = StrategyType.Pullback;

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

                recyclable.Name = $"AutoPilot-Pullback-{candidate.Symbol}";
                recyclable.BudgetUsdt = 20m;
                recyclable.MaxPositionPerTradeUsdt = 20m;
                recyclable.StopLossPercent = sl;
                recyclable.TakeProfitPercent = tp;
                recyclable.TakeProfit1Percent = tp;
                recyclable.TakeProfit1SellPercent = 0m;
                recyclable.TakeProfit2Percent = tp;
                recyclable.TrailingActivationPercent = Math.Max(1.5m, MinNetProfitFloor);
                recyclable.TrailingStopPercent = 0.9m;
                recyclable.MaxHoldingMinutes = 360;
                recyclable.MaxDailyLossUsdt = 2m;
                recyclable.MaxExposurePercent = 100m;
                recyclable.CooldownMinutesAfterLoss = 30;
                recyclable.MaxConsecutiveLossTrades = 3;
                recyclable.Symbols = [candidate.Symbol];
                recyclable.State = BotState.Running;
                recyclable.IsAutoManaged = true;
                recyclable.AutoScaleReferencePnlUsdt = 0m;
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
                Name = $"AutoPilot-Pullback-{candidate.Symbol}",
                BudgetUsdt = 20m,
                MaxPositionPerTradeUsdt = 20m,
                StopLossPercent = sl,
                TakeProfitPercent = tp,
                TakeProfit1Percent = tp,
                TakeProfit1SellPercent = 0m,
                TakeProfit2Percent = tp,
                TrailingActivationPercent = Math.Max(1.5m, MinNetProfitFloor),
                TrailingStopPercent = 0.9m,
                MaxHoldingMinutes = 360,
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
        if (nowUtc < SolHardBlockUntilUtc)
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

    private static bool IsPreferredRecoverySymbol(string symbol) =>
        symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase) ||
        symbol.Equals("BTCUSDC", StringComparison.OrdinalIgnoreCase) ||
        symbol.Equals("ETHUSDT", StringComparison.OrdinalIgnoreCase) ||
        symbol.Equals("ETHUSDC", StringComparison.OrdinalIgnoreCase);

    private static bool IsHardBlockedSymbol(string symbol, DateTime nowUtc)
    {
        if (nowUtc >= SolHardBlockUntilUtc)
        {
            return false;
        }

        return EntryFilters.TryGetBaseAsset(symbol, out var baseAsset) && HardBlockedBases.Contains(baseAsset);
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

        if (bot.MaxDailyLossUsdt > 0m && bot.RealizedPnlUsdt <= -Math.Abs(bot.MaxDailyLossUsdt))
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
