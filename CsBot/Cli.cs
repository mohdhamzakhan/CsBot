using System.Globalization;
namespace CsBot;

public static class Cli
{
    public static async Task RunAsync(string[] args)
    {

// Commands (run with: dotnet run -- cli ...):
//   fetch   download candles  : dotnet run -- fetch --exchange coinswitchx --symbol BTC/INR --interval 360 --days 730 --out btc_6h.csv
//   fees    show your fee tier: dotnet run -- fees --exchange coinswitchx
//   (none)  backtest          : dotnet run -- --csv btc_6h.csv --market spot [--compare] [--fee 0.1] [--fast 50 --slow 200 --resample 4 --fixed --band 0.3]
var opt = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--")) opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";

if (args.Length > 0 && args[0] == "fetch") { await Fetcher.RunAsync(opt); return; }
if (args.Length > 0 && args[0] == "fees") { await FeeTools.RunAsync(opt); return; }

double D(string k, double def) => opt.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;

bool futures = opt.GetValueOrDefault("market", "spot").Equals("futures", StringComparison.OrdinalIgnoreCase);
var candles = opt.TryGetValue("csv", out var path) ? LoadCsv(path) : Synthetic();
if (!opt.ContainsKey("csv")) Console.WriteLine("** SYNTHETIC data (has built-in trends): smoke test only, not evidence. **\n");
if (candles.Count < 300) { Console.WriteLine("Too few candles to test."); return; }

var gaps = candles.Take(101).Zip(candles.Skip(1), (a, b) => (b.Time - a.Time).TotalMinutes).OrderBy(x => x).ToList();
double baseMin = gaps[gaps.Count / 2];
double bpy = opt.ContainsKey("bars-per-year") ? D("bars-per-year", 1460) : 365.25 * 1440 / baseMin;
Console.WriteLine($"{candles.Count} candles, ~{baseMin:F0} min each, {bpy:F0} bars/year, {candles[0].Time:yyyy-MM-dd} to {candles[^1].Time:yyyy-MM-dd}");

var d0 = futures ? CostConfig.DefaultFutures : CostConfig.DefaultSpot;
var cost = new CostConfig
{
    Market = d0.Market, TakerFeePct = D("fee", d0.TakerFeePct), GstPct = D("gst", d0.GstPct),
    SlippagePct = D("slippage", d0.SlippagePct), TdsPct = D("tds", d0.TdsPct),
    FundingPer8hPct = D("funding", d0.FundingPer8hPct), MaxLeverage = D("leverage", d0.MaxLeverage)
};
Console.WriteLine($"Costs: fee {cost.TakerFeePct}% + {cost.GstPct}% GST, slippage {cost.SlippagePct}%, TDS {cost.TdsPct}%, funding {cost.FundingPer8hPct}%/8h\n");

var configs = new List<(string Label, int Factor, int Fast, int Slow, bool Fixed, double Band)>();
if (opt.ContainsKey("compare"))
{
    int day = Math.Max(1, (int)Math.Round(1440 / baseMin));
    configs.Add(("Base EMA20/100", 1, 20, 100, false, 0.15));
    configs.Add(("Base EMA50/200", 1, 50, 200, false, 0.15));
    if (day > 1)
    {
        configs.Add(("Daily EMA20/100", day, 20, 100, false, 0.15));
        configs.Add(("Daily EMA50/200", day, 50, 200, false, 0.15));
        configs.Add(("Daily 50/200 fixed", day, 50, 200, true, 0.30));
    }
}
else
{
    int f = (int)D("fast", 20), s = (int)D("slow", 100), rs = (int)D("resample", 1);
    configs.Add(($"EMA{f}/{s} x{rs}{(opt.ContainsKey("fixed") ? " fixed" : "")}", rs, f, s, opt.ContainsKey("fixed"), D("band", 0.15)));
}

foreach (var cfg in configs)
{
    var series = cfg.Factor > 1 ? Resample(candles, cfg.Factor) : candles;
    double b = bpy / cfg.Factor;
    var p = new TrendFollowingStrategy.Params(FastEma: cfg.Fast, SlowEma: cfg.Slow, StopAtr: D("stop", 3.0), BarsPerYear: b,
        MaxWeight: futures ? cost.MaxLeverage : 1, AllowShort: futures, FixedSize: cfg.Fixed);
    Func<IStrategy> factory = () => new TrendFollowingStrategy(p);

    var r = Backtester.Run(factory(), series, cost, b, minRebalance: cfg.Band);
    var bench = Backtester.Run(new BuyAndHoldStrategy(), series, CostConfig.DefaultSpot, b);
    var wf = Backtester.WalkForward(factory, series, cost, b, 5, factory().WarmupBars, cfg.Band);
    bool ok = LiveGate.Passes(r, bench, wf, out var why);
    string drag = double.IsNaN(r.FeeDragPct) ? "n/a (no gross profit)" : $"{r.FeeDragPct:F0}%";

    Console.WriteLine($"[{cfg.Label}]  {series.Count} bars");
    Console.WriteLine($"  Strategy   post-tax {r.PostTaxReturnPct,7:F1}%  Sharpe {r.Sharpe,5:F2}  MaxDD {r.MaxDrawdownPct,5:F1}%  trades {r.Trades}");
    Console.WriteLine($"  Buy&Hold   post-tax {bench.PostTaxReturnPct,7:F1}%  Sharpe {bench.Sharpe,5:F2}  MaxDD {bench.MaxDrawdownPct,5:F1}%");
    Console.WriteLine($"  Costs      fees {r.FeesPaid:F0}  slippage {r.SlippagePaid:F0}  TDS withheld (refundable, counted in equity) {r.TdsWithheld:F0}  funding {r.FundingPaid:F0}  turnover {r.Turnover:F1}x capital  fee drag {drag}");
    Console.WriteLine($"  Walk-fwd   post-tax % per segment: {string.Join(", ", wf.Select(x => x.ToString("F1")))}");
    Console.WriteLine($"  LIVE GATE  {(ok ? "PASS" : "BLOCKED")} ({why})\n");
}
if (configs.Count > 1)
    Console.WriteLine("Note: testing several configs raises the odds one passes by luck. Prefer a config that passes AND whose neighbours also look reasonable; confirm on a later period before trusting it.");

static List<Candle> Resample(IReadOnlyList<Candle> src, int factor)
{
    var res = new List<Candle>();
    for (int i = 0; i + factor <= src.Count; i += factor)
    {
        var g = src.Skip(i).Take(factor).ToList();
        res.Add(new Candle(g[0].Time, g[0].Open, g.Max(x => x.High), g.Min(x => x.Low), g[^1].Close, g.Sum(x => x.Volume)));
    }
    return res;
}

static List<Candle> LoadCsv(string file)
{
    var list = new List<Candle>();
    foreach (var line in File.ReadLines(file))
    {
        var f = line.Split(',');
        if (f.Length < 6 || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var o)) continue;
        DateTime t = long.TryParse(f[0], out var u)
            ? DateTimeOffset.FromUnixTimeMilliseconds(u > 1_000_000_000_000 ? u : u * 1000).UtcDateTime
            : DateTime.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        double P(int k) => double.Parse(f[k], CultureInfo.InvariantCulture);
        list.Add(new Candle(t, o, P(2), P(3), P(4), P(5)));
    }
    return list;
}

static List<Candle> Synthetic(int bars = 5840, int seed = 42)
{
    var rnd = new Random(seed);
    double Gauss() { double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); }
    var list = new List<Candle>(); double price = 30000, drift = 0; int left = 0;
    var t = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    for (int i = 0; i < bars; i++, t = t.AddHours(6))
    {
        if (left-- <= 0) { drift = (rnd.NextDouble() - 0.5) * 0.003; left = rnd.Next(150, 500); }
        double o = price, c = o * Math.Exp(drift + 0.012 * Gauss());
        double hi = Math.Max(o, c) * (1 + Math.Abs(Gauss()) * 0.002), lo = Math.Min(o, c) * (1 - Math.Abs(Gauss()) * 0.002);
        list.Add(new Candle(t, o, hi, lo, c, 100)); price = c;
    }
    return list;
}

    }
}

public static class Fetcher
{
    public static async Task RunAsync(Dictionary<string, string> o)
    {
        var market = o.GetValueOrDefault("market", "spot").Equals("futures", StringComparison.OrdinalIgnoreCase) ? Market.Futures : Market.Spot;
        string exchange = o.GetValueOrDefault("exchange", "coinswitchx");
        string symbol = o.GetValueOrDefault("symbol", market == Market.Futures ? "BTCUSDT" : "BTC/INR");
        int interval = int.Parse(o.GetValueOrDefault("interval", "360"));
        int days = int.Parse(o.GetValueOrDefault("days", "730"));
        string outFile = o.GetValueOrDefault("out", "candles.csv");

        using var cs = CoinSwitchClient.FromEnv();
        Console.WriteLine($"Keys: length {cs.ApiKeyLength}/{cs.SecretLength}, secret matches API key: {cs.KeyMatchesSecret}");
        if (!cs.KeyMatchesSecret) { Console.WriteLine("Secret does not match API key; re-copy both."); return; }
        Console.WriteLine($"Clock offset {await cs.SyncClockAsync()} ms (auto-corrected)");
        using (var v = await cs.ValidateKeysAsync()) Console.WriteLine(v.RootElement.ToString());

        var candles = await CandleStore.GetAsync(market, exchange, symbol, interval, days, true, Console.WriteLine);
        CandleStore.Save(outFile, candles);
        Console.WriteLine($"Saved {candles.Count} candles to {outFile}. Expected ~{days * 1440 / interval}; large gaps = illiquid periods or API limits.");
    }
}
