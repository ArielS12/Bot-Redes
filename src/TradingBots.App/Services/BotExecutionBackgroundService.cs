using TradingBots.App.Services;

namespace TradingBots.App.Services;

public sealed class BotExecutionBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<BotExecutionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var marketService = scope.ServiceProvider.GetRequiredService<IBinanceMarketService>();
                var botService = scope.ServiceProvider.GetRequiredService<IBotService>();
                var advisorService = scope.ServiceProvider.GetRequiredService<IMarketAdvisorService>();
                var autoTraderService = scope.ServiceProvider.GetRequiredService<IAutoTraderService>();
                var supervisorService = scope.ServiceProvider.GetRequiredService<IBotSupervisorService>();
                var controlAutotuneService = scope.ServiceProvider.GetRequiredService<IControlAutotuneService>();
                var runtimeStatus = scope.ServiceProvider.GetRequiredService<IRuntimeStatusService>();
                var symbols = (await botService.GetBotsAsync()).SelectMany(x => x.Symbols).Distinct().ToList();
                if (symbols.Count == 0)
                {
                    symbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"];
                }
                var marketData = await marketService.GetMarketOverviewAsync(symbols);
                await botService.TickBotsAsync(marketData.ToDictionary(x => x.Symbol, x => x));
                var fullMarketData = await marketService.GetMarketOverviewAsync(["*"]);
                await advisorService.AnalyzeMarketAsync(fullMarketData);
                var tuneStatus = await controlAutotuneService.TuneAsync(stoppingToken);
                var stoppedBySupervisor = await supervisorService.StopInactiveAutoBotsAsync(stoppingToken);
                var created = await autoTraderService.CreateBotsFromSuggestionsAsync();
                runtimeStatus.MarkAutoTraderRun($"Auto-creator OK. Bots creados: {created}. Supervisor inactivos: {stoppedBySupervisor}. AutoTune: {tuneStatus}");
            }
            catch (Exception ex)
            {
                using var errorScope = serviceProvider.CreateScope();
                var runtimeStatus = errorScope.ServiceProvider.GetRequiredService<IRuntimeStatusService>();
                runtimeStatus.MarkAutoTraderRun($"Error: {ex.GetType().Name}");
                logger.LogError(ex, "Error en ciclo de ejecucion de bots");
            }
        }
    }
}
