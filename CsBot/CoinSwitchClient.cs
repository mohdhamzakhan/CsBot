using System.Globalization;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CsBot;

/// <summary>
/// CoinSwitch PRO client (Spot v2 + Futures). Auth per official docs: headers X-AUTH-APIKEY / X-AUTH-SIGNATURE / X-AUTH-EPOCH;
/// signature = hex(Ed25519(METHOD + path_with_query + epoch)). Request bodies are NOT part of the signature.
/// Keys come from environment variables (CS_API_KEY / CS_SECRET_KEY), never source code.
/// </summary>
public sealed class CoinSwitchClient : IDisposable
{
    public const string FuturesExchange = "EXCHANGE_2";
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://coinswitch.co"), Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _apiKey;
    private readonly Ed25519PrivateKeyParameters _secret;
    private long _clockOffsetMs;
    private bool _synced;

    public int ApiKeyLength => _apiKey.Length;
    public int SecretLength { get; }
    public bool KeyMatchesSecret { get; }

    public CoinSwitchClient(string apiKey, string secretHex)
    {
        _apiKey = apiKey.Trim().Trim('"', '\'');
        secretHex = secretHex.Trim().Trim('"', '\'');
        SecretLength = secretHex.Length;
        var bytes = Convert.FromHexString(secretHex);
        if (bytes.Length == 64) bytes = bytes[..32];
        if (bytes.Length != 32) throw new ArgumentException($"Secret key should be 64 hex chars (32 bytes); got {secretHex.Length} chars.");
        _secret = new Ed25519PrivateKeyParameters(bytes, 0);
        KeyMatchesSecret = Convert.ToHexString(_secret.GeneratePublicKey().GetEncoded()).Equals(_apiKey, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasEnvKeys =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS_API_KEY")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS_SECRET_KEY"));

    private static CoinSwitchClient? _shared; private static readonly object _sharedLock = new();
    /// <summary>One long-lived client (clock already synced) shared by the chart, live feed and candle cache.</summary>
    public static CoinSwitchClient Shared { get { lock (_sharedLock) return _shared ??= FromEnv(); } }

    public static void ResetShared() { lock (_sharedLock) { _shared?.Dispose(); _shared = null; } }

    public static CoinSwitchClient FromEnv() => new(
        Environment.GetEnvironmentVariable("CS_API_KEY") ?? throw new InvalidOperationException("Set CS_API_KEY"),
        Environment.GetEnvironmentVariable("CS_SECRET_KEY") ?? throw new InvalidOperationException("Set CS_SECRET_KEY"));

    public async Task<long> SyncClockAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/trade/api/v2/time", ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        long server = FindLong(doc.RootElement, "serverTime") ?? throw new InvalidOperationException("Unexpected /time response: " + body);
        if (server < 1_000_000_000_000) server *= 1000;
        _clockOffsetMs = server - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _synced = true;
        return _clockOffsetMs;
    }

    private static long? FindLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == name) return p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : long.Parse(p.Value.GetString()!);
            var r = FindLong(p.Value, name);
            if (r != null) return r;
        }
        return null;
    }

    private string Sign(string message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _secret);
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
    }

    public Task<JsonDocument> GetAsync(string path, IReadOnlyList<KeyValuePair<string, string>>? query = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, query, null, ct);

    /// <summary>Signed request. Retries on HTTP 429 (rate limit) with back-off. Sign the UNENCODED query, send it URL-encoded.</summary>
    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, IReadOnlyList<KeyValuePair<string, string>>? query,
        object? body, CancellationToken ct = default)
    {
        string raw = path, enc = path;
        if (query is { Count: > 0 })
        {
            raw += "?" + string.Join("&", query.Select(kv => $"{kv.Key}={kv.Value}"));
            enc += "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }
        string json = body == null ? "" : JsonSerializer.Serialize(body);
        for (int attempt = 0; ; attempt++)
        {
            if (!_synced) await SyncClockAsync(ct);
            string epoch = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockOffsetMs).ToString(CultureInfo.InvariantCulture);
            using var req = new HttpRequestMessage(method, enc);
            req.Content = body == null ? new ByteArrayContent(Array.Empty<byte>()) : new StringContent(json, Encoding.UTF8);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-AUTH-APIKEY", _apiKey);
            req.Headers.Add("X-AUTH-SIGNATURE", Sign(method.Method + raw + epoch));
            req.Headers.Add("X-AUTH-EPOCH", epoch);
            using var resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 429 && attempt < 3) { await Task.Delay(8000 * (attempt + 1), ct); continue; }
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text}");
            return JsonDocument.Parse(text);
        }
    }

    /// <summary>Most responses wrap the payload in "data"; return it if present.</summary>
    public static JsonElement Data(JsonDocument d) =>
        d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("data", out var x) ? x.Clone() : d.RootElement.Clone();

    public Task<JsonDocument> ValidateKeysAsync(CancellationToken ct = default) => GetAsync("/trade/api/v2/validate/keys", null, ct);

    // ---------------- candles (spot + futures) ----------------

    /// <summary>Historical candles, fetched in chunks of 500. interval in MINUTES. Futures klines are limited to 30 req / 60s.</summary>
    public async Task<List<Candle>> GetCandlesAsync(Market market, string exchange, string symbol, int intervalMin,
        DateTime fromUtc, DateTime toUtc, Action<string>? progress = null, CancellationToken ct = default)
    {
        var map = new SortedDictionary<long, Candle>();
        long step = intervalMin * 60_000L, chunk = step * 500;
        long start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        bool fut = market == Market.Futures;
        string path = fut ? "/trade/api/v2/futures/klines" : "/trade/api/v2/candles";
        int delay = fut ? 2200 : 400;
        int total = (int)Math.Ceiling((end - start) / (double)chunk), done = 0;
        for (long s = start; s < end; s += chunk)
        {
            long e = Math.Min(s + chunk, end);
            var q = new List<KeyValuePair<string, string>>
            {
                new("exchange", fut ? FuturesExchange : exchange), new("symbol", symbol), new("interval", intervalMin.ToString()),
                new("start_time", s.ToString()), new("end_time", e.ToString())
            };
            if (fut) q.Add(new("limit", "500"));
            using var doc = await GetAsync(path, q, ct);
            var data = Data(doc);
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var k in data.EnumerateArray())
                {
                    long t = (long)Num(k, "start_time");
                    map[t] = new Candle(DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime, Num(k, "o"), Num(k, "h"), Num(k, "l"), Num(k, "c"), Num(k, "volume"));
                }
            progress?.Invoke($"chunk {++done}/{total}, {map.Count} candles");
            if (s + chunk < end) await Task.Delay(delay, ct);
        }
        return map.Values.ToList();
    }

    public static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
            JsonValueKind.Number => v.GetDouble(),
            _ => 0
        };
    }
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    // ---------------- orders ----------------

    // ---------------- live prices ----------------

    public async Task<JsonElement> SpotTickerAsync(string exchange, string symbol, CancellationToken ct = default)
    {
        using var d = await GetAsync("/trade/api/v2/24hr/ticker",
            new[] { new KeyValuePair<string, string>("exchange", exchange), new KeyValuePair<string, string>("symbol", symbol) }, ct);
        var data = Data(d);
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty(symbol, out var t)) return t;
            foreach (var p in data.EnumerateObject()) return p.Value;
        }
        return data;
    }

    public async Task<JsonElement> FuturesTickerAsync(string symbol, CancellationToken ct = default)
    {
        foreach (var sym in new[] { symbol.ToUpperInvariant(), symbol.ToLowerInvariant() })
        {
            try
            {
                using var d = await GetAsync("/trade/api/v2/futures/ticker",
                    new[] { new KeyValuePair<string, string>("exchange", FuturesExchange), new KeyValuePair<string, string>("symbol", sym) }, ct);
                var data = Data(d);
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(FuturesExchange, out var t)) return t;
                return data;
            }
            catch (HttpRequestException) when (sym != symbol.ToLowerInvariant()) { /* retry with lower-case symbol */ }
        }
        throw new InvalidOperationException("Futures ticker unavailable.");
    }

    public async Task<JsonElement> SpotPlaceLimitAsync(string exchange, string symbol, string side, double price, double qty, string clientId, CancellationToken ct = default)
    {
        using var d = await SendAsync(HttpMethod.Post, "/trade/api/v2/order", null,
            new Dictionary<string, object> { ["side"] = side, ["symbol"] = symbol, ["type"] = "limit", ["price"] = price, ["quantity"] = qty, ["exchange"] = exchange, ["client_order_id"] = clientId }, ct);
        return Data(d);
    }
    public async Task<JsonElement> SpotGetOrderAsync(string orderId, CancellationToken ct = default)
    {
        using var d = await GetAsync("/trade/api/v2/order", new[] { new KeyValuePair<string, string>("order_id", orderId) }, ct);
        return Data(d);
    }
    public async Task<JsonElement> SpotCancelAsync(string orderId, CancellationToken ct = default)
    {
        using var d = await SendAsync(HttpMethod.Delete, "/trade/api/v2/order", null, new Dictionary<string, object> { ["order_id"] = orderId }, ct);
        return Data(d);
    }

    public async Task<JsonElement> FuturesPlaceAsync(string symbol, string side, string type, double qty, double? price, bool reduceOnly, string clientId, CancellationToken ct = default)
    {
        var b = new Dictionary<string, object>
        { ["exchange"] = FuturesExchange, ["symbol"] = symbol, ["side"] = side, ["order_type"] = type, ["quantity"] = qty, ["reduce_only"] = reduceOnly, ["client_order_id"] = clientId };
        if (price != null) b["price"] = price.Value;
        using var d = await SendAsync(HttpMethod.Post, "/trade/api/v2/futures/order", null, b, ct);
        return Data(d);
    }
    public async Task<JsonElement> FuturesGetOrderAsync(string orderId, CancellationToken ct = default)
    {
        using var d = await GetAsync("/trade/api/v2/futures/order",
            new[] { new KeyValuePair<string, string>("exchange", FuturesExchange), new KeyValuePair<string, string>("order_id", orderId) }, ct);
        return Data(d);
    }
    public async Task FuturesSetLeverageAsync(string symbol, int leverage, CancellationToken ct = default)
    {
        using var d = await SendAsync(HttpMethod.Post, "/trade/api/v2/futures/leverage", null,
            new Dictionary<string, object> { ["exchange"] = FuturesExchange, ["symbol"] = symbol, ["leverage"] = leverage }, ct);
    }

    public void Dispose() => _http.Dispose();
}
