using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>Parametros de salida dependientes de estrategia (HTF = holds mas largos).</summary>
public static class StrategyExitProfiles
{
    public static decimal MinNetProfitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1.5m,
        _ => 2.0m
    };

    public static int EarlyInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 720,
        _ => 180
    };

    public static int MaxZombieHoldingMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 2880,
        _ => 480
    };

    public static (decimal Sl, decimal Tp, decimal TrailAct, decimal TrailStop, int MaxHold) AutoPilotParams(
        StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => (1.8m, 3.5m, 2.0m, 1.0m, 1440),
        _ => (1.2m, 4.2m, 1.5m, 0.9m, 360)
    };
}
