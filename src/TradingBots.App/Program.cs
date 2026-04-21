using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using TradingBots.App.Data;
using TradingBots.App.Components;
using TradingBots.App.Models;
using TradingBots.App.Services;
using TradingBots.App;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<AdminUserSettings>(builder.Configuration.GetSection(AdminUserSettings.SectionName));
var dbProvider = builder.Configuration["Database:Provider"] ?? Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "SqlServer";
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqliteConnection = builder.Configuration.GetConnectionString("SqliteConnection") ?? "Data Source=tradingbots.db";
        options.UseSqlite(sqliteConnection);
    }
    else if (dbProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) || dbProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        var postgresConnection =
            builder.Configuration.GetConnectionString("PostgresConnection")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Falta cadena de conexion Postgres.");
        options.UseNpgsql(NormalizePostgresConnection(postgresConnection));
    }
    else
    {
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
});
builder.Services.AddHttpClient<IBinanceMarketService, BinanceMarketService>();
builder.Services.AddHttpClient<IBinanceTradeExecutionService, BinanceTradeExecutionService>();
builder.Services.AddScoped<IBotService, BotService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ClientAuthSession>();
builder.Services.AddScoped<UiLoadingState>();
builder.Services.AddScoped<IBinanceSettingsService, BinanceSettingsService>();
builder.Services.AddScoped<ITradeMlService, TradeMlService>();
builder.Services.AddScoped<IMarketAdvisorService, MarketAdvisorService>();
builder.Services.AddScoped<IAutoTraderService, AutoTraderService>();
builder.Services.AddScoped<IBotSupervisorService, BotSupervisorService>();
builder.Services.AddScoped<IControlAutotuneService, ControlAutotuneService>();
builder.Services.AddSingleton<IRuntimeStatusService, RuntimeStatusService>();
builder.Services.AddHostedService<BotExecutionBackgroundService>();
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = key
        };
    });
builder.Services.AddAuthorization();

static string NormalizePostgresConnection(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        throw new InvalidOperationException("Cadena Postgres vacia.");
    }

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.None);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.Trim('/');
        var sslMode = "Require";
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var pairs = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=', 2, StringSplitOptions.None);
                if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                {
                    sslMode = Uri.UnescapeDataString(kv[1]);
                    break;
                }
            }
        }
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode={sslMode};Trust Server Certificate=true";
    }

    return raw;
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    if (db.Database.IsSqlServer())
    {
        await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID(N'[BinanceSettings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [BinanceSettings](
                [Id] int NOT NULL PRIMARY KEY,
                [IsEnabled] bit NOT NULL,
                [Environment] int NOT NULL,
                [ApiKey] nvarchar(300) NOT NULL,
                [ApiSecret] nvarchar(300) NOT NULL,
                [UpdatedAtUtc] datetime2 NOT NULL
            );
        END
        """);
    await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID(N'[InvestmentSuggestions]', N'U') IS NULL
        BEGIN
            CREATE TABLE [InvestmentSuggestions](
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [Symbol] nvarchar(30) NOT NULL,
                [Signal] nvarchar(10) NOT NULL,
                [Score] decimal(10,4) NOT NULL,
                [PriceChangePercent24h] decimal(10,4) NOT NULL,
                [Rationale] nvarchar(500) NOT NULL,
                [CreatedAtUtc] datetime2 NOT NULL
            );
            CREATE INDEX [IX_InvestmentSuggestions_CreatedAtUtc] ON [InvestmentSuggestions]([CreatedAtUtc]);
        END
        """);
    await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID(N'[OrderAuditEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [OrderAuditEvents](
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [BotId] uniqueidentifier NULL,
                [Symbol] nvarchar(30) NOT NULL,
                [Side] nvarchar(10) NOT NULL,
                [Stage] nvarchar(30) NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [Message] nvarchar(600) NOT NULL,
                [RequestedQuoteQty] decimal(18,8) NOT NULL,
                [RequestedBaseQty] decimal(18,8) NOT NULL,
                [ExecutedQty] decimal(18,8) NOT NULL,
                [ExecutedPrice] decimal(18,8) NOT NULL,
                [LatencyMs] int NOT NULL,
                [IsLive] bit NOT NULL,
                [CreatedAtUtc] datetime2 NOT NULL
            );
            CREATE INDEX [IX_OrderAuditEvents_CreatedAtUtc] ON [OrderAuditEvents]([CreatedAtUtc]);
            CREATE INDEX [IX_OrderAuditEvents_BotId_CreatedAtUtc] ON [OrderAuditEvents]([BotId],[CreatedAtUtc]);
        END
        """);
    await db.Database.ExecuteSqlRawAsync("""
        IF COL_LENGTH('Bots', 'IsAutoManaged') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [IsAutoManaged] bit NOT NULL CONSTRAINT DF_Bots_IsAutoManaged DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'AutoScaleReferencePnlUsdt') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [AutoScaleReferencePnlUsdt] decimal(18,2) NOT NULL CONSTRAINT DF_Bots_AutoScaleReferencePnlUsdt DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'StrategyType') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [StrategyType] int NOT NULL CONSTRAINT DF_Bots_StrategyType DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'PositionQuantity') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [PositionQuantity] decimal(18,8) NOT NULL CONSTRAINT DF_Bots_PositionQuantity DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'AverageEntryPrice') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [AverageEntryPrice] decimal(18,8) NOT NULL CONSTRAINT DF_Bots_AverageEntryPrice DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'PositionSymbol') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [PositionSymbol] nvarchar(30) NOT NULL CONSTRAINT DF_Bots_PositionSymbol DEFAULT('');
        END
        IF COL_LENGTH('Bots', 'MaxConsecutiveLossTrades') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [MaxConsecutiveLossTrades] int NOT NULL CONSTRAINT DF_Bots_MaxConsecutiveLossTrades DEFAULT(5);
        END
        IF COL_LENGTH('Bots', 'ConsecutiveLossTrades') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [ConsecutiveLossTrades] int NOT NULL CONSTRAINT DF_Bots_ConsecutiveLossTrades DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'MaxExposurePercent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [MaxExposurePercent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_MaxExposurePercent DEFAULT(100);
        END
        IF COL_LENGTH('Bots', 'CooldownMinutesAfterLoss') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [CooldownMinutesAfterLoss] int NOT NULL CONSTRAINT DF_Bots_CooldownMinutesAfterLoss DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'CooldownUntilUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [CooldownUntilUtc] datetime2 NULL;
        END
        IF COL_LENGTH('Bots', 'CooldownSymbol') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [CooldownSymbol] nvarchar(30) NOT NULL CONSTRAINT DF_Bots_CooldownSymbol DEFAULT('');
        END
        IF COL_LENGTH('Bots', 'LastExecutionError') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [LastExecutionError] nvarchar(500) NOT NULL CONSTRAINT DF_Bots_LastExecutionError DEFAULT('');
        END
        IF COL_LENGTH('Bots', 'TakeProfit1Percent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TakeProfit1Percent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_TakeProfit1Percent DEFAULT(1.5);
        END
        IF COL_LENGTH('Bots', 'TakeProfit1SellPercent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TakeProfit1SellPercent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_TakeProfit1SellPercent DEFAULT(50);
        END
        IF COL_LENGTH('Bots', 'TakeProfit2Percent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TakeProfit2Percent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_TakeProfit2Percent DEFAULT(3);
        END
        IF COL_LENGTH('Bots', 'TrailingActivationPercent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TrailingActivationPercent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_TrailingActivationPercent DEFAULT(1.2);
        END
        IF COL_LENGTH('Bots', 'TrailingStopPercent') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TrailingStopPercent] decimal(10,4) NOT NULL CONSTRAINT DF_Bots_TrailingStopPercent DEFAULT(0.8);
        END
        IF COL_LENGTH('Bots', 'MaxHoldingMinutes') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [MaxHoldingMinutes] int NOT NULL CONSTRAINT DF_Bots_MaxHoldingMinutes DEFAULT(180);
        END
        IF COL_LENGTH('Bots', 'PositionOpenedAtUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [PositionOpenedAtUtc] datetime2 NULL;
        END
        IF COL_LENGTH('Bots', 'PeakPriceSinceEntry') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [PeakPriceSinceEntry] decimal(18,8) NOT NULL CONSTRAINT DF_Bots_PeakPriceSinceEntry DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'TakeProfit1Taken') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [TakeProfit1Taken] bit NOT NULL CONSTRAINT DF_Bots_TakeProfit1Taken DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'RollingExpectancyUsdt') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [RollingExpectancyUsdt] decimal(18,4) NOT NULL CONSTRAINT DF_Bots_RollingExpectancyUsdt DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'NegativeEdgeCycles') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [NegativeEdgeCycles] int NOT NULL CONSTRAINT DF_Bots_NegativeEdgeCycles DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'LastAutoScaleUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [LastAutoScaleUtc] datetime2 NULL;
        END
        IF COL_LENGTH('Bots', 'LastRiskAdjustmentUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [LastRiskAdjustmentUtc] datetime2 NULL;
        END
        IF COL_LENGTH('Bots', 'OutOfTopCycles') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [OutOfTopCycles] int NOT NULL CONSTRAINT DF_Bots_OutOfTopCycles DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'MlRoundTripRealizedUsdt') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [MlRoundTripRealizedUsdt] decimal(18,4) NOT NULL CONSTRAINT DF_Bots_MlRoundTripRealizedUsdt DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'LastRunningStartedAtUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [LastRunningStartedAtUtc] datetime2 NULL;
        END
        IF COL_LENGTH('Bots', 'AutoResumeBlocked') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [AutoResumeBlocked] bit NOT NULL CONSTRAINT DF_Bots_AutoResumeBlocked DEFAULT(0);
        END
        IF COL_LENGTH('Bots', 'CreatedAtUtc') IS NULL
        BEGIN
            ALTER TABLE [Bots] ADD [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT DF_Bots_CreatedAtUtc DEFAULT(GETUTCDATE());
        END
        IF COL_LENGTH('InvestmentSuggestions', 'SuggestedStrategy') IS NULL
        BEGIN
            ALTER TABLE [InvestmentSuggestions] ADD [SuggestedStrategy] int NOT NULL CONSTRAINT DF_InvestmentSuggestions_SuggestedStrategy DEFAULT(0);
        END
        IF COL_LENGTH('BinanceSettings', 'ExecutionMode') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [ExecutionMode] int NOT NULL CONSTRAINT DF_BinanceSettings_ExecutionMode DEFAULT(0);
        END
        IF COL_LENGTH('BinanceSettings', 'LiveSafetyConfirmed') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [LiveSafetyConfirmed] bit NOT NULL CONSTRAINT DF_BinanceSettings_LiveSafetyConfirmed DEFAULT(0);
        END
        IF COL_LENGTH('BinanceSettings', 'LiveEnabledByChecklist') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [LiveEnabledByChecklist] bit NOT NULL CONSTRAINT DF_BinanceSettings_LiveEnabledByChecklist DEFAULT(0);
        END
        IF COL_LENGTH('BinanceSettings', 'GlobalKillSwitch') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [GlobalKillSwitch] bit NOT NULL CONSTRAINT DF_BinanceSettings_GlobalKillSwitch DEFAULT(1);
        END
        IF COL_LENGTH('BinanceSettings', 'MaxAutoBots') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MaxAutoBots] int NOT NULL CONSTRAINT DF_BinanceSettings_MaxAutoBots DEFAULT(10);
        END
        IF COL_LENGTH('BinanceSettings', 'AutoControlTuningEnabled') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [AutoControlTuningEnabled] bit NOT NULL CONSTRAINT DF_BinanceSettings_AutoControlTuningEnabled DEFAULT(1);
        END
        IF COL_LENGTH('BinanceSettings', 'SupervisorInactiveMinutes') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [SupervisorInactiveMinutes] int NOT NULL CONSTRAINT DF_BinanceSettings_SupervisorInactiveMinutes DEFAULT(120);
        END
        IF COL_LENGTH('BinanceSettings', 'RebalanceOutOfTopCycles') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [RebalanceOutOfTopCycles] int NOT NULL CONSTRAINT DF_BinanceSettings_RebalanceOutOfTopCycles DEFAULT(3);
        END
        IF COL_LENGTH('BinanceSettings', 'MinActiveBeforePauseMinutes') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MinActiveBeforePauseMinutes] int NOT NULL CONSTRAINT DF_BinanceSettings_MinActiveBeforePauseMinutes DEFAULT(20);
        END
        IF COL_LENGTH('BinanceSettings', 'MinStoppedBeforeReactivateMinutes') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MinStoppedBeforeReactivateMinutes] int NOT NULL CONSTRAINT DF_BinanceSettings_MinStoppedBeforeReactivateMinutes DEFAULT(5);
        END
        IF COL_LENGTH('BinanceSettings', 'LastAutoControlTuneUtc') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [LastAutoControlTuneUtc] datetime2 NULL;
        END
        IF COL_LENGTH('BinanceSettings', 'MlEnabled') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MlEnabled] bit NOT NULL CONSTRAINT DF_BinanceSettings_MlEnabled DEFAULT(0);
        END
        IF COL_LENGTH('BinanceSettings', 'MlShadowMode') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MlShadowMode] bit NOT NULL CONSTRAINT DF_BinanceSettings_MlShadowMode DEFAULT(1);
        END
        IF COL_LENGTH('BinanceSettings', 'MlMinWinProbability') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MlMinWinProbability] decimal(10,4) NOT NULL CONSTRAINT DF_BinanceSettings_MlMinWinProbability DEFAULT(0.55);
        END
        IF COL_LENGTH('BinanceSettings', 'MlMinSamples') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MlMinSamples] int NOT NULL CONSTRAINT DF_BinanceSettings_MlMinSamples DEFAULT(80);
        END
        IF COL_LENGTH('BinanceSettings', 'MinStoppedAfterRiskStopMinutes') IS NULL
        BEGIN
            ALTER TABLE [BinanceSettings] ADD [MinStoppedAfterRiskStopMinutes] int NOT NULL CONSTRAINT DF_BinanceSettings_MinStoppedAfterRiskStopMinutes DEFAULT(45);
        END
        IF OBJECT_ID(N'[MlTradeObservations]', N'U') IS NULL
        BEGIN
            CREATE TABLE [MlTradeObservations](
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [BotId] uniqueidentifier NOT NULL,
                [Symbol] nvarchar(30) NOT NULL,
                [StrategyType] int NOT NULL,
                [EntryAtUtc] datetime2 NOT NULL,
                [EntryPrice] decimal(18,8) NOT NULL,
                [PredictedWinProbability] decimal(10,6) NOT NULL,
                [EmaGapPct] decimal(10,6) NOT NULL,
                [Rsi14] decimal(10,6) NOT NULL,
                [MacdHistogram] decimal(18,8) NOT NULL,
                [RelativeVolume] decimal(10,6) NOT NULL,
                [PriceChangePercent24h] decimal(10,6) NOT NULL,
                [QuoteVolume24h] decimal(18,2) NOT NULL,
                [ClosedAtUtc] datetime2 NULL,
                [RealizedPnlUsdt] decimal(18,4) NULL,
                [IsWin] bit NULL
            );
            CREATE INDEX [IX_MlTradeObservations_BotId_Symbol_EntryAtUtc] ON [MlTradeObservations]([BotId],[Symbol],[EntryAtUtc]);
            CREATE INDEX [IX_MlTradeObservations_ClosedAtUtc] ON [MlTradeObservations]([ClosedAtUtc]);
        END
        """);
    }

    if (db.Database.IsNpgsql())
    {
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "BinanceSettings" ADD COLUMN IF NOT EXISTS "MinStoppedAfterRiskStopMinutes" integer NOT NULL DEFAULT 45;""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Bots" ADD COLUMN IF NOT EXISTS "MlRoundTripRealizedUsdt" numeric(18,4) NOT NULL DEFAULT 0;""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "MlTradeObservations" ALTER COLUMN "IsWin" DROP NOT NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "MlTradeObservations" SET "IsWin" = NULL WHERE "ClosedAtUtc" IS NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Bots" ADD COLUMN IF NOT EXISTS "LastRunningStartedAtUtc" timestamp with time zone NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "Bots" SET "LastRunningStartedAtUtc" = "UpdatedAtUtc" WHERE "State" = 1 AND "LastRunningStartedAtUtc" IS NULL;""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Bots" ADD COLUMN IF NOT EXISTS "AutoResumeBlocked" boolean NOT NULL DEFAULT FALSE;""");
    }

    if (db.Database.IsSqlite())
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE BinanceSettings ADD COLUMN MinStoppedAfterRiskStopMinutes INTEGER NOT NULL DEFAULT 45;""");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    // Bots sin posicion abierta (no hay activo a la espera de venta): budget y tope por trade 20/20.
    var nowBudgetAlign = DateTime.UtcNow;
    await db.Bots
        .Where(b => b.PositionQuantity <= 0m && (b.BudgetUsdt != 20m || b.MaxPositionPerTradeUsdt != 20m))
        .ExecuteUpdateAsync(s => s
            .SetProperty(b => b.BudgetUsdt, 20m)
            .SetProperty(b => b.MaxPositionPerTradeUsdt, 20m)
            .SetProperty(b => b.UpdatedAtUtc, nowBudgetAlign));

    if (!await db.Bots.AnyAsync())
    {
        var seedStart = DateTime.UtcNow;
        db.Bots.Add(new TradingBot
        {
            Name = "Momentum-BTC",
            BudgetUsdt = 20m,
            MaxPositionPerTradeUsdt = 20m,
            StopLossPercent = 1.8m,
            TakeProfitPercent = 3.2m,
            MaxDailyLossUsdt = 60m,
            Symbols = ["BTCUSDT"],
            State = BotState.Running,
            LastRunningStartedAtUtc = seedStart,
            UpdatedAtUtc = seedStart
        });
        await db.SaveChangesAsync();
    }

    if (!await db.BinanceSettings.AnyAsync())
    {
        db.BinanceSettings.Add(new BinanceConnectionSettings
        {
            IsEnabled = false,
            Environment = BinanceEnvironment.Sandbox
        });
        await db.SaveChangesAsync();
    }
}

app.MapPost("/api/auth/login", (LoginRequest request, IAuthService authService) =>
{
    var result = authService.Login(request);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
});

app.MapGet("/api/bots", async (IBotService botService) => Results.Ok(await botService.GetBotsAsync()));

app.MapGet("/api/bots/paged", async (int page, int pageSize, IBotService botService) =>
    Results.Ok(await botService.GetBotsPageAsync(page, pageSize)));

app.MapGet("/api/bots/{id:guid}", async (Guid id, IBotService botService) =>
{
    var bot = await botService.GetBotAsync(id);
    return bot is null ? Results.NotFound() : Results.Ok(bot);
});

app.MapGet("/api/bots/{id:guid}/trades", async (Guid id, AppDbContext db) =>
{
    var exists = await db.Bots.AnyAsync(x => x.Id == id);
    if (!exists)
    {
        return Results.NotFound();
    }

    var trades = await db.Trades
        .Where(x => x.BotId == id)
        .OrderByDescending(x => x.ExecutedAtUtc)
        .ToListAsync();

    return Results.Ok(trades);
});

app.MapPost("/api/bots", async (CreateOrUpdateBotRequest request, IBotService botService) =>
{
    var bot = await botService.CreateBotAsync(request);
    return Results.Created($"/api/bots/{bot.Id}", bot);
});

app.MapPut("/api/bots/{id:guid}", async (Guid id, CreateOrUpdateBotRequest request, IBotService botService) =>
{
    var bot = await botService.UpdateBotAsync(id, request);
    return bot is null ? Results.NotFound() : Results.Ok(bot);
});

app.MapPost("/api/bots/{id:guid}/start", async (Guid id, IBotService botService) =>
{
    var ok = await botService.SetBotStateAsync(id, BotState.Running);
    return ok ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/bots/{id:guid}/stop", async (Guid id, IBotService botService) =>
{
    var ok = await botService.SetBotStateAsync(id, BotState.Stopped);
    return ok ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/bots/{id:guid}/auto-block", async (Guid id, IBotService botService) =>
{
    var ok = await botService.SetAutoResumeBlockedAsync(id, true);
    return ok ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/bots/{id:guid}/auto-unblock", async (Guid id, IBotService botService) =>
{
    var ok = await botService.SetAutoResumeBlockedAsync(id, false);
    return ok ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/bots/{id:guid}/force-sell", async (Guid id, IBotService botService) =>
{
    var r = await botService.ForceSellAsync(id);
    return r.Outcome switch
    {
        "not_found" => Results.Json(r, statusCode: StatusCodes.Status404NotFound),
        "ok" => Results.Ok(r),
        _ => Results.BadRequest(r)
    };
});

app.MapGet("/api/market/overview", async (string? symbols, IBinanceMarketService marketService) =>
{
    var list = (symbols ?? "BTCUSDT,ETHUSDT,SOLUSDT")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var data = await marketService.GetMarketOverviewAsync(list);
    return Results.Ok(data);
});

app.MapGet("/api/settings/binance", async (IBinanceSettingsService settingsService) =>
{
    return Results.Ok(await settingsService.GetViewAsync());
});

app.MapPut("/api/settings/binance", async (UpdateBinanceSettingsRequest request, IBinanceSettingsService settingsService) =>
{
    try
    {
        var updated = await settingsService.UpdateAsync(request);
        return Results.Ok(updated);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/settings/binance/account-summary", async (IBinanceTradeExecutionService executionService) =>
{
    return Results.Ok(await executionService.GetAccountSummaryAsync());
});

app.MapGet("/api/settings/binance/health", async (IBinanceTradeExecutionService executionService) =>
{
    return Results.Ok(await executionService.GetHealthAsync());
});

app.MapPost("/api/settings/binance/arm-live", async (LiveChecklistRequest request, IBinanceSettingsService settingsService) =>
{
    try
    {
        return Results.Ok(await settingsService.ArmLiveTradingAsync(request));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/advisor/suggestions", async (IMarketAdvisorService advisorService) =>
{
    return Results.Ok(await advisorService.GetLatestSuggestionsAsync());
});

app.MapGet("/api/bots/performance", async (string? botIds, AppDbContext db) =>
{
    var filter = BotIdsQuery.Parse(botIds);
    var todayStart = DateTime.UtcNow.Date;
    IQueryable<TradingBot> bq = db.Bots;
    if (filter is { Count: > 0 })
    {
        bq = bq.Where(b => filter.Contains(b.Id));
    }

    var bots = await bq
        .OrderByDescending(x => x.State)
        .ThenBy(x => x.Name)
        .ToListAsync();
    if (bots.Count == 0)
    {
        return Results.Ok(Array.Empty<BotPerformanceItem>());
    }

    var idSet = bots.Select(x => x.Id).ToHashSet();
    var tradesToday = await db.Trades
        .Where(x => x.ExecutedAtUtc >= todayStart && idSet.Contains(x.BotId))
        .ToListAsync();

    var dailyMap = tradesToday
        .GroupBy(x => x.BotId)
        .ToDictionary(g => g.Key, g => g.Sum(x => x.RealizedPnlUsdt));
    var data = bots.Select(b => new BotPerformanceItem
    {
        BotId = b.Id,
        BotName = b.Name,
        DailyPnlUsdt = (dailyMap.TryGetValue(b.Id, out var v) ? v : 0m) + b.UnrealizedPnlUsdt,
        TotalPnlUsdt = b.RealizedPnlUsdt + b.UnrealizedPnlUsdt
    });
    return Results.Ok(data);
});

app.MapGet("/api/bots/signals", async (string? botIds, IBotService botService) =>
{
    var filter = BotIdsQuery.Parse(botIds);
    IEnumerable<Guid>? ids = filter is { Count: > 0 } ? filter : null;
    return Results.Ok(await botService.GetSignalDiagnosticsAsync(ids));
});

app.MapGet("/api/bots/analytics", async (string? botIds, AppDbContext db) =>
{
    var filter = BotIdsQuery.Parse(botIds);
    IQueryable<TradingBot> botsQuery = db.Bots;
    if (filter is { Count: > 0 })
    {
        botsQuery = botsQuery.Where(b => filter.Contains(b.Id));
    }

    var bots = await botsQuery
        .OrderByDescending(x => x.State)
        .ThenBy(x => x.Name)
        .ToListAsync();
    if (bots.Count == 0)
    {
        return Results.Ok(Array.Empty<BotAnalyticsItem>());
    }

    var idSet = bots.Select(x => x.Id).ToHashSet();
    var sellTrades = await db.Trades
        .Where(x => x.Side == "SELL" && idSet.Contains(x.BotId))
        .OrderBy(x => x.ExecutedAtUtc)
        .ToListAsync();
    var grouped = sellTrades.GroupBy(x => x.BotId).ToDictionary(x => x.Key, x => x.ToList());
    var result = new List<BotAnalyticsItem>();
    foreach (var bot in bots)
    {
        var closed = grouped.TryGetValue(bot.Id, out var list) ? list : [];
        var wins = closed.Where(x => x.RealizedPnlUsdt > 0m).ToList();
        var losses = closed.Where(x => x.RealizedPnlUsdt < 0m).ToList();
        var sumWins = wins.Sum(x => x.RealizedPnlUsdt);
        var sumLossAbs = Math.Abs(losses.Sum(x => x.RealizedPnlUsdt));
        var equity = 0m;
        var peak = 0m;
        var maxDd = 0m;
        foreach (var trade in closed)
        {
            equity += trade.RealizedPnlUsdt;
            peak = Math.Max(peak, equity);
            maxDd = Math.Max(maxDd, peak - equity);
        }

        result.Add(new BotAnalyticsItem
        {
            BotId = bot.Id,
            BotName = bot.Name,
            ClosedTrades = closed.Count,
            WinRatePercent = closed.Count == 0 ? 0m : decimal.Round((wins.Count * 100m) / closed.Count, 2),
            ProfitFactor = sumLossAbs <= 0m ? (sumWins > 0m ? 999m : 0m) : decimal.Round(sumWins / sumLossAbs, 4),
            AvgWinUsdt = wins.Count == 0 ? 0m : decimal.Round(sumWins / wins.Count, 4),
            AvgLossUsdt = losses.Count == 0 ? 0m : decimal.Round(losses.Sum(x => x.RealizedPnlUsdt) / losses.Count, 4),
            MaxDrawdownUsdt = decimal.Round(maxDd, 4),
            NetRealizedUsdt = decimal.Round(closed.Sum(x => x.RealizedPnlUsdt), 4)
        });
    }

    foreach (var item in result)
    {
        var score = 0m;
        if (item.ClosedTrades >= 100) score += 45m;
        else score += decimal.Round((item.ClosedTrades / 100m) * 45m, 2);

        var pfClamped = Math.Clamp(item.ProfitFactor, 0m, 2m);
        score += decimal.Round((pfClamped / 2m) * 35m, 2);

        var expectancyNorm = item.ClosedTrades == 0 ? 0m : item.NetRealizedUsdt / item.ClosedTrades;
        if (expectancyNorm > 0m)
        {
            score += Math.Min(20m, decimal.Round(expectancyNorm * 20m, 2));
        }

        item.SolidityScore = decimal.Round(score, 2);
        item.SolidityTier =
            item.ClosedTrades >= 200 && item.ProfitFactor > 1.2m && expectancyNorm > 0m ? "VERDE" :
            item.ClosedTrades >= 100 && item.ProfitFactor >= 1.0m ? "AMARILLO" :
            "ROJO";
        item.SolidityReason = item.SolidityTier switch
        {
            "VERDE" => "Muestra alta, PF>1.2 y expectancy positiva.",
            "AMARILLO" => "Muestra intermedia o edge moderado; requiere seguimiento.",
            _ => "Muestra insuficiente o edge no confirmado."
        };
    }

    return Results.Ok(result.OrderByDescending(x => x.NetRealizedUsdt));
});

// Lectura de diagnostico ML; sin JWT para poder comprobar en navegador / monitor basico.
app.MapGet("/api/ml/summary", async (IBinanceSettingsService settingsService, ITradeMlService mlService, CancellationToken ct) =>
{
    var settings = await settingsService.GetActiveSettingsAsync();
    return Results.Ok(await mlService.GetSummaryAsync(settings, ct));
});

app.MapGet("/api/ml/diagnostics", async (IBinanceSettingsService settingsService, ITradeMlService mlService, CancellationToken ct) =>
{
    var settings = await settingsService.GetActiveSettingsAsync();
    return Results.Ok(await mlService.GetDiagnosticsAsync(settings, ct));
});

app.MapGet("/api/audit/orders", async (int take, AppDbContext db) =>
{
    var safeTake = Math.Clamp(take <= 0 ? 50 : take, 1, 500);
    var data = await db.OrderAuditEvents
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(safeTake)
        .ToListAsync();
    return Results.Ok(data);
});

app.MapGet("/api/system/readiness", async (AppDbContext db) =>
{
    var settings = await db.BinanceSettings.FirstOrDefaultAsync(x => x.Id == 1) ?? new BinanceConnectionSettings();
    var hasRunningBots = await db.Bots.AnyAsync(x => x.State == BotState.Running);
    var exchangeConfigured = !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
    var liveGuardsOk = settings.LiveEnabledByChecklist && !settings.GlobalKillSwitch &&
                       (settings.Environment != BinanceEnvironment.Production || settings.LiveSafetyConfirmed);
    var liveReady = settings.IsEnabled && settings.ExecutionMode == TradeExecutionMode.Live && exchangeConfigured && liveGuardsOk;
    var summary = liveReady
        ? "Sistema listo para operacion Live con guardas activas."
        : "Sistema funcional en paper/diagnostico. Live requiere checklist, credenciales y kill switch desactivado.";
    return Results.Ok(new SystemReadinessView
    {
        AppHealthy = true,
        HasRunningBots = hasRunningBots,
        ExchangeConfigured = exchangeConfigured,
        LiveGuardsOk = liveGuardsOk,
        LiveReady = liveReady,
        Summary = summary
    });
});

app.MapGet("/api/dashboard/summary", async (IBotService botService, IBinanceMarketService marketService, IMarketAdvisorService advisorService, IRuntimeStatusService runtimeStatus, IBinanceSettingsService settingsService, AppDbContext db) =>
{
    var bots = (await botService.GetBotsAsync()).ToList();
    var exchangeSettings = await settingsService.GetActiveSettingsAsync();
    var allSymbols = bots.SelectMany(x => x.Symbols).Distinct().ToList();
    var market = allSymbols.Count == 0 ? [] : await marketService.GetMarketOverviewAsync(allSymbols);

    var todayStart = DateTime.UtcNow.Date;
    var tradesToday = await db.Trades
        .Where(x => x.ExecutedAtUtc >= todayStart)
        .ToListAsync();
    var dailyMap = tradesToday
        .GroupBy(x => x.BotId)
        .ToDictionary(g => g.Key, g => g.Sum(x => x.RealizedPnlUsdt));

    var summary = new DashboardSummary
    {
        TotalBots = bots.Count,
        RunningBots = bots.Count(x => x.State == BotState.Running),
        TotalBudget = bots.Sum(x => x.BudgetUsdt),
        TotalPnl = bots.Sum(x => x.RealizedPnlUsdt + x.UnrealizedPnlUsdt),
        Market = market,
        Suggestions = await advisorService.GetLatestSuggestionsAsync(),
        LastAutoTraderRunUtc = runtimeStatus.LastAutoTraderRunUtc,
        LastAutoTraderStatus = runtimeStatus.LastAutoTraderStatus,
        ExecutionMode = exchangeSettings.ExecutionMode,
        ExchangeEnvironment = exchangeSettings.Environment,
        RealTradingEnabled = exchangeSettings.IsEnabled &&
            exchangeSettings.ExecutionMode == TradeExecutionMode.Live &&
            exchangeSettings.LiveEnabledByChecklist &&
            !exchangeSettings.GlobalKillSwitch &&
            (!string.IsNullOrWhiteSpace(exchangeSettings.ApiKey) && !string.IsNullOrWhiteSpace(exchangeSettings.ApiSecret)) &&
            (exchangeSettings.Environment != BinanceEnvironment.Production || exchangeSettings.LiveSafetyConfirmed),
        BotPerformance = bots.Select(b => new BotPerformanceItem
        {
            BotId = b.Id,
            BotName = b.Name,
            DailyPnlUsdt = (dailyMap.TryGetValue(b.Id, out var v) ? v : 0m) + b.UnrealizedPnlUsdt,
            TotalPnlUsdt = b.RealizedPnlUsdt + b.UnrealizedPnlUsdt
        }).OrderByDescending(x => x.TotalPnlUsdt).ToList()
    };

    return Results.Ok(summary);
});

app.MapGet("/api/dashboard/trade-kpis", async (string? dateFrom, string? dateTo, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(dateFrom) || string.IsNullOrWhiteSpace(dateTo))
    {
        return Results.BadRequest(new { error = "Indica dateFrom y dateTo en formato yyyy-MM-dd (calendario UTC)." });
    }

    if (!DateOnly.TryParse(dateFrom, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDay) ||
        !DateOnly.TryParse(dateTo, CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDay))
    {
        return Results.BadRequest(new { error = "Fechas invalidas. Usa yyyy-MM-dd." });
    }

    if (fromDay > toDay)
    {
        (fromDay, toDay) = (toDay, fromDay);
    }

    var rangeStart = new DateTime(fromDay.Year, fromDay.Month, fromDay.Day, 0, 0, 0, DateTimeKind.Utc);
    var rangeEndExclusive = new DateTime(toDay.Year, toDay.Month, toDay.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);

    var rows = await (
        from t in db.Trades.AsNoTracking()
        join b in db.Bots.AsNoTracking() on t.BotId equals b.Id
        where t.ExecutedAtUtc >= rangeStart && t.ExecutedAtUtc < rangeEndExclusive
        select new { t.Side, t.RealizedPnlUsdt, t.Price, t.Quantity, b.Id, b.Name }
    ).ToListAsync();

    var sells = rows.Where(x => string.Equals(x.Side, "SELL", StringComparison.OrdinalIgnoreCase)).ToList();
    var winning = sells.Count(x => x.RealizedPnlUsdt > 0m);
    var losing = sells.Count(x => x.RealizedPnlUsdt < 0m);
    var pnlList = rows.Select(x => x.RealizedPnlUsdt).ToList();

    var byBot = rows
        .GroupBy(x => new { x.Id, x.Name })
        .Select(g => new TradeKpisByBotItem
        {
            BotId = g.Key.Id,
            BotName = g.Key.Name,
            Trades = g.Count(),
            RealizedPnlUsdt = g.Sum(x => x.RealizedPnlUsdt)
        })
        .OrderByDescending(x => x.RealizedPnlUsdt)
        .ToList();

    var summary = new TradeKpisSummary
    {
        RangeFromUtc = rangeStart,
        RangeToUtcExclusive = rangeEndExclusive,
        TotalTrades = rows.Count,
        BuyCount = rows.Count(x => string.Equals(x.Side, "BUY", StringComparison.OrdinalIgnoreCase)),
        SellCount = sells.Count,
        TotalRealizedPnlUsdt = rows.Sum(x => x.RealizedPnlUsdt),
        GrossVolumeQuoteUsdt = rows.Sum(x => x.Price * x.Quantity),
        WinningSells = winning,
        LosingSells = losing,
        BestTradePnlUsdt = pnlList.Count > 0 ? pnlList.Max() : 0m,
        WorstTradePnlUsdt = pnlList.Count > 0 ? pnlList.Min() : 0m,
        ByBot = byBot
    };

    return Results.Ok(summary);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
