namespace TradingBots.App.Models;

public sealed class BacktestRequest
{
    public string Symbol { get; set; } = "BTCUSDT";
    public StrategyType Strategy { get; set; } = StrategyType.Momentum;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public decimal StopLossPercent { get; set; } = 1.8m;
    public decimal TakeProfitPercent { get; set; } = 3.2m;
    public int MaxHoldingMinutes { get; set; } = 180;
    public decimal QuotePerTradeUsdt { get; set; } = 20m;
    public decimal TrailingActivationPercent { get; set; } = 1.2m;
    public decimal TrailingStopPercent { get; set; } = 0.8m;
}

public sealed class BacktestTradeRecord
{
    public DateTime EntryUtc { get; set; }
    public DateTime ExitUtc { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal RealizedPnlUsdt { get; set; }
    public string ExitReason { get; set; } = string.Empty;
}

public sealed class BacktestResult
{
    public string Symbol { get; set; } = string.Empty;
    public StrategyType Strategy { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public int BarsProcessed { get; set; }
    public int ClosedTrades { get; set; }
    public int WinningTrades { get; set; }
    public decimal WinRatePercent { get; set; }
    public decimal NetPnlUsdt { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal MaxDrawdownUsdt { get; set; }
    public decimal AvgTradePnlUsdt { get; set; }
    public string CohortTier { get; set; } = "ROJO";
    public string CohortReason { get; set; } = string.Empty;
    public List<BacktestTradeRecord> Trades { get; set; } = [];
}

public sealed class CohortReadinessView
{
    public bool LiveReadyBySample { get; set; }
    public int TotalClosedSells { get; set; }
    public decimal AggregateProfitFactor { get; set; }
    public decimal AggregateNetPnlUsdt { get; set; }
    public string Tier { get; set; } = "ROJO";
    public string Summary { get; set; } = string.Empty;
    public List<BotAnalyticsItem> BotAnalytics { get; set; } = [];
}

public sealed class BotMaintenanceResult
{
    public int StoppedInactiveBots { get; set; }
    public int ReactivatedBots { get; set; }
    public int RunningBotsAfter { get; set; }
    public string Message { get; set; } = string.Empty;
}
