using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>Intervalos por estrategia: entry + confirmaciones de tendencia + contexto de salida.</summary>
public sealed record StrategyTimeframeProfile(
    StrategyType Strategy,
    string EntryInterval,
    string Tf5Interval,
    string Tf15Interval,
    string SellContextInterval)
{
    public static StrategyTimeframeProfile For(StrategyType strategy) => strategy switch
    {
        StrategyType.PullbackHtf => new StrategyTimeframeProfile(
            StrategyType.PullbackHtf, "15m", "15m", "1h", "1h"),
        StrategyType.Pullback => new StrategyTimeframeProfile(
            StrategyType.Pullback, "1m", "5m", "15m", "5m"),
        _ => new StrategyTimeframeProfile(
            StrategyType.Momentum, "1m", "5m", "15m", "5m")
    };

    public static bool RequiresHourlySnapshots(IEnumerable<StrategyType> strategies) =>
        strategies.Any(s => For(s).Tf15Interval == "1h" || For(s).EntryInterval == "1h" ||
                            For(s).SellContextInterval == "1h");
}
