using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IControlAutotuneService
{
    Task<string> TuneAsync(CancellationToken ct = default);
}

public sealed class ControlAutotuneService(
    AppDbContext dbContext,
    IBinanceSettingsService settingsService,
    ILogger<ControlAutotuneService> logger) : IControlAutotuneService
{
    private static readonly TimeSpan TuneInterval = TimeSpan.FromHours(24);

    public async Task<string> TuneAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (!settings.AutoControlTuningEnabled)
        {
            return "AutoTune off";
        }

        var now = DateTime.UtcNow;
        if (settings.LastAutoControlTuneUtc is not null &&
            (now - settings.LastAutoControlTuneUtc.Value) < TuneInterval)
        {
            return "AutoTune cooldown";
        }

        var fromUtc = now.AddDays(-7);
        var autoBotIds = await dbContext.Bots
            .Where(x => x.IsAutoManaged && !x.AutoResumeBlocked)
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (autoBotIds.Count == 0)
        {
            settings.LastAutoControlTuneUtc = now;
            await dbContext.SaveChangesAsync(ct);
            return "AutoTune no autobots";
        }

        var sells = await dbContext.Trades
            .Where(x => autoBotIds.Contains(x.BotId) && x.Side == "SELL" && x.ExecutedAtUtc >= fromUtc)
            .ToListAsync(ct);

        var closed = sells.Count;
        var sumWins = sells.Where(x => x.RealizedPnlUsdt > 0m).Sum(x => x.RealizedPnlUsdt);
        var sumLossAbs = Math.Abs(sells.Where(x => x.RealizedPnlUsdt < 0m).Sum(x => x.RealizedPnlUsdt));
        var pf = sumLossAbs <= 0m ? (sumWins > 0m ? 999m : 0m) : sumWins / sumLossAbs;
        var net = sells.Sum(x => x.RealizedPnlUsdt);
        var expectancy = closed == 0 ? 0m : net / closed;
        var tradesPerDay = closed / 7m;

        var supervisor = Math.Clamp(settings.SupervisorInactiveMinutes <= 0 ? 120 : settings.SupervisorInactiveMinutes, 60, 240);
        var outCycles = Math.Clamp(settings.RebalanceOutOfTopCycles <= 0 ? 3 : settings.RebalanceOutOfTopCycles, 2, 6);
        var activeMin = Math.Clamp(settings.MinActiveBeforePauseMinutes <= 0 ? 20 : settings.MinActiveBeforePauseMinutes, 10, 90);
        var reactivateMin = Math.Clamp(settings.MinStoppedBeforeReactivateMinutes <= 0 ? 5 : settings.MinStoppedBeforeReactivateMinutes, 2, 30);

        if (closed < 30)
        {
            // Muestra baja: conservador para evitar overfitting.
            supervisor = Math.Min(240, supervisor + 20);
            outCycles = Math.Min(6, outCycles + 1);
            activeMin = Math.Min(90, activeMin + 5);
            reactivateMin = Math.Min(30, reactivateMin + 1);
        }
        else if (pf < 1.0m || expectancy < 0m)
        {
            // Rendimiento debil: reduce churn y endurece cambios.
            supervisor = Math.Min(240, supervisor + 20);
            outCycles = Math.Min(6, outCycles + 1);
            activeMin = Math.Min(90, activeMin + 10);
            reactivateMin = Math.Min(30, reactivateMin + 2);
        }
        else if (pf >= 1.2m && expectancy > 0m && tradesPerDay < 5m)
        {
            // Calidad buena pero poca actividad: relaja gradualmente.
            supervisor = Math.Max(60, supervisor - 20);
            outCycles = Math.Max(2, outCycles - 1);
            activeMin = Math.Max(10, activeMin - 5);
            reactivateMin = Math.Max(2, reactivateMin - 1);
        }

        var changed =
            settings.SupervisorInactiveMinutes != supervisor ||
            settings.RebalanceOutOfTopCycles != outCycles ||
            settings.MinActiveBeforePauseMinutes != activeMin ||
            settings.MinStoppedBeforeReactivateMinutes != reactivateMin;

        settings.SupervisorInactiveMinutes = supervisor;
        settings.RebalanceOutOfTopCycles = outCycles;
        settings.MinActiveBeforePauseMinutes = activeMin;
        settings.MinStoppedBeforeReactivateMinutes = reactivateMin;
        settings.LastAutoControlTuneUtc = now;
        settings.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(ct);

        if (changed)
        {
            logger.LogInformation(
                "AutoTune control params updated: Inactive={Inactive}, OutCycles={OutCycles}, MinActive={MinActive}, MinStopped={MinStopped}, PF={PF:0.###}, Expectancy={Expectancy:0.####}, Trades={Trades}",
                supervisor, outCycles, activeMin, reactivateMin, pf, expectancy, closed);
            return "AutoTune updated";
        }

        return "AutoTune no-change";
    }
}
