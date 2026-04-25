using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBinanceSettingsService
{
    Task<BinanceConnectionSettings> GetActiveSettingsAsync();
    Task<BinanceSettingsView> GetViewAsync();
    Task<BinanceSettingsView> UpdateAsync(UpdateBinanceSettingsRequest request);
    Task<BinanceSettingsView> ArmLiveTradingAsync(LiveChecklistRequest request);
    string ResolveMarketBaseUrl(BinanceConnectionSettings settings);
}

public sealed class BinanceSettingsService(AppDbContext dbContext) : IBinanceSettingsService
{
    public async Task<BinanceConnectionSettings> GetActiveSettingsAsync()
    {
        var settings = await dbContext.BinanceSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (settings is not null)
        {
            return settings;
        }

        settings = new BinanceConnectionSettings();
        dbContext.BinanceSettings.Add(settings);
        await dbContext.SaveChangesAsync();
        return settings;
    }

    public async Task<BinanceSettingsView> GetViewAsync()
    {
        var settings = await GetActiveSettingsAsync();
        return ToView(settings);
    }

    public async Task<BinanceSettingsView> UpdateAsync(UpdateBinanceSettingsRequest request)
    {
        if (request.ExecutionMode == TradeExecutionMode.Live &&
            request.Environment == BinanceEnvironment.Production &&
            !request.LiveSafetyConfirmed)
        {
            throw new InvalidOperationException("Debes confirmar seguridad para activar Live en Produccion.");
        }

        var settings = await GetActiveSettingsAsync();
        if (request.LiveEnabledByChecklist && !settings.LiveEnabledByChecklist)
        {
            throw new InvalidOperationException("Live solo puede armarse mediante checklist (/arm-live).");
        }
        settings.IsEnabled = request.IsEnabled;
        settings.ExecutionMode = request.ExecutionMode;
        settings.Environment = request.Environment;
        settings.LiveSafetyConfirmed = request.LiveSafetyConfirmed;
        settings.GlobalKillSwitch = request.GlobalKillSwitch;
        settings.MaxAutoBots = Math.Clamp(request.MaxAutoBots, 0, 50);
        settings.AutoControlTuningEnabled = request.AutoControlTuningEnabled;
        settings.SupervisorInactiveMinutes = Math.Clamp(request.SupervisorInactiveMinutes, 60, 300);
        settings.RebalanceOutOfTopCycles = Math.Clamp(request.RebalanceOutOfTopCycles, 2, 6);
        settings.MinActiveBeforePauseMinutes = Math.Clamp(request.MinActiveBeforePauseMinutes, 10, 90);
        settings.MinStoppedBeforeReactivateMinutes = Math.Clamp(request.MinStoppedBeforeReactivateMinutes, 2, 30);
        settings.MinStoppedAfterRiskStopMinutes = Math.Clamp(request.MinStoppedAfterRiskStopMinutes, 15, 240);
        settings.MlEnabled = request.MlEnabled;
        settings.MlShadowMode = request.MlShadowMode;
        settings.MlMinWinProbability = decimal.Clamp(request.MlMinWinProbability, 0.50m, 0.90m);
        settings.MlMinSamples = Math.Clamp(request.MlMinSamples, 30, 5000);
        settings.LiveEnabledByChecklist = request.LiveEnabledByChecklist && settings.LiveEnabledByChecklist;
        settings.ApiKey = request.ApiKey.Trim();
        var newSecret = request.ApiSecret.Trim();
        if (!string.IsNullOrWhiteSpace(newSecret))
        {
            settings.ApiSecret = newSecret;
        }
        settings.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        return ToView(settings);
    }

    public async Task<BinanceSettingsView> ArmLiveTradingAsync(LiveChecklistRequest request)
    {
        if (!request.ConfirmApiTradingPermission || !request.ConfirmIpWhitelist || !request.ConfirmSmallSizeFirst)
        {
            throw new InvalidOperationException("Checklist incompleto. Debes confirmar todos los puntos para armar Live.");
        }

        var settings = await GetActiveSettingsAsync();
        settings.LiveEnabledByChecklist = true;
        settings.GlobalKillSwitch = false;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        return ToView(settings);
    }

    public string ResolveMarketBaseUrl(BinanceConnectionSettings settings) =>
        settings.Environment == BinanceEnvironment.Sandbox
            ? "https://testnet.binance.vision"
            : "https://api.binance.com";

    private static BinanceSettingsView ToView(BinanceConnectionSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        ExecutionMode = settings.ExecutionMode,
        Environment = settings.Environment,
        LiveSafetyConfirmed = settings.LiveSafetyConfirmed,
        LiveEnabledByChecklist = settings.LiveEnabledByChecklist,
        GlobalKillSwitch = settings.GlobalKillSwitch,
        MaxAutoBots = Math.Clamp(settings.MaxAutoBots, 0, 50),
        AutoControlTuningEnabled = settings.AutoControlTuningEnabled,
        SupervisorInactiveMinutes = Math.Clamp(settings.SupervisorInactiveMinutes <= 0 ? 180 : settings.SupervisorInactiveMinutes, 60, 300),
        RebalanceOutOfTopCycles = Math.Clamp(settings.RebalanceOutOfTopCycles <= 0 ? 3 : settings.RebalanceOutOfTopCycles, 2, 6),
        MinActiveBeforePauseMinutes = Math.Clamp(settings.MinActiveBeforePauseMinutes <= 0 ? 20 : settings.MinActiveBeforePauseMinutes, 10, 90),
        MinStoppedBeforeReactivateMinutes = Math.Clamp(settings.MinStoppedBeforeReactivateMinutes <= 0 ? 5 : settings.MinStoppedBeforeReactivateMinutes, 2, 30),
        MinStoppedAfterRiskStopMinutes = Math.Clamp(settings.MinStoppedAfterRiskStopMinutes <= 0 ? 45 : settings.MinStoppedAfterRiskStopMinutes, 15, 240),
        MlEnabled = settings.MlEnabled,
        MlShadowMode = settings.MlShadowMode,
        MlMinWinProbability = settings.MlMinWinProbability <= 0m ? 0.55m : decimal.Clamp(settings.MlMinWinProbability, 0.50m, 0.90m),
        MlMinSamples = settings.MlMinSamples <= 0 ? 80 : Math.Clamp(settings.MlMinSamples, 30, 5000),
        LastAutoControlTuneUtc = settings.LastAutoControlTuneUtc,
        ApiKey = settings.ApiKey,
        ApiSecretMasked = string.IsNullOrWhiteSpace(settings.ApiSecret)
            ? string.Empty
            : new string('*', Math.Min(12, settings.ApiSecret.Length)),
        UpdatedAtUtc = settings.UpdatedAtUtc
    };
}
