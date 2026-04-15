using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBinanceTradeExecutionService
{
    Task<bool> IsRealTradingEnabledAsync(CancellationToken ct = default);
    Task<decimal> GetQuoteAssetFreeBalanceAsync(string quoteAsset, CancellationToken ct = default);
    Task<BinanceAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default);
    Task<BinanceHealthView> GetHealthAsync(CancellationToken ct = default);
    string GetLastExecutionError();
    Task<TradeFillResult?> MarketBuyAsync(string symbol, decimal quoteOrderQty, Guid? botId = null, CancellationToken ct = default);
    Task<TradeFillResult?> MarketSellAsync(string symbol, decimal quantity, Guid? botId = null, CancellationToken ct = default);
}

public sealed class TradeFillResult
{
    public decimal ExecutedQuantity { get; init; }
    public decimal AveragePrice { get; init; }
}

public sealed class BinanceTradeExecutionService(
    AppDbContext dbContext,
    HttpClient httpClient,
    IBinanceSettingsService settingsService,
    ILogger<BinanceTradeExecutionService> logger) : IBinanceTradeExecutionService
{
    private string _lastExecutionError = string.Empty;
    private const long DefaultRecvWindow = 5000;
    private long _timeOffsetMs;
    private DateTime? _lastTimeSyncUtc;
    private int _rateLimit429Count;
    private int _rateLimit418Count;
    private int _reconcileAttempts;
    private int _reconcileRecovered;

    public string GetLastExecutionError() => _lastExecutionError;

    public async Task<bool> IsRealTradingEnabledAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        return CanTradeReal(settings);
    }

    public async Task<TradeFillResult?> MarketBuyAsync(string symbol, decimal quoteOrderQty, Guid? botId = null, CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (!CanTradeReal(settings))
        {
            _lastExecutionError = "Live trading deshabilitado por configuracion/guardas.";
            await WriteAuditAsync(botId, symbol, "BUY", "preflight", "blocked", _lastExecutionError, quoteOrderQty, 0m, 0m, 0m, 0, true);
            return null;
        }

        var baseUrl = settingsService.ResolveMarketBaseUrl(settings);
        var quote = decimal.Round(Math.Max(quoteOrderQty, 0m), 8, MidpointRounding.ToZero);
        if (quote <= 0m)
        {
            await WriteAuditAsync(botId, symbol, "BUY", "preflight", "invalid", "Monto de compra no valido.", quoteOrderQty, 0m, 0m, 0m, 0, true);
            return null;
        }
        var filters = await GetExchangeInfoAsync(baseUrl, symbol, ct);
        if (filters is null)
        {
            _lastExecutionError = $"No se pudo leer filtros de simbolo para {symbol}.";
            await WriteAuditAsync(botId, symbol, "BUY", "preflight", "failed", _lastExecutionError, quote, 0m, 0m, 0m, 0, true);
            return null;
        }
        var minNotional = Math.Max(filters.MinNotional, filters.NotionalMin);
        if (quote < minNotional)
        {
            _lastExecutionError = $"Monto {Fmt(quote)} menor a notional minimo {Fmt(minNotional)} para {symbol}.";
            await WriteAuditAsync(botId, symbol, "BUY", "preflight", "blocked", _lastExecutionError, quote, 0m, 0m, 0m, 0, true);
            return null;
        }
        var clientOrderId = BuildClientOrderId("BUY", symbol, botId);
        var query = $"symbol={symbol}&side=BUY&type=MARKET&quoteOrderQty={Fmt(quote)}&newClientOrderId={clientOrderId}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
        return await SendOrderAsync(baseUrl, settings, query, botId, symbol, "BUY", quote, 0m, ct);
    }

    public async Task<decimal> GetQuoteAssetFreeBalanceAsync(string quoteAsset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            return 0m;
        }

        var settings = await settingsService.GetActiveSettingsAsync();
        if (!CanTradeReal(settings))
        {
            return 0m;
        }

        var account = await GetAccountInfoAsync(settings, ct);
        var balance = account?.Balances?.FirstOrDefault(x => x.Asset.Equals(quoteAsset.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase));
        return Parse(balance?.Free);
    }

    public async Task<BinanceAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var envName = settings.Environment == BinanceEnvironment.Production ? "Production" : "Sandbox";
        if (!CanTradeReal(settings))
        {
            return new BinanceAccountSummary
            {
                Connected = false,
                EnvironmentName = envName,
                Message = "Trading real no habilitado o faltan credenciales/API."
            };
        }

        try
        {
            var account = await GetAccountInfoAsync(settings, ct);
            var usdt = account?.Balances?.FirstOrDefault(x => x.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase));
            var usdc = account?.Balances?.FirstOrDefault(x => x.Asset.Equals("USDC", StringComparison.OrdinalIgnoreCase));
            var fdusd = account?.Balances?.FirstOrDefault(x => x.Asset.Equals("FDUSD", StringComparison.OrdinalIgnoreCase));
            return new BinanceAccountSummary
            {
                Connected = account is not null,
                EnvironmentName = envName,
                UsdtFree = Parse(usdt?.Free),
                UsdtLocked = Parse(usdt?.Locked),
                UsdcFree = Parse(usdc?.Free),
                UsdcLocked = Parse(usdc?.Locked),
                FdusdFree = Parse(fdusd?.Free),
                FdusdLocked = Parse(fdusd?.Locked),
                Message = account is null ? "No se pudo leer cuenta." : "Conexion OK"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer resumen de cuenta Binance.");
            return new BinanceAccountSummary
            {
                Connected = false,
                EnvironmentName = envName,
                Message = "Error consultando cuenta. Verifica API Key, permisos y entorno."
            };
        }
    }

    public async Task<BinanceHealthView> GetHealthAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var envName = settings.Environment == BinanceEnvironment.Production ? "Production" : "Sandbox";
        var liveEnabled = CanTradeReal(settings);
        if (liveEnabled)
        {
            await SyncServerTimeOffsetAsync(settingsService.ResolveMarketBaseUrl(settings), ct);
        }

        var attempts = _reconcileAttempts;
        var recovered = _reconcileRecovered;
        var recoveryRate = attempts == 0 ? 0m : decimal.Round((recovered * 100m) / attempts, 2);
        return new BinanceHealthView
        {
            EnvironmentName = envName,
            TradingLiveEnabled = liveEnabled,
            TimeOffsetMs = _timeOffsetMs,
            LastTimeSyncUtc = _lastTimeSyncUtc,
            RateLimit429Count = _rateLimit429Count,
            RateLimit418Count = _rateLimit418Count,
            ReconcileAttempts = attempts,
            ReconcileRecovered = recovered,
            ReconcileRecoveryRatePercent = recoveryRate,
            LastExecutionError = _lastExecutionError
        };
    }

    public async Task<TradeFillResult?> MarketSellAsync(string symbol, decimal quantity, Guid? botId = null, CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (!CanTradeReal(settings))
        {
            _lastExecutionError = "Live trading deshabilitado por configuracion/guardas.";
            await WriteAuditAsync(botId, symbol, "SELL", "preflight", "blocked", _lastExecutionError, 0m, quantity, 0m, 0m, 0, true);
            return null;
        }

        var baseUrl = settingsService.ResolveMarketBaseUrl(settings);
        var exchange = await GetExchangeInfoAsync(baseUrl, symbol, ct);
        var normalized = NormalizeQuantity(quantity, exchange?.StepSize ?? 0.000001m, exchange?.MinQty ?? 0m);
        if (normalized <= 0m)
        {
            _lastExecutionError = $"Cantidad invalida para {symbol} tras normalizacion.";
            await WriteAuditAsync(botId, symbol, "SELL", "preflight", "blocked", _lastExecutionError, 0m, quantity, 0m, 0m, 0, true);
            return null;
        }

        var clientOrderId = BuildClientOrderId("SELL", symbol, botId);
        var query = $"symbol={symbol}&side=SELL&type=MARKET&quantity={Fmt(normalized)}&newClientOrderId={clientOrderId}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
        return await SendOrderAsync(baseUrl, settings, query, botId, symbol, "SELL", 0m, normalized, ct);
    }

    private async Task<TradeFillResult?> SendOrderAsync(
        string baseUrl,
        BinanceConnectionSettings settings,
        string query,
        Guid? botId,
        string symbol,
        string side,
        decimal requestedQuote,
        decimal requestedBase,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            _lastExecutionError = string.Empty;
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var clientOrderId = ReadQueryValue(query, "newClientOrderId");
            var signature = Sign(query, settings.ApiSecret);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/order?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
            var response = await SendWithRateLimitRetryAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();
            var order = JsonSerializer.Deserialize<OrderResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (order is null)
            {
                return null;
            }

            var qty = Parse(order.ExecutedQty);
            var cummQuote = Parse(order.CummulativeQuoteQty);
            var price = qty > 0 ? cummQuote / qty : 0m;
            var latencyMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
            await WriteAuditAsync(botId, symbol, side, "execution", "success", "Orden ejecutada.", requestedQuote, requestedBase, qty, price, latencyMs, true);
            return new TradeFillResult
            {
                ExecutedQuantity = qty,
                AveragePrice = price
            };
        }
        catch (Exception ex)
        {
            if (LooksLikeUnknownExecution(ex))
            {
                var clientOrderId = ReadQueryValue(query, "newClientOrderId");
                var recovered = await TryRecoverOrderAsync(baseUrl, settings, symbol, side, clientOrderId, botId, requestedQuote, requestedBase, startedAt, ct);
                if (recovered is not null)
                {
                    return recovered;
                }
            }
            _lastExecutionError = ClassifyOrderError(ex);
            var latencyMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
            await WriteAuditAsync(botId, symbol, side, "execution", "failed", _lastExecutionError, requestedQuote, requestedBase, 0m, 0m, latencyMs, true);
            logger.LogWarning(ex, "Error enviando orden real a Binance.");
            return null;
        }
    }

    private async Task WriteAuditAsync(Guid? botId, string symbol, string side, string stage, string status, string message, decimal requestedQuote, decimal requestedBase, decimal executedQty, decimal executedPrice, int latencyMs, bool isLive)
    {
        dbContext.OrderAuditEvents.Add(new OrderAuditEvent
        {
            BotId = botId,
            Symbol = symbol,
            Side = side,
            Stage = stage,
            Status = status,
            Message = message,
            RequestedQuoteQty = requestedQuote,
            RequestedBaseQty = requestedBase,
            ExecutedQty = executedQty,
            ExecutedPrice = executedPrice,
            LatencyMs = latencyMs,
            IsLive = isLive,
            CreatedAtUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<AccountInfoResponse?> GetAccountInfoAsync(BinanceConnectionSettings settings, CancellationToken ct)
    {
        var baseUrl = settingsService.ResolveMarketBaseUrl(settings);
        await SyncServerTimeOffsetAsync(baseUrl, ct);
        var query = $"recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
        var signature = Sign(query, settings.ApiSecret);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/account?{query}&signature={signature}");
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
        var response = await SendWithRateLimitRetryAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<AccountInfoResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private async Task<SymbolFilters?> GetExchangeInfoAsync(string baseUrl, string symbol, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/exchangeInfo?symbol={symbol}");
            var response = await SendWithRateLimitRetryAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<ExchangeInfoResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var s = data?.Symbols?.FirstOrDefault();
            var lot = s?.Filters?.FirstOrDefault(x => x.FilterType == "LOT_SIZE");
            var marketLot = s?.Filters?.FirstOrDefault(x => x.FilterType == "MARKET_LOT_SIZE");
            var minNotional = s?.Filters?.FirstOrDefault(x => x.FilterType == "MIN_NOTIONAL");
            var notional = s?.Filters?.FirstOrDefault(x => x.FilterType == "NOTIONAL");
            return new SymbolFilters
            {
                MinQty = Parse((marketLot?.MinQty ?? "0")) > 0m ? Parse(marketLot?.MinQty ?? "0") : Parse(lot?.MinQty ?? "0"),
                MaxQty = Parse((marketLot?.MaxQty ?? "0")) > 0m ? Parse(marketLot?.MaxQty ?? "0") : Parse(lot?.MaxQty ?? "0"),
                StepSize = Parse((marketLot?.StepSize ?? "0")) > 0m ? Parse(marketLot?.StepSize ?? "0") : Parse(lot?.StepSize ?? "0"),
                MinNotional = Parse(minNotional?.MinNotional ?? "0"),
                NotionalMin = Parse(notional?.MinNotional ?? "0")
            };
        }
        catch
        {
            return null;
        }
    }

    private static decimal NormalizeQuantity(decimal qty, decimal stepSize, decimal minQty)
    {
        if (stepSize <= 0)
        {
            return qty;
        }

        var steps = Math.Floor(qty / stepSize);
        var normalized = steps * stepSize;
        if (normalized < minQty)
        {
            return 0m;
        }

        return decimal.Round(normalized, 8, MidpointRounding.ToZero);
    }

    private static bool CanTradeReal(BinanceConnectionSettings settings) =>
        settings.IsEnabled &&
        settings.ExecutionMode == TradeExecutionMode.Live &&
        settings.LiveEnabledByChecklist &&
        !settings.GlobalKillSwitch &&
        (settings.Environment != BinanceEnvironment.Production || settings.LiveSafetyConfirmed) &&
        !string.IsNullOrWhiteSpace(settings.ApiKey) &&
        !string.IsNullOrWhiteSpace(settings.ApiSecret);

    private static string ClassifyOrderError(Exception ex)
    {
        var message = ex.Message.ToUpperInvariant();
        if (message.Contains("INSUFFICIENT")) return "Balance insuficiente para ejecutar orden.";
        if (message.Contains("MIN_NOTIONAL")) return "Orden rechazada por MIN_NOTIONAL.";
        if (message.Contains("LOT_SIZE") || message.Contains("PRECISION")) return "Orden rechazada por precision/tamano de lote.";
        if (message.Contains("TIMESTAMP")) return "Error de timestamp/sincronizacion con Binance.";
        if (message.Contains("TOO MUCH REQUEST WEIGHT") || message.Contains("429")) return "Rate limit excedido temporalmente (429).";
        if (message.Contains("418") || message.Contains("BANNED")) return "IP temporalmente bloqueada por exceso de peticiones.";
        if (message.Contains("SEND STATUS UNKNOWN") || message.Contains("-1007")) return "Timeout de Binance con estado de orden incierto.";
        return "Error enviando orden a Binance.";
    }

    private static string Fmt(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long NowMsWithOffset() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
    private static decimal Parse(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string Sign(string query, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class OrderResponse
    {
        public string ExecutedQty { get; set; } = "0";
        public string CummulativeQuoteQty { get; set; } = "0";
    }

    private sealed class ExchangeInfoResponse
    {
        public List<SymbolInfo> Symbols { get; set; } = [];
    }

    private sealed class SymbolInfo
    {
        public List<FilterInfo> Filters { get; set; } = [];
    }

    private sealed class FilterInfo
    {
        public string FilterType { get; set; } = string.Empty;
        public string MinQty { get; set; } = "0";
        public string MaxQty { get; set; } = "0";
        public string StepSize { get; set; } = "0";
        public string MinNotional { get; set; } = "0";
    }

    private sealed class SymbolFilters
    {
        public decimal MinQty { get; init; }
        public decimal MaxQty { get; init; }
        public decimal StepSize { get; init; }
        public decimal MinNotional { get; init; }
        public decimal NotionalMin { get; init; }
    }

    private sealed class AccountInfoResponse
    {
        public List<AccountBalance> Balances { get; set; } = [];
    }

    private sealed class AccountBalance
    {
        public string Asset { get; set; } = string.Empty;
        public string Free { get; set; } = "0";
        public string Locked { get; set; } = "0";
    }

    private sealed class ServerTimeResponse
    {
        public long ServerTime { get; set; }
    }

    private async Task SyncServerTimeOffsetAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/time");
            var response = await SendWithRateLimitRetryAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            var time = JsonSerializer.Deserialize<ServerTimeResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (time is not null && time.ServerTime > 0)
            {
                _timeOffsetMs = time.ServerTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _lastTimeSyncUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // Mantiene offset previo; evita bloquear trading por falla transitoria de sincronizacion.
        }
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var cloned = await CloneRequestAsync(request);
            var response = await httpClient.SendAsync(cloned, ct);
            if ((int)response.StatusCode != 429 && (int)response.StatusCode != 418)
            {
                return response;
            }
            if ((int)response.StatusCode == 429) _rateLimit429Count++;
            if ((int)response.StatusCode == 418) _rateLimit418Count++;

            var delay = await ResolveRetryDelayAsync(response, attempt, ct);
            response.Dispose();
            await Task.Delay(delay, ct);
        }

        var last = await CloneRequestAsync(request);
        return await httpClient.SendAsync(last, ct);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static async Task<TimeSpan> ResolveRetryDelayAsync(HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorObj) &&
                errorObj.TryGetProperty("data", out var dataObj) &&
                dataObj.TryGetProperty("retryAfter", out var retryAfter) &&
                retryAfter.TryGetInt64(out var epochMs))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var ms = Math.Max(250, epochMs - now);
                return TimeSpan.FromMilliseconds(Math.Min(ms, 15000));
            }
        }
        catch
        {
        }

        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
    }

    private static string BuildClientOrderId(string side, string symbol, Guid? botId)
    {
        var botPart = botId?.ToString("N")[..8] ?? "manual";
        var sym = symbol.Length > 8 ? symbol[..8] : symbol;
        var nonce = Guid.NewGuid().ToString("N")[..8];
        return $"tbp-{side.ToLowerInvariant()}-{sym.ToLowerInvariant()}-{botPart}-{nonce}";
    }

    private static string ReadQueryValue(string query, string key)
    {
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                return p[(key.Length + 1)..];
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeUnknownExecution(Exception ex)
    {
        var text = ex.ToString().ToUpperInvariant();
        return text.Contains("TIMEOUT WAITING FOR RESPONSE FROM BACKEND SERVER") ||
               text.Contains("SEND STATUS UNKNOWN") ||
               text.Contains("-1007");
    }

    private async Task<TradeFillResult?> TryRecoverOrderAsync(
        string baseUrl,
        BinanceConnectionSettings settings,
        string symbol,
        string side,
        string clientOrderId,
        Guid? botId,
        decimal requestedQuote,
        decimal requestedBase,
        DateTime startedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            return null;
        }

        try
        {
            _reconcileAttempts++;
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"symbol={symbol}&origClientOrderId={clientOrderId}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.ApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/order?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
            var response = await SendWithRateLimitRetryAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();
            var order = JsonSerializer.Deserialize<OrderStatusResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (order is null || string.IsNullOrWhiteSpace(order.Status))
            {
                return null;
            }

            var qty = Parse(order.ExecutedQty);
            var cummQuote = Parse(order.CummulativeQuoteQty);
            var price = qty > 0 ? cummQuote / qty : 0m;
            var statusUpper = order.Status.ToUpperInvariant();
            var latencyMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
            if (statusUpper is "FILLED" or "PARTIALLY_FILLED")
            {
                _reconcileRecovered++;
                await WriteAuditAsync(botId, symbol, side, "reconcile", "success", $"Orden recuperada por clientOrderId={clientOrderId}, status={order.Status}.", requestedQuote, requestedBase, qty, price, latencyMs, true);
                return new TradeFillResult
                {
                    ExecutedQuantity = qty,
                    AveragePrice = price
                };
            }

            await WriteAuditAsync(botId, symbol, side, "reconcile", "info", $"Orden encontrada con status={order.Status} y sin ejecucion confirmada.", requestedQuote, requestedBase, qty, price, latencyMs, true);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class OrderStatusResponse
    {
        public string Status { get; set; } = string.Empty;
        public string ExecutedQty { get; set; } = "0";
        public string CummulativeQuoteQty { get; set; } = "0";
    }
}
