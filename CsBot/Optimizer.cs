using System.Text.Json;

namespace CsBot;

public sealed record ParamRange(string Key, double From, double To, double Step);
public sealed record CurvesReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, double? StartCash, List<Dictionary<string, double>> Sets);
public sealed record OptimizeReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, double? StartCash, List<ParamRange> Ranges, double? TrainPct);
public sealed record OptWin(double Ret, double Sharpe, double Dd);
public sealed record OptFull(double PostTaxPct, double Sharpe, double Dd, int Trades, double? FeeDragPct);
public sealed record OptRobust(double Sharpe, double Ret, double Calmar);
public sealed record OptCombo(int[] Ix, Dictionary<string, double> Params, OptWin Train, OptWin Test, OptFull Full, OptRobust Robust);

/// <summary>
/// Parameter search. Runs the strategy once per combination of settings on the SAME costs and data as a normal backtest, then judges each
/// combination separately on an early "training" window and a later "test" window it was not ranked on (one continuous run, parameters fixed).
/// Showing both is the point: a setting that wins on training but fails on the test window is almost always luck.
/// </summary>
public static class Optimizer
{
    public const int MaxCombos = 400;

    public static List<double> Values(ParamRange r)
    {
        if (r.Step <= 0 || r.To < r.From) throw new ArgumentException($"Check the range for '{r.Key}': 'to' must be at least 'from' and the step above zero.");
        var v = new List<double>();
        for (double x = r.From; x <= r.To + r.Step * 1e-6 && v.Count < 200; x += r.Step) v.Add(Math.Round(x, 6));
        return v;
    }

    private static double F(double x) => double.IsFinite(x) ? Math.Round(x, 4) : 0;

    /// <summary>Return %, Sharpe and max drawdown % of equity bars [a..b] (inclusive); baseEq is the equity just before bar a.</summary>
    private static OptWin Window(double[] eq, int a, int b, double baseEq, double bpy)
    {
        double prev = baseEq, peak = baseEq, dd = 0, sum = 0, sumSq = 0; int cnt = 0;
        for (int i = a; i <= b; i++)
        {
            double e = eq[i];
            if (prev > 0) { double r = e / prev - 1; sum += r; sumSq += r * r; cnt++; }
            peak = Math.Max(peak, e); if (peak > 0) dd = Math.Max(dd, (peak - e) / peak);
            prev = e;
        }
        double mean = cnt > 0 ? sum / cnt : 0, sd = cnt > 1 ? Math.Sqrt(Math.Max(0, (sumSq - cnt * mean * mean) / (cnt - 1))) : 0;
        double ret = baseEq > 0 ? (eq[b] / baseEq - 1) * 100 : 0;
        return new OptWin(F(ret), F(sd > 0 ? mean / sd * Math.Sqrt(bpy) : 0), F(dd * 100));
    }

    private static double Obj(OptWin w, int which) => which == 0 ? w.Sharpe : which == 1 ? w.Ret : w.Ret / Math.Max(1, w.Dd);

    public static object Run(IReadOnlyList<Candle> candles, MarketRef data, StrategyIn st, CostConfig cost, double cash, List<ParamRange> ranges, double trainPct, CancellationToken ct)
    {
        if (ranges.Count == 0) throw new ArgumentException("Pick at least one setting to vary.");
        if (ranges.Count > 3) throw new ArgumentException("Vary at most 3 settings at a time.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = Api.ParseMarket(data.Market); double bpy = 365.25 * 1440 / data.IntervalMin;
        var dims = ranges.Select(r => (r.Key, Vals: Values(r))).ToList();
        long total = dims.Aggregate(1L, (a, d) => a * d.Vals.Count);
        if (total > MaxCombos) throw new ArgumentException($"{total} combinations is too many (limit {MaxCombos}). Narrow the ranges or use bigger steps.");

        var all = new List<int[]> { Array.Empty<int>() };
        foreach (var d in dims) all = all.SelectMany(p => Enumerable.Range(0, d.Vals.Count).Select(i => p.Append(i).ToArray())).ToList();

        int n = candles.Count, s = Math.Clamp((int)(n * trainPct), 200, n - 200);
        var benchCost = new CostConfig { Market = Market.Spot, TakerFeePct = cost.TakerFeePct, GstPct = cost.GstPct, SlippagePct = cost.SlippagePct, TdsPct = cost.TdsPct, MaxLeverage = 1 };
        var bench = Backtester.Run(new BuyAndHoldStrategy(), candles, benchCost, bpy, cash);
        var baseline = new { train = Window(bench.Equity, 0, s - 1, cash, bpy), test = Window(bench.Equity, s, n - 1, bench.Equity[s - 1], bpy) };

        var results = new OptCombo?[all.Count];
        Parallel.For(0, all.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
        {
            var ix = all[i]; var pd = new Dictionary<string, JsonElement>(st.Params ?? new()); var pv = new Dictionary<string, double>();
            for (int d = 0; d < dims.Count; d++) { double v = dims[d].Vals[ix[d]]; pv[dims[d].Key] = v; pd[dims[d].Key] = JsonSerializer.SerializeToElement(v); }
            try
            {
                var (factory, band) = StrategyRegistry.Create(st.Id, pd, st.Rules, m, bpy, cost.MaxLeverage, st.Code);
                var res = Backtester.Run(factory(), candles, cost, bpy, cash, band); var eq = res.Equity;
                results[i] = new OptCombo(ix, pv, Window(eq, 0, s - 1, cash, bpy), Window(eq, s, n - 1, eq[s - 1] > 0 ? eq[s - 1] : cash, bpy),
                    new OptFull(F(res.PostTaxReturnPct), F(res.Sharpe), F(res.MaxDrawdownPct), res.Trades, double.IsFinite(res.FeeDragPct) ? F(res.FeeDragPct) : null),
                    new OptRobust(0, 0, 0));
            }
            catch (ArgumentException) { /* invalid combination (for example fast EMA >= slow EMA): skipped */ }
        });

        var ok = results.Where(c => c != null).Select(c => c!).ToList();
        // Stability score: average of the TRAINING result over a setting and its direct neighbours (one step in any one direction).
        // A lone spike surrounded by poor settings scores low; a broad plateau scores high.
        var byIx = ok.ToDictionary(c => string.Join(",", c.Ix));
        OptRobust Robust(OptCombo c)
        {
            var group = new List<OptCombo> { c };
            for (int d = 0; d < dims.Count; d++)
                foreach (int dd in new[] { -1, 1 })
                {
                    var nx = (int[])c.Ix.Clone(); nx[d] += dd;
                    if (nx[d] >= 0 && nx[d] < dims[d].Vals.Count && byIx.TryGetValue(string.Join(",", nx), out var nb)) group.Add(nb);
                }
            return new OptRobust(F(group.Average(g => Obj(g.Train, 0))), F(group.Average(g => Obj(g.Train, 1))), F(group.Average(g => Obj(g.Train, 2))));
        }
        ok = ok.Select(c => c with { Robust = Robust(c) }).ToList();

        return new
        {
            dims = dims.Select(d => new { key = d.Key, values = d.Vals }), combos = ok, tested = ok.Count, skipped = all.Count - ok.Count,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), start = new DateTimeOffset(DateTime.SpecifyKind(candles[0].Time, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            trainEnd = new DateTimeOffset(DateTime.SpecifyKind(candles[s].Time, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            end = new DateTimeOffset(DateTime.SpecifyKind(candles[n - 1].Time, DateTimeKind.Utc)).ToUnixTimeSeconds(), baseline
        };
    }
}
