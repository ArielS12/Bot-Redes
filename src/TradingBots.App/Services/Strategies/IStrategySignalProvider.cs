using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public interface IStrategySignalProvider
{
    StrategyType Strategy { get; }

    bool ShouldBuy(TechnicalMarketSnapshot technical);

    bool ShouldSell(TechnicalMarketSnapshot technical);

    decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical);

    bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf5, TechnicalMarketSnapshot tf15);

    string DescribeBuySignalGap(TechnicalMarketSnapshot technical);

    string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker);

    bool PassesShortRegimeFilter(TechnicalMarketSnapshot technical, MarketTicker ticker);

    string? DescribeLongTermRegimeFailure(LongTermRegimeSnapshot? regime);

    bool PassesLongTermRegime(LongTermRegimeSnapshot? regime);
}
