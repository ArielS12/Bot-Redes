using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Parametros defensivos HTF: R:R ~1:2.3 post-fee, cortes tempranos, size reducido en AutoPilot.
/// </summary>
public static class StrategyExitProfiles
{
    public const decimal HtfQuotePerTradeUsdt = 12m;
    public const decimal HtfBudgetUsdt = 12m;
    public const decimal HtfMaxDailyLossUsdt = 0.80m;
    public const int HtfMaxConsecutiveLosses = 2;
    public const int SafeLiveMaxAutoBots = 2;

    public static decimal MinNetProfitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1.0m,
        _ => 2.0m
    };

    public static int EarlyInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 360,
        _ => 180
    };

    public static int MaxZombieHoldingMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1440,
        _ => 480
    };

    public static (decimal Sl, decimal Tp, decimal TrailAct, decimal TrailStop, int MaxHold) AutoPilotParams(
        StrategyType strategy) => strategy switch
    {
        // SL 1.2 / TP 2.8 ≈ 2.3R; trail arma en +1.4 y protege 0.7.
        StrategyType.PullbackHtf => (1.2m, 2.8m, 1.4m, 0.7m, 960),
        _ => (1.2m, 4.2m, 1.5m, 0.9m, 360)
    };
}
