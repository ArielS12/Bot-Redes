using System.Text.Json;
using System.Globalization;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBinanceMarketService
{
    Task<List<MarketTicker>> GetMarketOverviewAsync(IEnumerable<string> symbols);
    Task<Dictionary<string, TechnicalMarketSnapshot>> GetTechnicalSnapshotsAsync(IEnumerable<string> symbols, string interval = "1m", int limit = 120);
}

public sealed class BinanceMarketService(
    HttpClient httpClient,
    ILogger<BinanceMarketService> logger,
    IBinanceSettingsService settingsService) : IBinanceMarketService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<MarketTicker>> GetMarketOverviewAsync(IEnumerable<string> symbols)
    {
        var selected = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        var includeAll = selected.Contains("*");

        if (selected.Count == 0 && !includeAll)
        {
            return [];
        }

        try
        {
            var settings = await settingsService.GetActiveSettingsAsync();
            var endpoint = $"{settingsService.ResolveMarketBaseUrl(settings)}/api/v3/ticker/24hr";
            var response = await GetWithRetryAsync(endpoint);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var all = JsonSerializer.Deserialize<List<BinanceTickerDto>>(json, JsonOptions) ?? [];

            var filtered = includeAll
                ? all
                : all.Where(x => selected.Contains(x.Symbol)).ToList();

            return filtered
                .Select(x => new MarketTicker
                {
                    Symbol = x.Symbol,
                    LastPrice = decimal.TryParse(x.LastPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m,
                    PriceChangePercent24h = decimal.TryParse(x.PriceChangePercent, NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0m,
                    QuoteVolume24h = decimal.TryParse(x.QuoteVolume, NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q : 0m
                })
                .OrderBy(x => x.Symbol)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer Binance, devolviendo datos vacios.");
            return selected.Select(s => new MarketTicker { Symbol = s }).ToList();
        }
    }

    public async Task<Dictionary<string, TechnicalMarketSnapshot>> GetTechnicalSnapshotsAsync(IEnumerable<string> symbols, string interval = "1m", int limit = 120)
    {
        var selected = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        if (selected.Count == 0)
        {
            return [];
        }

        var settings = await settingsService.GetActiveSettingsAsync();
        var baseUrl = settingsService.ResolveMarketBaseUrl(settings);
        var tasks = selected.Select(async symbol =>
        {
            var endpoint = $"{baseUrl}/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
            try
            {
                var response = await GetWithRetryAsync(endpoint);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadAsStringAsync();
                var raw = JsonSerializer.Deserialize<List<List<JsonElement>>>(payload, JsonOptions);
                var closes = raw?
                    .Where(x => x.Count > 4)
                    .Select(x => decimal.TryParse(x[4].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0m)
                    .Where(x => x > 0m)
                    .ToList() ?? [];
                var highs = raw?
                    .Where(x => x.Count > 2)
                    .Select(x => decimal.TryParse(x[2].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ? h : 0m)
                    .ToList() ?? [];
                var lows = raw?
                    .Where(x => x.Count > 3)
                    .Select(x => decimal.TryParse(x[3].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0m)
                    .ToList() ?? [];
                var quoteVolumes = raw?
                    .Where(x => x.Count > 7)
                    .Select(x => decimal.TryParse(x[7].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m)
                    .ToList() ?? [];
                if (closes.Count < 35)
                {
                    return (symbol, snapshot: (TechnicalMarketSnapshot?)null);
                }

                var emaFast = CalculateEma(closes, 9);
                var emaSlow = CalculateEma(closes, 21);
                var rsi14 = CalculateRsi(closes, 14);
                var (macdLine, signalLine, histogram, previousHistogram) = CalculateMacd(closes);
                var relativeVolume = CalculateRelativeVolume(quoteVolumes, 20);
                var atrPercent = CalculateAtrPercent(highs, lows, closes, 14);
                var volatilityPercent = CalculateVolatilityPercent(closes, 20);
                var snapshot = new TechnicalMarketSnapshot
                {
                    Symbol = symbol,
                    LastPrice = closes[^1],
                    EmaFast = emaFast,
                    EmaSlow = emaSlow,
                    Rsi14 = rsi14,
                    MacdLine = macdLine,
                    MacdSignal = signalLine,
                    MacdHistogram = histogram,
                    PreviousMacdHistogram = previousHistogram,
                    RelativeVolume = relativeVolume,
                    AtrPercent = atrPercent,
                    VolatilityPercent = volatilityPercent,
                    Interval = interval
                };
                return (symbol, snapshot);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No se pudo calcular snapshot tecnico para {Symbol}", symbol);
                return (symbol, snapshot: (TechnicalMarketSnapshot?)null);
            }
        });

        var result = await Task.WhenAll(tasks);
        return result
            .Where(x => x.snapshot is not null)
            .ToDictionary(x => x.symbol, x => x.snapshot!);
    }

    private static decimal CalculateEma(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count < period)
        {
            return values.Count == 0 ? 0m : values[^1];
        }

        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        for (var i = period; i < values.Count; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
        }

        return decimal.Round(ema, 8);
    }

    private static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period)
        {
            return 50m;
        }

        decimal gain = 0m;
        decimal loss = 0m;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta > 0m)
            {
                gain += delta;
            }
            else
            {
                loss += Math.Abs(delta);
            }
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;
        for (var i = period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var currentGain = delta > 0m ? delta : 0m;
            var currentLoss = delta < 0m ? Math.Abs(delta) : 0m;
            avgGain = ((avgGain * (period - 1)) + currentGain) / period;
            avgLoss = ((avgLoss * (period - 1)) + currentLoss) / period;
        }

        if (avgLoss == 0m)
        {
            return 100m;
        }

        var rs = avgGain / avgLoss;
        var rsi = 100m - (100m / (1m + rs));
        return decimal.Round(rsi, 4);
    }

    private static (decimal macdLine, decimal signalLine, decimal histogram, decimal previousHistogram) CalculateMacd(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 35)
        {
            return (0m, 0m, 0m, 0m);
        }

        var ema12Series = BuildEmaSeries(closes, 12);
        var ema26Series = BuildEmaSeries(closes, 26);
        var minCount = Math.Min(ema12Series.Count, ema26Series.Count);
        var macdSeries = new List<decimal>(minCount);
        var offset12 = ema12Series.Count - minCount;
        var offset26 = ema26Series.Count - minCount;
        for (var i = 0; i < minCount; i++)
        {
            macdSeries.Add(ema12Series[i + offset12] - ema26Series[i + offset26]);
        }

        var signalSeries = BuildEmaSeries(macdSeries, 9);
        var alignedCount = Math.Min(macdSeries.Count, signalSeries.Count);
        var macdOffset = macdSeries.Count - alignedCount;
        var signalOffset = signalSeries.Count - alignedCount;
        var histogramSeries = new List<decimal>(alignedCount);
        for (var i = 0; i < alignedCount; i++)
        {
            histogramSeries.Add(macdSeries[i + macdOffset] - signalSeries[i + signalOffset]);
        }

        var lastHistogram = histogramSeries.Count > 0 ? histogramSeries[^1] : 0m;
        var previousHistogram = histogramSeries.Count > 1 ? histogramSeries[^2] : lastHistogram;
        var macdLine = macdSeries.Count > 0 ? macdSeries[^1] : 0m;
        var signalLine = signalSeries.Count > 0 ? signalSeries[^1] : 0m;
        return (decimal.Round(macdLine, 8), decimal.Round(signalLine, 8), decimal.Round(lastHistogram, 8), decimal.Round(previousHistogram, 8));
    }

    private static List<decimal> BuildEmaSeries(IReadOnlyList<decimal> values, int period)
    {
        var series = new List<decimal>();
        if (values.Count < period)
        {
            return series;
        }

        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        series.Add(ema);
        for (var i = period; i < values.Count; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
            series.Add(ema);
        }

        return series;
    }

    private static decimal CalculateRelativeVolume(IReadOnlyList<decimal> quoteVolumes, int lookback)
    {
        if (quoteVolumes.Count < lookback + 1)
        {
            return 1m;
        }

        var current = quoteVolumes[^1];
        var avg = quoteVolumes.Skip(Math.Max(0, quoteVolumes.Count - lookback - 1)).Take(lookback).DefaultIfEmpty(0m).Average();
        if (avg <= 0m)
        {
            return 1m;
        }

        return decimal.Round(current / avg, 4);
    }

    private static decimal CalculateAtrPercent(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period)
    {
        var count = Math.Min(highs.Count, Math.Min(lows.Count, closes.Count));
        if (count <= period + 1)
        {
            return 0m;
        }

        var trs = new List<decimal>(count - 1);
        for (var i = 1; i < count; i++)
        {
            var high = highs[i];
            var low = lows[i];
            var prevClose = closes[i - 1];
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trs.Add(Math.Max(0m, tr));
        }

        if (trs.Count < period)
        {
            return 0m;
        }

        var atr = trs.Skip(Math.Max(0, trs.Count - period)).Take(period).DefaultIfEmpty(0m).Average();
        var last = closes[^1];
        if (last <= 0m)
        {
            return 0m;
        }

        return decimal.Round((atr / last) * 100m, 4);
    }

    private static decimal CalculateVolatilityPercent(IReadOnlyList<decimal> closes, int lookback)
    {
        if (closes.Count < lookback + 1)
        {
            return 0m;
        }

        var returns = new List<decimal>(lookback);
        var start = closes.Count - lookback - 1;
        for (var i = start + 1; i < closes.Count; i++)
        {
            var prev = closes[i - 1];
            if (prev <= 0m)
            {
                continue;
            }

            returns.Add((closes[i] - prev) / prev);
        }

        if (returns.Count == 0)
        {
            return 0m;
        }

        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).DefaultIfEmpty(0m).Average();
        var std = (decimal)Math.Sqrt((double)Math.Max(0m, variance));
        return decimal.Round(std * 100m, 4);
    }

    private sealed class BinanceTickerDto
    {
        public string Symbol { get; set; } = string.Empty;
        public string LastPrice { get; set; } = "0";
        public string PriceChangePercent { get; set; } = "0";
        public string QuoteVolume { get; set; } = "0";
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string endpoint)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await httpClient.GetAsync(endpoint);
            if ((int)response.StatusCode != 429 && (int)response.StatusCode != 418)
            {
                return response;
            }

            var delay = await ResolveRetryDelayAsync(response, attempt);
            response.Dispose();
            await Task.Delay(delay);
        }

        return await httpClient.GetAsync(endpoint);
    }

    private static async Task<TimeSpan> ResolveRetryDelayAsync(HttpResponseMessage response, int attempt)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
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
}
