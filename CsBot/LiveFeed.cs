using System.Collections.Concurrent;
using System.Text.Json;

namespace CsBot;

public sealed record LiveBar(long T, double O, double H, double L, double C);
public sealed record LiveTick(double Price, double? Bid, double? Ask, double? ChangePct, double? High, double? Low, double? Volume,
    long At, double? FundingRate, double? MarkPrice, long? NextFunding, LiveBar? Bar);

/// <summary>
/// Latest price + 24h stats (+ the still-forming candle when the exchange returns it), cached for a couple of seconds
/// so several browser tabs cannot exhaust CoinSwitch's rate limits.
/// </summary>
public static class LiveFeed
{
    private sealed record Entry(DateTime At, LiveTick Tick);
    private sealed record BarEntry(DateTime At, LiveBar? Bar);
    private static readonly ConcurrentDictionary<string, Entry> _ticks = new();
    private static readonly ConcurrentDictionary<string, BarEntry> _bars = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public static async Task<LiveTick> GetAsync(Market m, string exchange, string symbol, int intervalMin, CancellationToken ct)
    {
        string key = $"{m}|{exchange}|{symbol}|{intervalMin}";
        if (_ticks.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMilliseconds(1500)) return hit.Tick;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_ticks.TryGetValue(key, out hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMilliseconds(1500)) return hit.Tick;
            if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Set CS_API_KEY and CS_SECRET_KEY to see live prices.");
            var cs = CoinSwitchClient.Shared;
            LiveTick tick = m == Market.Spot
                ? ParseSpot(await cs.SpotTickerAsync(exchange, symbol, ct))
                : ParseFutures(await cs.FuturesTickerAsync(symbol, ct));
            if (tick.Price <= 0) throw new InvalidOperationException("The exchange returned no price for " + symbol);
            if (tick.At <= 0) tick = tick with { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            tick = tick with { Bar = await FormingBarAsync(cs, m, exchange, symbol, intervalMin, key, ct) };
            _ticks[key] = new Entry(DateTime.UtcNow, tick);
            return tick;
        }
        finally { gate.Release(); }
    }

    private static double Num(JsonElement e, string n) => CoinSwitchClient.Num(e, n);
    private static double? Opt(JsonElement e, string n) => e.TryGetProperty(n, out _) ? Num(e, n) : null;

    public static LiveTick ParseSpot(JsonElement t) =>
        new(Num(t, "lastPrice"), Opt(t, "bidPrice"), Opt(t, "askPrice"), Opt(t, "percentageChange"), Opt(t, "highPrice"),
            Opt(t, "lowPrice"), Opt(t, "quoteVolume"), (long)Num(t, "at"), null, null, null, null);

    public static LiveTick ParseFutures(JsonElement t) =>
        new(Num(t, "last_price"), Opt(t, "best_bid_price"), Opt(t, "best_ask_price"), Opt(t, "price_24h_pcnt"), Opt(t, "high_price_24h"),
            Opt(t, "low_price_24h"), Opt(t, "quote_asset_volume_24h"), (long)Num(t, "timestamp"), Opt(t, "funding_rate"), Opt(t, "mark_price"),
            t.TryGetProperty("next_funding_timestamp", out _) ? (long)Num(t, "next_funding_timestamp") : null, null);

    /// <summary>Best effort: ask for the last couple of candles; if the newest has not closed yet it is the forming bar.</summary>
    private static async Task<LiveBar?> FormingBarAsync(CoinSwitchClient cs, Market m, string exchange, string symbol, int intervalMin, string key, CancellationToken ct)
    {
        if (_bars.TryGetValue(key, out var b) && DateTime.UtcNow - b.At < TimeSpan.FromSeconds(10)) return b.Bar;
        LiveBar? bar = null;
        try
        {
            var now = DateTime.UtcNow; var step = TimeSpan.FromMinutes(intervalMin);
            var c = await cs.GetCandlesAsync(m, exchange, symbol, intervalMin, now - step * 2, now, null, ct);
            var last = c.LastOrDefault();
            if (last.Time != default && last.Time + step > now)
                bar = new LiveBar(new DateTimeOffset(DateTime.SpecifyKind(last.Time, DateTimeKind.Utc)).ToUnixTimeSeconds(), last.Open, last.High, last.Low, last.Close);
        }
        catch { /* the ticker alone is enough to animate the chart */ }
        _bars[key] = new BarEntry(DateTime.UtcNow, bar);
        return bar;
    }
}
