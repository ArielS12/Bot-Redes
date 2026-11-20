using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Perfil HTF orientado a expectancy: dejar correr winners (trail amplio),
/// cortar losers en 1R, parcial en 1.5R. Soft-BE desactivado (destruye edge en trend-following).
/// </summary>
public static class StrategyExitProfiles
{
    public const decimal HtfQuotePerTradeUsdt = 12m;
    public const decimal HtfBudgetUsdt = 12m;
    /// <summary>~7.5% del budget diario: corta el dia si sangra.</summary>
    public const decimal HtfMaxDailyLossUsdt = 0.90m;
    public const int HtfMaxConsecutiveLosses = 2;
    public const int SafeLiveMaxAutoBots = 2;

    /// <summary>TP1 = 1.5R con venta parcial 50%; el runner va a trail/TP2.</summary>
    public const decimal HtfTakeProfit1Percent = 2.25m;
    public const decimal HtfTakeProfit1SellPercent = 50m;

    public static decimal MinNetProfitPercent(StrategyType strategy) => strategy switch
    {
        // Alineado a ~1R para armar trail / no llamar "winner" a micro-rebotes.
        StrategyType.PullbackHtf => 1.50m,
        _ => 2.0m
    };

    public static int EarlyInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 600,
        _ => 180
    };

    public static decimal EarlyInvalidationMinLossPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => -0.75m,
        _ => -0.25m
    };

    public static int MaxZombieHoldingMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 2160, // 36h
        _ => 480
    };

    /// <summary>Soft-BE de salida a profit fijo: OFF en HTF (clipa runners y empeora PF).</summary>
    public static bool SoftBreakevenEnabled(StrategyType strategy) => strategy != StrategyType.PullbackHtf;

    public static decimal SoftBreakevenExitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 999m, // desactivado
        _ => 0.50m
    };

    /// <summary>Tras MFE >= este multiplo de R, el stop pasa a scratch (fees).</summary>
    public static decimal HardBreakevenArmRMultiple(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1.0m,
        _ => 0.70m
    };

    public static int MinHoldBeforeBounceInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 180,
        _ => 0
    };

    /// <summary>
    /// SL 1.5 / TP 4.5 = 3R; trail arma en 1R (1.5) con distancia amplia 1.2.
    /// </summary>
    public static (decimal Sl, decimal Tp, decimal TrailAct, decimal TrailStop, int MaxHold) AutoPilotParams(
        StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => (1.50m, 4.50m, 1.50m, 1.20m, 1440),
        _ => (1.2m, 4.2m, 1.5m, 0.9m, 360)
    };
}
