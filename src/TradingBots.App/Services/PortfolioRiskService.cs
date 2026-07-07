using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public sealed class PortfolioRiskVerdict
{
    public bool Allowed { get; init; } = true;
    public string Reason { get; init; } = string.Empty;
}

public interface IPortfolioRiskService
{
    Task<PortfolioRiskVerdict> EvaluateNewBuyAsync(
        string symbol,
        IReadOnlyDictionary<string, MarketTicker> marketSnapshot,
        CancellationToken ct = default);

    Task<decimal> GetTodayRealizedPnlAsync(CancellationToken ct = default);
}

public sealed class PortfolioRiskService(
    AppDbContext dbContext,
    IBinanceSettingsService settingsService) : IPortfolioRiskService
{
    private static readonly HashSet<string> BtcSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT", "BTCUSDC"
    };

    public async Task<decimal> GetTodayRealizedPnlAsync(CancellationToken ct = default)
    {
        var todayStart = DateTime.UtcNow.Date;
        return await dbContext.Trades
            .Where(x => x.Side == "SELL" && x.ExecutedAtUtc >= todayStart)
            .SumAsync(x => x.RealizedPnlUsdt, ct);
    }

    public async Task<PortfolioRiskVerdict> EvaluateNewBuyAsync(
        string symbol,
        IReadOnlyDictionary<string, MarketTicker> marketSnapshot,
        CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var globalMaxLoss = settings.GlobalMaxDailyLossUsdt <= 0m ? 15m : settings.GlobalMaxDailyLossUsdt;
        var btcGate = settings.BtcCrashGatePercent <= 0m ? 3m : settings.BtcCrashGatePercent;
        var maxAlts = settings.MaxConcurrentAltPositions <= 0 ? 4 : settings.MaxConcurrentAltPositions;

        var dailyPnl = await GetTodayRealizedPnlAsync(ct);
        if (dailyPnl <= -Math.Abs(globalMaxLoss))
        {
            return new PortfolioRiskVerdict
            {
                Allowed = false,
                Reason = $"PnL gate global: perdida diaria {dailyPnl:0.00} USDT (limite -{globalMaxLoss:0.##})."
            };
        }

        if (!IsBtcSymbol(symbol) && TryGetBtcTicker(marketSnapshot, out var btc) &&
            btc.PriceChangePercent24h <= -btcGate)
        {
            return new PortfolioRiskVerdict
            {
                Allowed = false,
                Reason = $"BTC crash gate: BTC 24h {btc.PriceChangePercent24h:0.##}% (umbral -{btcGate:0.#}%)."
            };
        }

        if (!IsBtcSymbol(symbol) && !EntryFilters.IsMajorSymbol(symbol))
        {
            var openBots = await dbContext.Bots
                .AsNoTracking()
                .Where(x => x.State == BotState.Running && x.PositionQuantity > 0m)
                .ToListAsync(ct);
            var openAlts = openBots.Count(b =>
                b.Symbols.Count > 0 &&
                b.Symbols.All(s => !EntryFilters.IsMajorSymbol(s) && !IsBtcSymbol(s)));
            if (openAlts >= maxAlts)
            {
                return new PortfolioRiskVerdict
                {
                    Allowed = false,
                    Reason = $"Limite de exposicion en alts: {openAlts}/{maxAlts} posiciones abiertas."
                };
            }
        }

        return new PortfolioRiskVerdict { Allowed = true };
    }

    private static bool IsBtcSymbol(string symbol) =>
        BtcSymbols.Contains(symbol) || symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetBtcTicker(IReadOnlyDictionary<string, MarketTicker> market, out MarketTicker ticker)
    {
        if (market.TryGetValue("BTCUSDT", out ticker!))
        {
            return true;
        }

        if (market.TryGetValue("BTCUSDC", out ticker!))
        {
            return true;
        }

        ticker = null!;
        return false;
    }
}
