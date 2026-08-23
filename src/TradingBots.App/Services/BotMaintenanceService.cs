using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBotMaintenanceService
{
    Task<BotMaintenanceResult> ConsolidateFleetAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BotRegimeStatusItem>> GetBotRegimeStatusAsync(CancellationToken ct = default);
}

public sealed class BotMaintenanceService(
    AppDbContext db,
    IMarketHistoryService marketHistory,
    IBinanceSettingsService settingsService) : IBotMaintenanceService
{
    private const int MaxAutoBotsHardCap = 15;
    private const int DefaultMaxRunning = 6;
    private const int TargetMaxTotalBots = 30;

    public async Task<BotMaintenanceResult> ConsolidateFleetAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var haltFleet = settings.MaxAutoBots <= 0;
        var maxRunning = haltFleet ? 0 : Math.Clamp(settings.MaxAutoBots, 1, MaxAutoBotsHardCap);
        var bots = await db.Bots.OrderByDescending(x => x.State).ThenBy(x => x.Name).ToListAsync(ct);
        var botIds = bots.Select(x => x.Id).ToList();
        var tradeCounts = await db.Trades
            .Where(x => botIds.Contains(x.BotId))
            .GroupBy(x => x.BotId)
            .Select(g => new { BotId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BotId, x => x.Count, ct);
        var recentTradeCutoff = DateTime.UtcNow.AddDays(-7);
        var recentTradeBotIds = await db.Trades
            .Where(x => botIds.Contains(x.BotId) && x.ExecutedAtUtc >= recentTradeCutoff)
            .Select(x => x.BotId)
            .Distinct()
            .ToListAsync(ct);
        var recentSet = recentTradeBotIds.ToHashSet();

        var stopped = 0;
        var pruned = 0;
        var now = DateTime.UtcNow;
        foreach (var bot in bots.Where(x => x.State == BotState.Running))
        {
            var trades = tradeCounts.TryGetValue(bot.Id, out var c) ? c : 0;
            var inactive = trades == 0 &&
                           bot.LastRunningStartedAtUtc is not null &&
                           now - bot.LastRunningStartedAtUtc.Value > TimeSpan.FromDays(14);
            if (inactive)
            {
                bot.State = BotState.Stopped;
                bot.LastExecutionError = "Mantenimiento: detenido por 0 trades en 14+ dias (consolidacion de flota).";
                bot.UpdatedAtUtc = now;
                stopped++;
            }
        }

        // Solo PullbackHtf en flota activa: detener legacy 1m y otras estrategias sin posicion.
        foreach (var bot in bots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     x.StrategyType != StrategyType.PullbackHtf))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Mantenimiento: solo PullbackHtf activo; otras estrategias detenidas.";
            bot.UpdatedAtUtc = now;
            stopped++;
        }

        // Fuera del universo liquido AutoPilot.
        foreach (var bot in bots.Where(x =>
                     x.State == BotState.Running &&
                     x.PositionQuantity <= 0m &&
                     !EntryFilters.IsAutopilotAllowedSymbol(x.Symbols.FirstOrDefault() ?? string.Empty)))
        {
            bot.State = BotState.Stopped;
            bot.LastExecutionError = "Mantenimiento: simbolo fuera del universo liquido AutoPilot.";
            bot.UpdatedAtUtc = now;
            stopped++;
        }

        var runningAuto = bots
            .Where(x => x.IsAutoManaged && x.State == BotState.Running && !x.AutoResumeBlocked)
            .OrderByDescending(x => x.RealizedPnlUsdt)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToList();
        while (runningAuto.Count > maxRunning)
        {
            var victim = runningAuto
                .OrderBy(x => x.RealizedPnlUsdt)
                .ThenBy(x => tradeCounts.TryGetValue(x.Id, out var tc) ? tc : 0)
                .ThenBy(x => x.UpdatedAtUtc)
                .First();
            if (victim.PositionQuantity > 0m)
            {
                runningAuto.Remove(victim);
                if (runningAuto.Count <= maxRunning)
                {
                    break;
                }

                continue;
            }

            victim.State = BotState.Stopped;
            victim.LastExecutionError = $"Mantenimiento: exceso de bots auto (max {maxRunning}).";
            victim.UpdatedAtUtc = now;
            runningAuto.Remove(victim);
            stopped++;
        }

        if (settings.MaxAutoBots > MaxAutoBotsHardCap || settings.MaxAutoBots > DefaultMaxRunning)
        {
            var row = await db.BinanceSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is not null && row.MaxAutoBots != DefaultMaxRunning)
            {
                row.MaxAutoBots = DefaultMaxRunning;
                row.UpdatedAtUtc = now;
            }
        }

        // Podar bots detenidos por rebalanceo/inactividad sin trades recientes hasta TargetMaxTotalBots.
        var total = bots.Count;
        if (total > TargetMaxTotalBots)
        {
            var pruneCandidates = bots
                .Where(x =>
                    x.IsAutoManaged &&
                    x.State == BotState.Stopped &&
                    x.PositionQuantity <= 0m &&
                    !x.AutoResumeBlocked &&
                    !recentSet.Contains(x.Id) &&
                    (x.StrategyType != StrategyType.PullbackHtf ||
                     !EntryFilters.IsAutopilotAllowedSymbol(x.Symbols.FirstOrDefault() ?? string.Empty) ||
                     x.LastExecutionError.Contains("rebalanceo", StringComparison.OrdinalIgnoreCase) ||
                     x.LastExecutionError.Contains("inactividad", StringComparison.OrdinalIgnoreCase) ||
                     x.LastExecutionError.Contains("Mantenimiento:", StringComparison.OrdinalIgnoreCase) ||
                     x.LastExecutionError.Contains("slot liberado", StringComparison.OrdinalIgnoreCase) ||
                     x.LastExecutionError.Contains("universo liquido", StringComparison.OrdinalIgnoreCase) ||
                     x.LastExecutionError.Contains("Momentum/MeanReversion", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => EntryFilters.IsAutopilotAllowedSymbol(x.Symbols.FirstOrDefault() ?? string.Empty) ? 1 : 0)
                .ThenBy(x => x.StrategyType == StrategyType.PullbackHtf ? 1 : 0)
                .ThenBy(x => x.RealizedPnlUsdt)
                .ThenBy(x => x.UpdatedAtUtc)
                .Take(total - TargetMaxTotalBots)
                .ToList();

            if (pruneCandidates.Count > 0)
            {
                db.Bots.RemoveRange(pruneCandidates);
                pruned = pruneCandidates.Count;
                foreach (var p in pruneCandidates)
                {
                    bots.Remove(p);
                }
            }
        }

        var reactivated = 0;
        var runningNow = bots.Count(x => x.State == BotState.Running);
        var slots = Math.Max(0, maxRunning - runningNow);
        if (slots > 0)
        {
            foreach (var bot in bots.Where(x =>
                         x.State == BotState.Stopped &&
                         x.IsAutoManaged &&
                         !x.AutoResumeBlocked &&
                         x.PositionQuantity <= 0m &&
                         x.StrategyType == StrategyType.PullbackHtf &&
                         EntryFilters.IsAutopilotAllowedSymbol(x.Symbols.FirstOrDefault() ?? string.Empty) &&
                         x.RealizedPnlUsdt > 0m &&
                         x.Symbols.Any(TradingSymbolFilters.IsTradableVolatilePair))
                     .OrderByDescending(x => EntryFilters.IsMajorSymbol(x.Symbols.FirstOrDefault() ?? string.Empty) ? 1 : 0)
                     .ThenByDescending(x => x.RealizedPnlUsdt)
                     .ThenByDescending(x => x.UpdatedAtUtc)
                     .Take(slots))
            {
                bot.State = BotState.Running;
                bot.LastRunningStartedAtUtc = now;
                bot.LastExecutionError = string.Empty;
                bot.UpdatedAtUtc = now;
                reactivated++;
            }
        }

        await db.SaveChangesAsync(ct);
        var runningAfter = await db.Bots.CountAsync(x => x.State == BotState.Running, ct);
        var totalAfter = await db.Bots.CountAsync(ct);
        return new BotMaintenanceResult
        {
            StoppedInactiveBots = stopped,
            PrunedBots = pruned,
            ReactivatedBots = reactivated,
            RunningBotsAfter = runningAfter,
            TotalBotsAfter = totalAfter,
            Message =
                $"Consolidacion: {stopped} detenido(s), {pruned} podado(s), {reactivated} reactivado(s). " +
                $"En ejecucion: {runningAfter}/{maxRunning}. Total bots: {totalAfter}."
        };
    }

    public async Task<IReadOnlyList<BotRegimeStatusItem>> GetBotRegimeStatusAsync(CancellationToken ct = default)
    {
        var bots = await db.Bots.AsNoTracking().ToListAsync(ct);
        var symbols = bots.SelectMany(x => x.Symbols).Distinct().ToList();
        var regimes = await marketHistory.GetRegimesAsync(symbols, ct);
        var list = new List<BotRegimeStatusItem>();
        foreach (var bot in bots)
        {
            var sym = bot.Symbols.FirstOrDefault() ?? string.Empty;
            regimes.TryGetValue(sym, out var r);
            list.Add(new BotRegimeStatusItem
            {
                BotId = bot.Id,
                BotName = bot.Name,
                Symbol = sym,
                HasRegimeData = r?.HasData ?? false,
                PricePercentile90d = r?.PricePercentileIn90d ?? 0m,
                DailyTrendUp = r?.DailyTrendUp ?? false,
                DailyAtrPercentile = r?.DailyAtrPercentileVsYear ?? 0m,
                RegimeSummary = r is null || !r.HasData
                    ? "Sin historial D1"
                    : $"D1 {(r.DailyTrendUp ? "alcista" : "bajista")}, pct90d={r.PricePercentileIn90d:0.#}, ATR%={r.DailyAtrPercentileVsYear:0.#}"
            });
        }

        return list;
    }
}
