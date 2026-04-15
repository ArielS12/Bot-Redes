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
    ILogger<BotSupervisorService> logger) : IBotSupervisorService
{
    public async Task<int> StopInactiveAutoBotsAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (settings.MaxAutoBots <= 0)
        {
            return 0;
        }

        var inactiveMinutes = Math.Clamp(settings.SupervisorInactiveMinutes <= 0 ? 120 : settings.SupervisorInactiveMinutes, 60, 240);
        var inactiveWindow = TimeSpan.FromMinutes(inactiveMinutes);
        var now = DateTime.UtcNow;
        var candidates = await dbContext.Bots
            .Where(x => x.IsAutoManaged && x.State == BotState.Running)
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

            var referenceUtc = lastTradeByBot.TryGetValue(bot.Id, out var lastTradeAtUtc)
                ? lastTradeAtUtc
                : bot.CreatedAtUtc;

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
}
