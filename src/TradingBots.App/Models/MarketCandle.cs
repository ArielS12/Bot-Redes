namespace TradingBots.App.Models;

public sealed class MarketCandle
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = "1d";
    public DateTime OpenTimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal QuoteVolume { get; set; }
}

/// <summary>Contexto de regimen calculado desde velas 1d/1h persistidas.</summary>
public sealed class LongTermRegimeSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public bool HasData { get; set; }
    public decimal LastClose { get; set; }
    public decimal DailyEma50 { get; set; }
    public decimal DailyEma200 { get; set; }
    public decimal PricePercentileIn90d { get; set; }
    public decimal DailyAtrPercent { get; set; }
    public decimal DailyAtrPercentileVsYear { get; set; }
    public bool DailyTrendUp { get; set; }
    public DateTime? LastDailyOpenUtc { get; set; }
}

public sealed class KlineBar
{
    public DateTime OpenTimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal QuoteVolume { get; set; }
}
