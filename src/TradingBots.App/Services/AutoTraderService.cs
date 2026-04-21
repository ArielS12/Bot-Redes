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
    private static readonly HashSet<string> StableAssets = new(StringComparer.Ordinal)
    {
        "USDT", "USDC", "FDUSD", "USD1", "BUSD", "TUSD", "USDP", "DAI"
    };

    /// <summary>Pares con historial de churn / micro-edge que no aportan a AutoPilot.</summary>
    private static readonly HashSet<string> AutopilotSymbolBlocklist = new(StringComparer.Ordinal)
    {
        "UUSDT", "UUSDC"
    };

    /// <summary>Umbral alineado con el analista (BUY mas estricto) mas sesgo por simbolo.</summary>
    private const decimal MinAdjustedBuyScore = 5.05m;

    /// <summary>Ventana alineada con el ciclo (~10s) y batches del analista; antes 5 min dejaba fuera sugerencias validas.</summary>
    private const int SuggestionTtlMinutes = 12;

    /// <summary>Tras paradas operativas (supervisor inactividad o rebalanceo fuera del top), no usar el cooldown largo de settings.</summary>
    private static readonly TimeSpan RecycleCooldownAfterOperationalStop = TimeSpan.FromSeconds(90);

    public async Task<int> CreateBotsFromSuggestionsAsync()
    {
        var now = DateTime.UtcNow;
        var minSuggestionTime = now.AddMinutes(-SuggestionTtlMinutes);
        var suggestions = await advisorService.GetLatestSuggestionsAsync(30);
        var symbolBias = await BuildSymbolBiasMapAsync(now);
        var candidates = suggestions
            .Where(x => x.CreatedAtUtc >= minSuggestionTime)
            .GroupBy(x => x.Symbol)
            .Select(g => g.OrderByDescending(x => x.CreatedAtUtc).First())
            .Where(x => x.Signal == "BUY" &&
                        GetAdjustedScore(x, symbolBias) >= MinAdjustedBuyScore &&
                        !AutopilotSymbolBlocklist.Contains(x.Symbol) &&
                        !IsStableStablePair(x.Symbol) &&
                        (x.Symbol.EndsWith("USDT", StringComparison.Ordinal) || x.Symbol.EndsWith("USDC", StringComparison.Ordinal)))
            .OrderByDescending(x => GetAdjustedScore(x, symbolBias))
            .ToList();

        if (candidates.Count == 0)
        {
            return 0;
        }

        var settings = await settingsService.GetActiveSettingsAsync();
        var maxAutoBots = Math.Clamp(settings.MaxAutoBots, 0, 50);
        if (maxAutoBots == 0)
        {
            return 0;
        }
        var minActiveBeforePause = TimeSpan.FromMinutes(Math.Clamp(settings.MinActiveBeforePauseMinutes <= 0 ? 20 : settings.MinActiveBeforePauseMinutes, 10, 90));
        var minStoppedBeforeReactivate = TimeSpan.FromMinutes(Math.Clamp(settings.MinStoppedBeforeReactivateMinutes <= 0 ? 5 : settings.MinStoppedBeforeReactivateMinutes, 2, 30));
        var minStoppedAfterRiskMinutes = Math.Clamp(settings.MinStoppedAfterRiskStopMinutes <= 0 ? 45 : settings.MinStoppedAfterRiskStopMinutes, 15, 240);
        var outOfTopCyclesToPause = Math.Clamp(settings.RebalanceOutOfTopCycles <= 0 ? 3 : settings.RebalanceOutOfTopCycles, 2, 6);
        var existingAutoBots = await dbContext.Bots
            .Where(x => x.IsAutoManaged)
            .ToListAsync();
        var target = candidates
            .Take(maxAutoBots)
            .ToList();
        var targetSymbols = target.Select(x => x.Symbol).ToHashSet(StringComparer.Ordinal);

        // Rebalanceo continuo: pausa bots auto sin posicion que ya no esten en el top actual del analista.
        foreach (var running in existingAutoBots.Where(x => x.State == BotState.Running))
        {
            if (running.AutoResumeBlocked)
            {
                continue;
            }

            if (running.PositionQuantity > 0m)
            {
                continue;
            }

            var runningSymbol = running.Symbols.FirstOrDefault() ?? string.Empty;
            var activeAge = now - running.UpdatedAtUtc;
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

        var runningAutoBots = existingAutoBots.Count(x => x.State == BotState.Running);
        var capacity = Math.Max(0, maxAutoBots - runningAutoBots);

        var createdCount = 0;
        foreach (var candidate in target)
        {
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

            // SL algo mas ajustado y TP mayor (mejor R:R frente a perdidas medias grandes).
            var (sl, tp) = candidate.SuggestedStrategy == StrategyType.Momentum
                ? (1.85m, 5.2m)
                : (1.55m, 4.0m);

            var recyclable = existingAutoBots
                .Where(x => x.State == BotState.Stopped &&
                            x.PositionQuantity <= 0m &&
                            !x.AutoResumeBlocked &&
                            x.Symbols.Contains(candidate.Symbol))
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            if (recyclable is not null)
            {
                var stoppedAge = now - recyclable.UpdatedAtUtc;
                var requiredCooldown = ResolveRecycleCooldown(recyclable, minStoppedBeforeReactivate, minStoppedAfterRiskMinutes);
                if (stoppedAge < requiredCooldown)
                {
                    continue;
                }

                recyclable.Name = $"AutoPilot-{candidate.SuggestedStrategy}-{candidate.Symbol}";
                recyclable.BudgetUsdt = 20m;
                recyclable.MaxPositionPerTradeUsdt = 20m;
                recyclable.StopLossPercent = sl;
                recyclable.TakeProfitPercent = tp;
                recyclable.MaxDailyLossUsdt = 4m;
                recyclable.MaxExposurePercent = 100m;
                recyclable.CooldownMinutesAfterLoss = 5;
                recyclable.MaxConsecutiveLossTrades = 5;
                recyclable.Symbols = [candidate.Symbol];
                recyclable.State = BotState.Running;
                recyclable.IsAutoManaged = true;
                recyclable.AutoScaleReferencePnlUsdt = 0m;
                recyclable.StrategyType = candidate.SuggestedStrategy;
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
                Name = $"AutoPilot-{candidate.SuggestedStrategy}-{candidate.Symbol}",
                BudgetUsdt = 20m,
                MaxPositionPerTradeUsdt = 20m,
                StopLossPercent = sl,
                TakeProfitPercent = tp,
                MaxDailyLossUsdt = 4m,
                MaxExposurePercent = 100m,
                CooldownMinutesAfterLoss = 5,
                MaxConsecutiveLossTrades = 5,
                Symbols = [candidate.Symbol],
                State = BotState.Running,
                IsAutoManaged = true,
                AutoScaleReferencePnlUsdt = 0m,
                StrategyType = candidate.SuggestedStrategy,
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

    private static bool IsStableStablePair(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var upper = symbol.Trim().ToUpperInvariant();
        string? quote = null;
        if (upper.EndsWith("USDT", StringComparison.Ordinal)) quote = "USDT";
        else if (upper.EndsWith("USDC", StringComparison.Ordinal)) quote = "USDC";
        else if (upper.EndsWith("FDUSD", StringComparison.Ordinal)) quote = "FDUSD";
        else if (upper.EndsWith("BUSD", StringComparison.Ordinal)) quote = "BUSD";
        if (quote is null) return false;

        var baseAsset = upper[..^quote.Length];
        return StableAssets.Contains(baseAsset) && StableAssets.Contains(quote);
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
            var winRate = wins * 1m / count; // 0..1
            var net = group.Sum(x => x.RealizedPnlUsdt);
            var bias = (winRate - 0.5m) * 1.4m;
            if (winRate < 0.45m)
            {
                bias -= 0.22m;
            }

            if (net > 0m) bias += 0.15m;
            if (net < 0m) bias -= 0.45m;
            map[group.Key] = Math.Clamp(decimal.Round(bias, 4), -1.2m, 1.2m);
        }

        return map;
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

    /// <summary>Parada por perdidas, limite diario o edge negativo: esperar mas antes de reutilizar el slot AutoPilot.</summary>
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

    /// <summary>Paradas por inactividad del supervisor o rebalanceo (no riesgo de trade): reciclar rapido.</summary>
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

        return lastError.Contains("AutoTrader:", StringComparison.OrdinalIgnoreCase) &&
               lastError.Contains("rebalanceo", StringComparison.OrdinalIgnoreCase);
    }
}
