using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public static class StrategySnapshotHelper
{
    public static TechnicalMarketSnapshot? Resolve(
        string interval,
        string symbol,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> m1,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> m5,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> m15,
        IReadOnlyDictionary<string, TechnicalMarketSnapshot> m1h) =>
        interval switch
        {
            "1m" => m1.GetValueOrDefault(symbol),
            "5m" => m5.GetValueOrDefault(symbol),
            "15m" => m15.GetValueOrDefault(symbol),
            "1h" => m1h.GetValueOrDefault(symbol),
            _ => null
        };
}
