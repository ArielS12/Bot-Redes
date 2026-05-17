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
    public async Task<BotMaintenanceResult> ConsolidateFleetAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var maxRunning = Math.Clamp(settings.MaxAutoBots, 1, 8);
        var bots = await db.Bots.OrderByDescending(x => x.State).ThenBy(x => x.Name).ToListAsync(ct);
        var botIds = bots.Select(x => x.Id).ToList();
        var tradeCounts = await db.Trades
            .Where(x => botIds.Contains(x.BotId))
            .GroupBy(x => x.BotId)
            .Select(g => new { BotId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BotId, x => x.Count, ct);

        var stopped = 0;
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

        var runningAuto = bots
            .Where(x => x.IsAutoManaged && x.State == BotState.Running && !x.AutoResumeBlocked)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToList();
        while (runningAuto.Count > maxRunning)
        {
            var victim = runningAuto
                .OrderBy(x => tradeCounts.TryGetValue(x.Id, out var tc) ? tc : 0)
                .ThenBy(x => x.UpdatedAtUtc)
                .First();
            victim.State = BotState.Stopped;
            victim.LastExecutionError = $"Mantenimiento: exceso de bots auto (max {maxRunning}).";
            victim.UpdatedAtUtc = now;
            runningAuto.Remove(victim);
            stopped++;
        }

        if (settings.MaxAutoBots > 8)
        {
            var row = await db.BinanceSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is not null)
            {
                row.MaxAutoBots = 8;
                row.UpdatedAtUtc = now;
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
                         !string.IsNullOrWhiteSpace(x.LastExecutionError) &&
                         x.LastExecutionError.Contains("Supervisor:", StringComparison.OrdinalIgnoreCase) &&
                         x.LastExecutionError.Contains("inactividad", StringComparison.OrdinalIgnoreCase) &&
                         x.Symbols.Any(TradingSymbolFilters.IsTradableVolatilePair))
                     .OrderByDescending(x => x.UpdatedAtUtc)
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
        return new BotMaintenanceResult
        {
            StoppedInactiveBots = stopped,
            ReactivatedBots = reactivated,
            RunningBotsAfter = runningAfter,
            Message = reactivated > 0
                ? $"Consolidacion: {stopped} detenido(s), {reactivated} reactivado(s) tras parada del supervisor. En ejecucion: {runningAfter} (max {maxRunning})."
                : $"Consolidacion: {stopped} bot(s) detenidos. En ejecucion: {runningAfter} (max auto {maxRunning})."
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
