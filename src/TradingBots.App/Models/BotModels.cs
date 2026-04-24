using System.Text.Json.Serialization;

namespace TradingBots.App.Models;

public enum BinanceEnvironment
{
    Production = 0,
    Sandbox = 1
}

public enum TradeExecutionMode
{
    Paper = 0,
    Live = 1
}

public enum BotState
{
    Stopped = 0,
    Running = 1
}

public enum StrategyType
{
    Momentum = 0,
    Pullback = 1
}

public sealed class TradingBot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Name { get; set; } = string.Empty;
    public decimal BudgetUsdt { get; set; }
    public decimal MaxPositionPerTradeUsdt { get; set; }
    public decimal StopLossPercent { get; set; }
    public decimal TakeProfitPercent { get; set; }
    public decimal TakeProfit1Percent { get; set; } = 1.5m;
    public decimal TakeProfit1SellPercent { get; set; } = 50m;
    public decimal TakeProfit2Percent { get; set; } = 3m;
    public decimal TrailingActivationPercent { get; set; } = 1.2m;
    public decimal TrailingStopPercent { get; set; } = 0.8m;
    public int MaxHoldingMinutes { get; set; } = 180;
    public decimal MaxDailyLossUsdt { get; set; }
    public List<string> Symbols { get; set; } = [];
    public BotState State { get; set; } = BotState.Stopped;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public decimal RealizedPnlUsdt { get; set; }
    public decimal UnrealizedPnlUsdt { get; set; }
    public decimal PositionQuantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public string PositionSymbol { get; set; } = string.Empty;
    public DateTime? PositionOpenedAtUtc { get; set; }
    public decimal PeakPriceSinceEntry { get; set; }
    public bool TakeProfit1Taken { get; set; }
    public decimal MaxExposurePercent { get; set; } = 100m;
    public int CooldownMinutesAfterLoss { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
    public string CooldownSymbol { get; set; } = string.Empty;
    public string LastExecutionError { get; set; } = string.Empty;
    public int MaxConsecutiveLossTrades { get; set; } = 5;
    public int ConsecutiveLossTrades { get; set; }
    public bool IsAutoManaged { get; set; }

    /// <summary>
    /// Si es true, AutoPilot no reciclará ni reactivará este bot; rebalanceo y supervisor lo ignoran.
    /// Se desactiva solo con desbloqueo manual (API/UI).
    /// </summary>
    public bool AutoResumeBlocked { get; set; }
    public decimal AutoScaleReferencePnlUsdt { get; set; }
    public StrategyType StrategyType { get; set; } = StrategyType.Momentum;
    public decimal RollingExpectancyUsdt { get; set; }
    public int NegativeEdgeCycles { get; set; }
    public DateTime? LastAutoScaleUtc { get; set; }
    public DateTime? LastRiskAdjustmentUtc { get; set; }
    public int OutOfTopCycles { get; set; }

    /// <summary>
    /// Suma del realized PnL de las ventas del ciclo actual (misma observacion ML hasta cierre total).
    /// </summary>
    public decimal MlRoundTripRealizedUsdt { get; set; }

    /// <summary>
    /// Ultima vez que el bot paso a Running (manual o AutoPilot). El supervisor usa esto si aun no hay trades.
    /// </summary>
    public DateTime? LastRunningStartedAtUtc { get; set; }
}

public sealed class CreateOrUpdateBotRequest
{
    public string Name { get; set; } = string.Empty;
    public decimal BudgetUsdt { get; set; }
    public decimal MaxPositionPerTradeUsdt { get; set; }
    public decimal StopLossPercent { get; set; } = 2m;
    public decimal TakeProfitPercent { get; set; } = 3m;
    public decimal TakeProfit1Percent { get; set; } = 1.5m;
    public decimal TakeProfit1SellPercent { get; set; } = 50m;
    public decimal TakeProfit2Percent { get; set; } = 3m;
    public decimal TrailingActivationPercent { get; set; } = 1.2m;
    public decimal TrailingStopPercent { get; set; } = 0.8m;
    public int MaxHoldingMinutes { get; set; } = 180;
    public decimal MaxDailyLossUsdt { get; set; } = 50m;
    public decimal MaxExposurePercent { get; set; } = 100m;
    public int CooldownMinutesAfterLoss { get; set; }
    public int MaxConsecutiveLossTrades { get; set; } = 5;
    public List<string> Symbols { get; set; } = [];
}

public sealed class MarketTicker
{
    public string Symbol { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
    public decimal PriceChangePercent24h { get; set; }
    public decimal QuoteVolume24h { get; set; }
}

public sealed class TechnicalMarketSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
    public decimal EmaFast { get; set; }
    public decimal EmaSlow { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal MacdLine { get; set; }
    public decimal MacdSignal { get; set; }
    public decimal MacdHistogram { get; set; }
    public decimal PreviousMacdHistogram { get; set; }
    public decimal RelativeVolume { get; set; }
    public decimal AtrPercent { get; set; }
    public decimal VolatilityPercent { get; set; }
    public string Interval { get; set; } = "1m";
}

public sealed class DashboardSummary
{
    public int TotalBots { get; set; }
    public int RunningBots { get; set; }
    public decimal TotalBudget { get; set; }
    public decimal TotalPnl { get; set; }
    public List<MarketTicker> Market { get; set; } = [];
    public List<InvestmentSuggestion> Suggestions { get; set; } = [];
    public DateTime? LastAutoTraderRunUtc { get; set; }
    public string LastAutoTraderStatus { get; set; } = "Sin ejecucion";
    public TradeExecutionMode ExecutionMode { get; set; } = TradeExecutionMode.Paper;
    public BinanceEnvironment ExchangeEnvironment { get; set; } = BinanceEnvironment.Sandbox;
    public bool RealTradingEnabled { get; set; }
    public List<BotPerformanceItem> BotPerformance { get; set; } = [];
}

/// <summary>KPIs de operaciones (filas en Trades) en un rango de fechas calendario UTC.</summary>
public sealed class TradeKpisSummary
{
    /// <summary>Inicio inclusivo del rango (00:00 UTC del primer día).</summary>
    public DateTime RangeFromUtc { get; set; }

    /// <summary>Fin exclusivo (00:00 UTC del día siguiente al último día incluido).</summary>
    public DateTime RangeToUtcExclusive { get; set; }

    public int TotalTrades { get; set; }
    public int BuyCount { get; set; }
    public int SellCount { get; set; }
    public decimal TotalRealizedPnlUsdt { get; set; }
    public decimal GrossVolumeQuoteUsdt { get; set; }

    /// <summary>Ventas con PnL estrictamente positivo.</summary>
    public int WinningSells { get; set; }

    /// <summary>Ventas con PnL estrictamente negativo.</summary>
    public int LosingSells { get; set; }

    /// <summary>Maximo PnL en una sola fila (habitualmente cierres en SELL).</summary>
    public decimal BestTradePnlUsdt { get; set; }

    /// <summary>Minimo PnL en una sola fila.</summary>
    public decimal WorstTradePnlUsdt { get; set; }

    public List<TradeKpisByBotItem> ByBot { get; set; } = [];
}

public sealed class TradeKpisByBotItem
{
    public Guid BotId { get; set; }
    public string BotName { get; set; } = string.Empty;
    public int Trades { get; set; }
    public decimal RealizedPnlUsdt { get; set; }
}

/// <summary>Lista paginada de bots (panel).</summary>
public sealed class PagedBotsResponse
{
    public List<TradingBot> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class TradeExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = "BUY";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal RealizedPnlUsdt { get; set; }
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public sealed class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class BinanceConnectionSettings
{
    public int Id { get; set; } = 1;
    public bool IsEnabled { get; set; } = false;
    public TradeExecutionMode ExecutionMode { get; set; } = TradeExecutionMode.Paper;
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Sandbox;
    public bool LiveSafetyConfirmed { get; set; }
    public bool LiveEnabledByChecklist { get; set; }
    public bool GlobalKillSwitch { get; set; } = true;
    public int MaxAutoBots { get; set; } = 10;
    public bool AutoControlTuningEnabled { get; set; } = true;
    public int SupervisorInactiveMinutes { get; set; } = 120;
    public int RebalanceOutOfTopCycles { get; set; } = 3;
    public int MinActiveBeforePauseMinutes { get; set; } = 20;
    public int MinStoppedBeforeReactivateMinutes { get; set; } = 5;
    /// <summary>
    /// Minutos minimos detenido tras parada por riesgo antes de reciclaje AutoPilot.
    /// </summary>
    public int MinStoppedAfterRiskStopMinutes { get; set; } = 45;
    public bool MlEnabled { get; set; }
    public bool MlShadowMode { get; set; } = true;
    public decimal MlMinWinProbability { get; set; } = 0.55m;
    public int MlMinSamples { get; set; } = 80;
    public DateTime? LastAutoControlTuneUtc { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class UpdateBinanceSettingsRequest
{
    public bool IsEnabled { get; set; }
    public TradeExecutionMode ExecutionMode { get; set; } = TradeExecutionMode.Paper;
    public BinanceEnvironment Environment { get; set; }
    public bool LiveSafetyConfirmed { get; set; }
    public bool LiveEnabledByChecklist { get; set; }
    public bool GlobalKillSwitch { get; set; } = true;
    public int MaxAutoBots { get; set; } = 10;
    public bool AutoControlTuningEnabled { get; set; } = true;
    public int SupervisorInactiveMinutes { get; set; } = 120;
    public int RebalanceOutOfTopCycles { get; set; } = 3;
    public int MinActiveBeforePauseMinutes { get; set; } = 20;
    public int MinStoppedBeforeReactivateMinutes { get; set; } = 5;
    public int MinStoppedAfterRiskStopMinutes { get; set; } = 45;
    public bool MlEnabled { get; set; }
    public bool MlShadowMode { get; set; } = true;
    public decimal MlMinWinProbability { get; set; } = 0.55m;
    public int MlMinSamples { get; set; } = 80;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
}

public sealed class BinanceSettingsView
{
    public bool IsEnabled { get; set; }
    public TradeExecutionMode ExecutionMode { get; set; } = TradeExecutionMode.Paper;
    public BinanceEnvironment Environment { get; set; }
    public bool LiveSafetyConfirmed { get; set; }
    public bool LiveEnabledByChecklist { get; set; }
    public bool GlobalKillSwitch { get; set; }
    public int MaxAutoBots { get; set; }
    public bool AutoControlTuningEnabled { get; set; }
    public int SupervisorInactiveMinutes { get; set; }
    public int RebalanceOutOfTopCycles { get; set; }
    public int MinActiveBeforePauseMinutes { get; set; }
    public int MinStoppedBeforeReactivateMinutes { get; set; }
    public int MinStoppedAfterRiskStopMinutes { get; set; }
    public bool MlEnabled { get; set; }
    public bool MlShadowMode { get; set; }
    public decimal MlMinWinProbability { get; set; }
    public int MlMinSamples { get; set; }
    public DateTime? LastAutoControlTuneUtc { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecretMasked { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LiveChecklistRequest
{
    public bool ConfirmApiTradingPermission { get; set; }
    public bool ConfirmIpWhitelist { get; set; }
    public bool ConfirmSmallSizeFirst { get; set; }
}

public sealed class BinanceAccountSummary
{
    public bool Connected { get; set; }
    public string EnvironmentName { get; set; } = string.Empty;
    public decimal UsdtFree { get; set; }
    public decimal UsdtLocked { get; set; }
    public decimal UsdcFree { get; set; }
    public decimal UsdcLocked { get; set; }
    public decimal FdusdFree { get; set; }
    public decimal FdusdLocked { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class InvestmentSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = string.Empty;
    public string Signal { get; set; } = "HOLD";
    public decimal Score { get; set; }
    public decimal PriceChangePercent24h { get; set; }
    public string Rationale { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public StrategyType SuggestedStrategy { get; set; } = StrategyType.Momentum;
}

public sealed class BotPerformanceItem
{
    public Guid BotId { get; set; }
    public string BotName { get; set; } = string.Empty;
    public decimal DailyPnlUsdt { get; set; }
    public decimal TotalPnlUsdt { get; set; }
}

public sealed class BotSignalDiagnosticsItem
{
    public Guid BotId { get; set; }
    public string BotName { get; set; } = string.Empty;
    public string SignalLabel { get; set; } = "SIN_DATOS";
    public string Reason { get; set; } = string.Empty;
    public string ActiveSymbol { get; set; } = string.Empty;
    public string ExitState { get; set; } = string.Empty;
}

public sealed class ForceSellResponse
{
    /// <summary>ok | not_found | invalid</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("quantitySold")]
    public decimal? QuantitySold { get; init; }

    [JsonPropertyName("averagePrice")]
    public decimal? AveragePrice { get; init; }
}

public sealed class OrderAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? BotId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal RequestedQuoteQty { get; set; }
    public decimal RequestedBaseQty { get; set; }
    public decimal ExecutedQty { get; set; }
    public decimal ExecutedPrice { get; set; }
    public int LatencyMs { get; set; }
    public bool IsLive { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BotAnalyticsItem
{
    public Guid BotId { get; set; }
    public string BotName { get; set; } = string.Empty;
    public int ClosedTrades { get; set; }
    public decimal WinRatePercent { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AvgWinUsdt { get; set; }
    public decimal AvgLossUsdt { get; set; }
    public decimal MaxDrawdownUsdt { get; set; }
    public decimal NetRealizedUsdt { get; set; }
    public string SolidityTier { get; set; } = "ROJO";
    public decimal SolidityScore { get; set; }
    public string SolidityReason { get; set; } = string.Empty;
}

public sealed class SystemReadinessView
{
    public bool AppHealthy { get; set; }
    public bool HasRunningBots { get; set; }
    public bool ExchangeConfigured { get; set; }
    public bool LiveGuardsOk { get; set; }
    public bool LiveReady { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class BinanceHealthView
{
    public string EnvironmentName { get; set; } = string.Empty;
    public bool TradingLiveEnabled { get; set; }
    public long TimeOffsetMs { get; set; }
    public DateTime? LastTimeSyncUtc { get; set; }
    public int RateLimit429Count { get; set; }
    public int RateLimit418Count { get; set; }
    public int ReconcileAttempts { get; set; }
    public int ReconcileRecovered { get; set; }
    public decimal ReconcileRecoveryRatePercent { get; set; }
    public string LastExecutionError { get; set; } = string.Empty;
}

public sealed class MlTradeObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public StrategyType StrategyType { get; set; } = StrategyType.Momentum;
    public DateTime EntryAtUtc { get; set; } = DateTime.UtcNow;
    public decimal EntryPrice { get; set; }
    public decimal PredictedWinProbability { get; set; }
    public decimal EmaGapPct { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal MacdHistogram { get; set; }
    public decimal RelativeVolume { get; set; }
    public decimal PriceChangePercent24h { get; set; }
    public decimal QuoteVolume24h { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public decimal? RealizedPnlUsdt { get; set; }
    public bool? IsWin { get; set; }
}

public sealed class MlRuntimeSummary
{
    public bool Enabled { get; set; }
    public bool ShadowMode { get; set; }
    public decimal MinWinProbability { get; set; }
    public int MinSamples { get; set; }
    public int TotalSamples { get; set; }
    public int ClosedSamples { get; set; }
    public decimal WinRatePercent { get; set; }
    public DateTime? LastTrainedUtc { get; set; }
}

public sealed class MlDiagnosticsView
{
    public bool Enabled { get; set; }
    public bool ShadowMode { get; set; }
    public decimal MinWinProbability { get; set; }
    public int MinSamples { get; set; }
    public int TotalSamples { get; set; }
    public int ClosedSamples { get; set; }
    public decimal WinRatePercent { get; set; }
    public bool TrainingRanThisRequest { get; set; }
    public bool ModelReady { get; set; }
    public int ClosedRowsUsedForTraining { get; set; }
    public DateTime? LastTrainedUtc { get; set; }
    public string Note { get; set; } = string.Empty;
}
