using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBotSupervisorService
{
    Task<int> StopInactiveAutoBotsAsync(CancellationToken ct = default);
}

public sealed class BotSupervisorService(
    AppDbContext dbContext,
    IBinanceSettingsService settingsService,
    IMarketAdvisorService advisorService,
    ILogger<BotSupervisorService> logger) : IBotSupervisorService
{
    public async Task<int> StopInactiveAutoBotsAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (settings.MaxAutoBots <= 0)
        {
            return 0;
        }

        var inactiveMinutes = Math.Clamp(settings.SupervisorInactiveMinutes <= 0 ? 180 : settings.SupervisorInactiveMinutes, 60, 300);
        var inactiveWindow = TimeSpan.FromMinutes(inactiveMinutes);
        var neverTradedStop = TimeSpan.FromHours(Math.Clamp(settings.NeverTradedStopHours <= 0 ? 36 : settings.NeverTradedStopHours, 12, 168));
        var now = DateTime.UtcNow;
        var recentAdvisorBuys = await BuildRecentBuySymbolsAsync(now);
        var candidates = await dbContext.Bots
            .Where(x => x.IsAutoManaged && x.State == BotState.Running && !x.AutoResumeBlocked)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var botIds = candidates.Select(x => x.Id).ToList();
        var lastTradeByBot = await dbContext.Trades
            .Where(x => botIds.Contains(x.BotId))
            .GroupBy(x => x.BotId)
            .Select(g => new { BotId = g.Key, LastTradeAtUtc = g.Max(t => t.ExecutedAtUtc) })
            .ToDictionaryAsync(x => x.BotId, x => x.LastTradeAtUtc, ct);

        var stopped = 0;
        foreach (var bot in candidates)
        {
            if (bot.PositionQuantity > 0m)
            {
                continue;
            }

            var sym = bot.Symbols.FirstOrDefault() ?? string.Empty;

            // Flota recovery BTC/ETH: no cortar por inactividad histórica al reactivar.
            if (EntryFilters.IsPreferredRecoverySymbol(sym))
            {
                continue;
            }

            if (!lastTradeByBot.ContainsKey(bot.Id))
            {
                var started = bot.LastRunningStartedAtUtc ?? bot.CreatedAtUtc;
                if (now - started < neverTradedStop)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(sym) && recentAdvisorBuys.Contains(sym))
                {
                    continue;
                }

                bot.State = BotState.Stopped;
                bot.LastExecutionError =
                    $"Supervisor: bot auto detenido por {neverTradedStop.TotalHours:0}h sin ningun trade (liberando slot).";
                bot.UpdatedAtUtc = now;
                stopped++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sym) && recentAdvisorBuys.Contains(sym))
            {
                continue;
            }

            var referenceUtc = lastTradeByBot[bot.Id];
            if (now - referenceUtc < inactiveWindow)
            {
                continue;
            }

            bot.State = BotState.Stopped;
            bot.LastExecutionError = $"Supervisor: bot auto detenido por inactividad > {inactiveWindow.TotalMinutes:0} min (sin BUY/SELL).";
            bot.UpdatedAtUtc = now;
            stopped++;
        }

        if (stopped > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Supervisor detuvo {StoppedCount} bots auto por inactividad.", stopped);
        }

        return stopped;
    }

    private async Task<HashSet<string>> BuildRecentBuySymbolsAsync(DateTime nowUtc)
    {
        var fresh = await advisorService.GetLatestSuggestionsAsync(48);
        var cutoff = nowUtc.AddMinutes(-30);
        return fresh
            .Where(x => x.Signal.Equals("BUY", StringComparison.OrdinalIgnoreCase) && x.CreatedAtUtc >= cutoff)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.Ordinal);
    }
}
