namespace TradingBots.App.Services.Strategies;

public static class StrategySignalConstants
{
    internal const decimal PullbackMaxAbsChange24hPercent = 18m;
    internal const decimal PullbackMaxEmaSpreadPercentOfPrice = 3.5m;
    internal const decimal MomentumMaxEmaSpreadPercentOfPrice = 1.0m;
    internal const decimal MomentumMaxAbsChange24hPercentForEntry = 6m;
    internal const decimal MomentumMaxRsiOnStrongDailyMove = 60m;
    internal const decimal MaxAtrPercentForEntry = 3.5m;
    internal const decimal MaxVolatilityPercentForEntry = 2.5m;
    internal const decimal MinTrendSpreadPercentForEntry = 0.03m;

    internal const decimal LongTermMomentumMaxPricePercentile90d = 92m;
    internal const decimal LongTermPullbackMaxPricePercentile90d = 95m;
    internal const decimal LongTermMaxAtrPercentileVsYear = 95m;
}
