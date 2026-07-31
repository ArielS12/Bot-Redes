namespace TradingBots.App.Services.Strategies;

public static class StrategySignalConstants
{
    internal const decimal PullbackMaxAbsChange24hPercent = 15m;
    internal const decimal PullbackMaxEmaSpreadPercentOfPrice = 2.8m;
    internal const decimal MomentumMaxEmaSpreadPercentOfPrice = 1.0m;
    internal const decimal MomentumMaxAbsChange24hPercentForEntry = 6m;
    internal const decimal MomentumMaxRsiOnStrongDailyMove = 60m;
    internal const decimal MaxAtrPercentForEntry = 3.0m;
    internal const decimal MaxVolatilityPercentForEntry = 1.8m;
    internal const decimal MinTrendSpreadPercentForEntry = 0.03m;

    internal const decimal LongTermMomentumMaxPricePercentile90d = 92m;
    internal const decimal LongTermPullbackMaxPricePercentile90d = 92m;
    internal const decimal LongTermMaxAtrPercentileVsYear = 90m;
}
