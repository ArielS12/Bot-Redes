using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Parametros HTF orientados a expectancy neta post-fee (evitar winners clipados bajo el SL).
/// </summary>
public static class StrategyExitProfiles
{
    public const decimal HtfQuotePerTradeUsdt = 12m;
    public const decimal HtfBudgetUsdt = 12m;
    /// <summary>~7.5% del budget: corta el dia si el edge sangra.</summary>
    public const decimal HtfMaxDailyLossUsdt = 0.90m;
    public const int HtfMaxConsecutiveLosses = 2;
    public const int SafeLiveMaxAutoBots = 2;

    public static decimal MinNetProfitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1.0m,
        _ => 2.0m
    };

    public static int EarlyInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 480,
        _ => 180
    };

    /// <summary>Umbral de rojo para invalidacion temprana (HTF evita micro-cortes de -0.25% + fees).</summary>
    public static decimal EarlyInvalidationMinLossPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => -0.55m,
        _ => -0.25m
    };

    public static int MaxZombieHoldingMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1440,
        _ => 480
    };

    /// <summary>
    /// Floor de soft-BE tras armar trailing. Historico 0.45% era toxico:
    /// winners ~0.25% net vs SL ~1% → PF&lt;1 sistematico.
    /// </summary>
    public static decimal SoftBreakevenExitPercent(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 1.10m,
        _ => 0.50m
    };

    /// <summary>Minutos minimos en posicion antes de permitir bounce_invalidation HTF.</summary>
    public static int MinHoldBeforeBounceInvalidationMinutes(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => 120,
        _ => 0
    };

    public static (decimal Sl, decimal Tp, decimal TrailAct, decimal TrailStop, int MaxHold) AutoPilotParams(
        StrategyType strategy) => strategy switch
    {
        // SL mas ancho al ruido 15m; soft-BE/trail protegen gains reales post-fee (~1R floor).
        StrategyType.PullbackHtf => (1.35m, 3.2m, 1.8m, 0.9m, 1200),
        _ => (1.2m, 4.2m, 1.5m, 0.9m, 360)
    };
}
