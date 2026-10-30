using System.Collections.Concurrent;
using System.Text.Json;

namespace CsBot;

/// <summary>
/// Your REAL CoinSwitch wallet (not the paper-trading account).
///   Spot:    INR / USDT balances and every holding with its value and profit (GET /user/portfolio)
///   Futures: USDT wallet totals plus the open position on the selected symbol (GET /futures/wallet_balance, /futures/positions)
/// Results are cached for a few seconds so refreshing cannot exhaust CoinSwitch's rate limits (futures: 20 requests / 60 s).
/// </summary>
public static class PortfolioFeed
{
    private sealed record Entry(DateTime At, object Data);
    private static readonly ConcurrentDictionary<string, Entry> _cache = new();
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static double Num(JsonElement e, string n) => CoinSwitchClient.Num(e, n);
    private static string Str(JsonElement e, string n) => CoinSwitchClient.Str(e, n);

    public static async Task<object> GetAsync(Market m, string? symbol, CancellationToken ct)
    {
        string key = m == Market.Spot ? "spot" : "fut|" + (symbol ?? "").ToUpperInvariant();
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(8)) return hit.Data;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(8)) return hit.Data;
            if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Connect your CoinSwitch API keys to see your wallet.");
            var cs = CoinSwitchClient.Shared;
            object result;
            if (m == Market.Spot) result = Spot(await cs.SpotPortfolioAsync(ct));
            else
            {
                var wallet = await cs.FuturesWalletAsync(ct);
                JsonElement? pos = null; string? posError = null;
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    try { pos = await cs.FuturesPositionsAsync(symbol, ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { posError = e.Message; }
                }
                result = Futures(wallet, pos, posError);
            }
            _cache[key] = new Entry(DateTime.UtcNow, result);
            return result;
        }
        finally { _gate.Release(); }
    }

    public static object Spot(JsonElement data)
    {
        var rows = new List<(double Value, object Row)>();
        double inrFree = 0, inrBlocked = 0, usdtFree = 0, usdtBlocked = 0, totalValue = 0, invested = 0, pnl = 0;
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var r in data.EnumerateArray())
            {
                string cur = Str(r, "currency");
                double free = Num(r, "main_balance"), blocked = Num(r, "blocked_balance_order"), total = free + blocked;
                if (total <= 0) continue;
                if (cur == "INR") { inrFree = free; inrBlocked = blocked; totalValue += total; continue; }      // INR cash counts at face value
                if (cur == "USDT") { usdtFree = free; usdtBlocked = blocked; }
                double value = Num(r, "current_value"), inv = Num(r, "invested_value");
                totalValue += value;
                if (inv > 0) { invested += inv; pnl += value - inv; }
                rows.Add((value, new
                {
                    currency = cur, name = Str(r, "name"), free, blocked, total, avgBuy = Num(r, "buy_average_price"), invested = inv, value,
                    pnl = inv > 0 ? (double?)(value - inv) : null, pnlPct = inv > 0 ? (double?)((value / inv - 1) * 100) : null,
                }));
            }
        return new
        {
            market = "spot",
            inr = new { free = inrFree, blocked = inrBlocked, total = inrFree + inrBlocked },
            usdt = new { free = usdtFree, blocked = usdtBlocked, total = usdtFree + usdtBlocked },
            holdings = rows.OrderByDescending(x => x.Value).Select(x => x.Row).ToList(),
            totalValueInr = totalValue, investedInr = invested, pnlInr = pnl, asOf = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    public static object Futures(JsonElement wallet, JsonElement? positions, string? positionsError)
    {
        JsonElement bal = default; bool found = false;
        if (wallet.ValueKind == JsonValueKind.Object && wallet.TryGetProperty("base_asset_balances", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var b in arr.EnumerateArray())
                if (Str(b, "base_asset") == "USDT" && b.TryGetProperty("balances", out bal)) { found = true; break; }
        var plist = new List<object>(); double upnl = 0;
        if (positions is { ValueKind: JsonValueKind.Array } pa)
            foreach (var p in pa.EnumerateArray())
            {
                double pnl = Num(p, "unrealised_pnl"); upnl += pnl;
                plist.Add(new
                {
                    symbol = Str(p, "symbol"), side = Str(p, "position_side").ToLowerInvariant(), size = Num(p, "position_size"), notional = Num(p, "position_value"),
                    margin = Num(p, "position_margin"), entry = Num(p, "avg_entry_price"), mark = Num(p, "mark_price"), last = Num(p, "last_price"), pnl,
                    leverage = Num(p, "leverage"), liquidation = Num(p, "liquidation_price"), marginType = Str(p, "margin_type")
                });
            }
        return new
        {
            market = "futures",
            usdt = found
                ? new { total = Num(bal, "total_balance"), available = Num(bal, "total_available_balance"), blocked = Num(bal, "total_blocked_balance"), positionMargin = Num(bal, "total_position_margin"), openOrderMargin = Num(bal, "total_open_order_margin") }
                : new { total = 0.0, available = 0.0, blocked = 0.0, positionMargin = 0.0, openOrderMargin = 0.0 },
            positions = plist, unrealisedPnl = upnl, positionsError, asOf = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }
}
