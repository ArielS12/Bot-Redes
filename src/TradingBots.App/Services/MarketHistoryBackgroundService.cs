namespace TradingBots.App.Services;

public sealed class MarketHistoryBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<MarketHistoryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var botService = scope.ServiceProvider.GetRequiredService<IBotService>();
                var history = scope.ServiceProvider.GetRequiredService<IMarketHistoryService>();
                var symbols = (await botService.GetBotsAsync())
                    .SelectMany(x => x.Symbols)
                    .Concat(["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"])
                    .Where(TradingSymbolFilters.IsTradableVolatilePair)
                    .Distinct()
                    .ToList();
                await history.SyncSymbolsAsync(symbols, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error sincronizando historial de mercado");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
