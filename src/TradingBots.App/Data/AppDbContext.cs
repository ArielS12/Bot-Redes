using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TradingBots.App.Models;

namespace TradingBots.App.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TradingBot> Bots => Set<TradingBot>();
    public DbSet<TradeExecution> Trades => Set<TradeExecution>();
    public DbSet<OrderAuditEvent> OrderAuditEvents => Set<OrderAuditEvent>();
    public DbSet<BinanceConnectionSettings> BinanceSettings => Set<BinanceConnectionSettings>();
    public DbSet<InvestmentSuggestion> InvestmentSuggestions => Set<InvestmentSuggestion>();
    public DbSet<MlTradeObservation> MlTradeObservations => Set<MlTradeObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var symbolsComparer = new ValueComparer<List<string>>(
            (a, b) => a!.SequenceEqual(b!),
            a => a.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
            a => a.ToList());

        modelBuilder.Entity<TradingBot>(entity =>
        {
            entity.HasKey(x => x.Id);
            // Evita SQL específico de proveedor (GETUTCDATE en SQL Server no existe en PostgreSQL).
            // El valor por defecto se toma del inicializador del modelo (DateTime.UtcNow).
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.BudgetUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.MaxPositionPerTradeUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.StopLossPercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TakeProfitPercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TakeProfit1Percent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TakeProfit1SellPercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TakeProfit2Percent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TrailingActivationPercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.TrailingStopPercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.MaxDailyLossUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.RealizedPnlUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.UnrealizedPnlUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.PositionQuantity).HasColumnType("decimal(18,8)");
            entity.Property(x => x.AverageEntryPrice).HasColumnType("decimal(18,8)");
            entity.Property(x => x.PeakPriceSinceEntry).HasColumnType("decimal(18,8)");
            entity.Property(x => x.PositionSymbol).HasMaxLength(30);
            entity.Property(x => x.MaxExposurePercent).HasColumnType("decimal(10,4)");
            entity.Property(x => x.CooldownSymbol).HasMaxLength(30);
            entity.Property(x => x.LastExecutionError).HasMaxLength(500);
            entity.Property(x => x.MaxConsecutiveLossTrades).HasDefaultValue(5);
            entity.Property(x => x.AutoScaleReferencePnlUsdt).HasColumnType("decimal(18,2)");
            entity.Property(x => x.RollingExpectancyUsdt).HasColumnType("decimal(18,4)");
            entity.Property(x => x.NegativeEdgeCycles).HasDefaultValue(0);
            entity.Property(x => x.OutOfTopCycles).HasDefaultValue(0);
            entity.Property(x => x.StrategyType).HasConversion<int>();

            entity.Property(x => x.Symbols)
                .HasConversion(
                    list => string.Join(',', list),
                    value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList())
                .Metadata.SetValueComparer(symbolsComparer);
        });

        modelBuilder.Entity<TradeExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Symbol).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Side).HasMaxLength(10).IsRequired();
            entity.Property(x => x.Price).HasColumnType("decimal(18,8)");
            entity.Property(x => x.Quantity).HasColumnType("decimal(18,8)");
            entity.Property(x => x.RealizedPnlUsdt).HasColumnType("decimal(18,2)");
            entity.HasIndex(x => new { x.BotId, x.ExecutedAtUtc });
        });

        modelBuilder.Entity<BinanceConnectionSettings>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExecutionMode).HasConversion<int>();
            entity.Property(x => x.MaxAutoBots).HasDefaultValue(10);
            entity.Property(x => x.AutoControlTuningEnabled).HasDefaultValue(true);
            entity.Property(x => x.SupervisorInactiveMinutes).HasDefaultValue(120);
            entity.Property(x => x.RebalanceOutOfTopCycles).HasDefaultValue(3);
            entity.Property(x => x.MinActiveBeforePauseMinutes).HasDefaultValue(20);
            entity.Property(x => x.MinStoppedBeforeReactivateMinutes).HasDefaultValue(5);
            entity.Property(x => x.MlEnabled).HasDefaultValue(false);
            entity.Property(x => x.MlShadowMode).HasDefaultValue(true);
            entity.Property(x => x.MlMinWinProbability).HasColumnType("decimal(10,4)").HasDefaultValue(0.55m);
            entity.Property(x => x.MlMinSamples).HasDefaultValue(80);
            entity.Property(x => x.ApiKey).HasMaxLength(300);
            entity.Property(x => x.ApiSecret).HasMaxLength(300);
        });

        modelBuilder.Entity<OrderAuditEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Symbol).HasMaxLength(30);
            entity.Property(x => x.Side).HasMaxLength(10);
            entity.Property(x => x.Stage).HasMaxLength(30);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Message).HasMaxLength(600);
            entity.Property(x => x.RequestedQuoteQty).HasColumnType("decimal(18,8)");
            entity.Property(x => x.RequestedBaseQty).HasColumnType("decimal(18,8)");
            entity.Property(x => x.ExecutedQty).HasColumnType("decimal(18,8)");
            entity.Property(x => x.ExecutedPrice).HasColumnType("decimal(18,8)");
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => new { x.BotId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<InvestmentSuggestion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Symbol).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Signal).HasMaxLength(10).IsRequired();
            entity.Property(x => x.Score).HasColumnType("decimal(10,4)");
            entity.Property(x => x.PriceChangePercent24h).HasColumnType("decimal(10,4)");
            entity.Property(x => x.Rationale).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SuggestedStrategy).HasConversion<int>();
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<MlTradeObservation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Symbol).HasMaxLength(30).IsRequired();
            entity.Property(x => x.StrategyType).HasConversion<int>();
            entity.Property(x => x.EntryPrice).HasColumnType("decimal(18,8)");
            entity.Property(x => x.PredictedWinProbability).HasColumnType("decimal(10,6)");
            entity.Property(x => x.EmaGapPct).HasColumnType("decimal(10,6)");
            entity.Property(x => x.Rsi14).HasColumnType("decimal(10,6)");
            entity.Property(x => x.MacdHistogram).HasColumnType("decimal(18,8)");
            entity.Property(x => x.RelativeVolume).HasColumnType("decimal(10,6)");
            entity.Property(x => x.PriceChangePercent24h).HasColumnType("decimal(10,6)");
            entity.Property(x => x.QuoteVolume24h).HasColumnType("decimal(18,2)");
            entity.Property(x => x.RealizedPnlUsdt).HasColumnType("decimal(18,4)");
            entity.HasIndex(x => new { x.BotId, x.Symbol, x.EntryAtUtc });
            entity.HasIndex(x => x.ClosedAtUtc);
        });
    }
}
