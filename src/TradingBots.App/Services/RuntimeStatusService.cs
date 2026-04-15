namespace TradingBots.App.Services;

public interface IRuntimeStatusService
{
    DateTime? LastAutoTraderRunUtc { get; }
    string LastAutoTraderStatus { get; }
    void MarkAutoTraderRun(string status);
}

public sealed class RuntimeStatusService : IRuntimeStatusService
{
    public DateTime? LastAutoTraderRunUtc { get; private set; }
    public string LastAutoTraderStatus { get; private set; } = "Sin ejecucion";

    public void MarkAutoTraderRun(string status)
    {
        LastAutoTraderRunUtc = DateTime.UtcNow;
        LastAutoTraderStatus = status;
    }
}
