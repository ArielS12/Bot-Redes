using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

public interface IStrategySignalRegistry
{
    IStrategySignalProvider Get(StrategyType strategy);
}

public sealed class StrategySignalRegistry : IStrategySignalRegistry
{
    private readonly IReadOnlyDictionary<StrategyType, IStrategySignalProvider> _providers;

    public StrategySignalRegistry(IEnumerable<IStrategySignalProvider> providers)
    {
        _providers = providers.ToDictionary(x => x.Strategy);
    }

    public IStrategySignalProvider Get(StrategyType strategy) =>
        _providers.TryGetValue(strategy, out var provider)
            ? provider
            : _providers[StrategyType.Momentum];
}
