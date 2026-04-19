namespace TradingBots.App.Services;

/// <summary>Contador global de peticiones en curso para mostrar un indicador en la UI (Blazor Server, por circuito).</summary>
public sealed class UiLoadingState
{
    private int _depth;

    public bool IsBusy => _depth > 0;

    public event Action? Changed;

    public void Begin()
    {
        _depth++;
        Changed?.Invoke();
    }

    public void End()
    {
        if (_depth <= 0)
        {
            return;
        }

        _depth--;
        Changed?.Invoke();
    }
}
