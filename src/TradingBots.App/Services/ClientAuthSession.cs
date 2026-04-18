using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.JSInterop;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public sealed class ClientAuthSession(IJSRuntime js)
{
    public string? Token { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }

    public event Action? SessionChanged;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(Token) &&
        ExpiresAtUtc is { } exp &&
        exp > DateTime.UtcNow;

    public async Task InitializeFromBrowserAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("tradingBotsAuth.get");
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<StoredAuthDto>(json);
            if (dto is null || string.IsNullOrEmpty(dto.T))
            {
                return;
            }

            Token = dto.T;
            ExpiresAtUtc = dto.E;
            if (!IsAuthenticated)
            {
                await ClearStorageAsync();
                Token = null;
                ExpiresAtUtc = null;
            }

            SessionChanged?.Invoke();
        }
        catch (JSException)
        {
            // Sin JS (prerender u otro entorno)
        }
        catch (InvalidOperationException)
        {
            // Prerender
        }
    }

    public async Task SetSessionAsync(LoginResponse response, bool persistToBrowser)
    {
        Token = response.Token;
        ExpiresAtUtc = response.ExpiresAtUtc;
        if (persistToBrowser)
        {
            await PersistToStorageAsync();
        }
        else
        {
            await ClearStorageAsync();
        }

        SessionChanged?.Invoke();
    }

    public async Task SignOutAsync()
    {
        Token = null;
        ExpiresAtUtc = null;
        await ClearStorageAsync();
        SessionChanged?.Invoke();
    }

    public void ApplyBearer(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = null;
        if (IsAuthenticated && !string.IsNullOrEmpty(Token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
    }

    private async Task PersistToStorageAsync()
    {
        if (string.IsNullOrEmpty(Token) || ExpiresAtUtc is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new StoredAuthDto { T = Token, E = ExpiresAtUtc.Value });
        await js.InvokeVoidAsync("tradingBotsAuth.set", json);
    }

    private async Task ClearStorageAsync()
    {
        try
        {
            await js.InvokeVoidAsync("tradingBotsAuth.remove");
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class StoredAuthDto
    {
        public string T { get; set; } = string.Empty;
        public DateTime E { get; set; }
    }
}
