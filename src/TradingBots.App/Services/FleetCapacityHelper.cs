using TradingBots.App.Models;

namespace TradingBots.App.Services;

/// <summary>
/// Solo los bots que realmente operan o estan en periodo de gracia consumen cupo AutoPilot.
/// Evita el deadlock de 30 slots ocupados por bots en ESPERANDO indefinido.
/// </summary>
public static class FleetCapacityHelper
{
    private static readonly TimeSpan RecentTradeWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan NewBotGracePeriod = TimeSpan.FromHours(6);

    public static bool CountsTowardAutoCapacity(
        TradingBot bot,
        DateTime nowUtc,
        IReadOnlyDictionary<Guid, DateTime> lastTradeByBot)
    {
        if (bot.State != BotState.Running || !bot.IsAutoManaged)
        {
            return false;
        }

        if (bot.PositionQuantity > 0m)
        {
            return true;
        }

        if (lastTradeByBot.TryGetValue(bot.Id, out var lastTrade) &&
            nowUtc - lastTrade < RecentTradeWindow)
        {
            return true;
        }

        var started = bot.LastRunningStartedAtUtc ?? bot.CreatedAtUtc;
        return nowUtc - started < NewBotGracePeriod;
    }

    public static int CountEffectiveRunning(
        IEnumerable<TradingBot> bots,
        DateTime nowUtc,
        IReadOnlyDictionary<Guid, DateTime> lastTradeByBot) =>
        bots.Count(b => CountsTowardAutoCapacity(b, nowUtc, lastTradeByBot));
}
