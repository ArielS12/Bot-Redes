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

    public static decimal SoftBreakevenExitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 0.45m,
        _ => 0.25m
    };

    /// <summary>Minutos minimos en posicion antes de permitir bounce_invalidation HTF.</summary>
    public static int MinHoldBeforeBounceInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 90,
        _ => 0
    };

    public static (decimal Sl, decimal Tp, decimal TrailAct, decimal TrailStop, int MaxHold) AutoPilotParams(
        StrategyType strategy) => strategy switch
    {
        // SL 1.0 / TP 3.0 ≈ 3R; trail arma en +1.6.
        StrategyType.PullbackHtf => (1.0m, 3.0m, 1.6m, 0.8m, 1200),
        _ => (1.2m, 4.2m, 1.5m, 0.9m, 360)
    };
}
