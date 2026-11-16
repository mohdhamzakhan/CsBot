namespace CsBot.Research;

/// <param name="InSampleBars">Bars used to optimise in each window.</param>
/// <param name="OutSampleBars">Unseen bars the chosen parameters are tested on.</param>
/// <param name="StepBars">How far the window moves each time (usually = OutSampleBars).</param>
/// <param name="Anchored">True = in-sample always starts at bar 0 and grows; false = rolling.</param>
/// <param name="Warmup">Extra bars before each segment, used only to warm up indicators.</param>
/// <param name="MinTrades">Parameter sets with fewer trades in the in-sample run are ineligible.</param>
/// <param name="SmoothRadius">Parameters are chosen by neighbourhood-averaged score (plateau), radius in grid steps. 0 = raw best.</param>
public sealed record WfaRequest(
    int InSampleBars, int OutSampleBars, int StepBars, bool Anchored = false, int Warmup = 200,
    int MinTrades = 5, int SmoothRadius = 1, string Objective = "return", int MaxCombos = 2000);

public sealed record WfaWindow(
    int Index, int IsStart, int IsEnd, int OosStart, int OosEnd,
    IReadOnlyDictionary<string, double> Params, double? IsScore, double? IsReturnPct,
    double OosReturnPct, double OosSharpe, double OosMaxDdPct, int OosTrades, bool BestWasFragile);

public sealed record WfaResult(
    IReadOnlyList<WfaWindow> Windows, double CombinedOosReturnPct, double CombinedOosSharpe, double CombinedOosMaxDdPct,
    double? WalkForwardEfficiency, double ProfitableWindowsPct, IReadOnlyDictionary<string, double> ParamInstability,
    double[] CombinedOosEquity, bool Passed, string[] Flags);

public static class WalkForwardOptimizer
{
    public static int WindowCount(int candles, WfaRequest q)
     => q.StepBars < 1 || candles < q.InSampleBars + q.OutSampleBars ? 0 : (candles - q.InSampleBars - q.OutSampleBars) / q.StepBars + 1;

    /// <summary>Total backtests this run will execute; show it to the user before starting.</summary>
    public static long EstimateRuns(int candles, IReadOnlyList<Axis> axes, WfaRequest q)
        => WindowCount(candles, q) * (GridRunner.Size(axes) + 1);

    public static WfaResult Run(Func<ParamSet, Built> factory, IReadOnlyList<Axis> axes,
        IReadOnlyList<Candle> candles, CostConfig cost, double barsPerYear, WfaRequest q,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        if (q.InSampleBars < 50 || q.OutSampleBars < 10 || q.StepBars < 1) throw new ArgumentException("Window sizes too small.");
        var objective = Objectives.ByName(q.Objective);
        // slow settings (e.g. a 300-bar EMA) need more lead-in than the default; over-estimating is harmless
        int warmup = Math.Max(q.Warmup, Math.Min(1500, (int)Math.Ceiling(axes.Max(a => a.Max)) + 50));
        int windows = WindowCount(candles.Count, q);
        if (windows < 2) throw new ArgumentException("Not enough data for at least 2 windows. Shorten the windows or fetch more history.");

        const double start = 100_000;
        var rows = new List<WfaWindow>();
        var combined = new List<double>();
        double level = start; int oosTrades = 0; bool bust = false;

        for (int w = 0; w < windows; w++)
        {
            ct.ThrowIfCancellationRequested();
            int isStart = q.Anchored ? 0 : w * q.StepBars;
            int isEnd = w * q.StepBars + q.InSampleBars;
            int oosStart = isEnd, oosEnd = oosStart + q.OutSampleBars;

            // 1. optimise on in-sample data
            var (isData, isSkip) = Cut(candles, isStart, isEnd, warmup);
            var grid = GridRunner.Run(factory, axes, isData, cost, barsPerYear, objective, isSkip, maxCombos: q.MaxCombos, ct: ct);
            var an = Sensitivity.Analyze(grid, q.MinTrades, q.SmoothRadius);

            // 2. no eligible parameters -> stay flat for this window
            if (an == null)
            {
                for (int i = 0; i < q.OutSampleBars; i++) combined.Add(level);
                rows.Add(new WfaWindow(w, isStart, isEnd, oosStart, oosEnd, new Dictionary<string, double>(), null, null, 0, 0, 0, 0, false));
                progress?.Invoke((w + 1) / (double)windows);
                continue;
            }

            var ps = grid.ParamsAt(grid.Unflatten(an.StableFlat));
            var isM = grid.Cells[an.StableFlat];

            // 3. test the chosen parameters on unseen data
            var (oosData, oosSkip) = Cut(candles, oosStart, oosEnd, warmup);
            var built = factory(ps);
            var r = Backtester.Run(built.Strategy, oosData, cost, barsPerYear, minRebalance: built.Band);
            var gp = Metrics.GrowthPath(r, oosSkip);
            var m = Metrics.FromGrowth(gp, r.Trades, r.Busted, barsPerYear);
            oosTrades += r.Trades;

            double w0 = level;
            foreach (var v in gp) combined.Add(w0 * v);
            if (gp.Length > 0) level = w0 * gp[^1];

            rows.Add(new WfaWindow(w, isStart, isEnd, oosStart, oosEnd, ps.Values, an.StableRawScore, isM.ReturnPct,
                m.ReturnPct, m.Sharpe, m.MaxDdPct, r.Trades, an.BestIsFragile));
            progress?.Invoke((w + 1) / (double)windows);

            if (level <= 0) { bust = true; break; }
        }

        var cg = combined.Select(x => x / start).ToArray();
        var cm = Metrics.FromGrowth(cg, oosTrades, bust, barsPerYear);
        var used = rows.Where(x => x.Params.Count > 0).ToList();

        // walk-forward efficiency: annualised OOS return / annualised IS return (simple annualisation, avoids compounding blow-ups)
        double isAnn = used.Count == 0 ? 0 : used.Average(x => x.IsReturnPct!.Value * barsPerYear / (x.IsEnd - x.IsStart));
        double oosAnn = rows.Count == 0 ? 0 : rows.Average(x => x.OosReturnPct * barsPerYear / (x.OosEnd - x.OosStart));
        double? wfe = isAnn > 0 ? oosAnn / isAnn : null;
        double profitable = rows.Count(x => x.OosReturnPct > 0) * 100.0 / rows.Count;

        var instability = new Dictionary<string, double>();
        foreach (var a in axes)
        {
            var vals = used.Select(x => x.Params[a.Name]).ToList();
            double range = a.Max - a.Min;
            if (vals.Count < 2 || range <= 0) { instability[a.Name] = 0; continue; }
            double mean = vals.Average();
            instability[a.Name] = Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / vals.Count) / range;
        }

        var flags = new List<string>();
        if (cm.ReturnPct <= 0) flags.Add("Combined out-of-sample return is not positive.");
        if (wfe is double wfeValue && wfeValue < 0.5)
        {
            flags.Add(
                $"Walk-forward efficiency {wfeValue:P0} is below 50%: likely overfit.");
        }
        if (profitable < 50) flags.Add($"Only {profitable:F0}% of out-of-sample windows were profitable.");
        foreach (var kv in instability.Where(kv => kv.Value > 0.25))
            flags.Add($"Parameter '{kv.Key}' jumps around between windows (normalised spread {kv.Value:F2}).");
        int flat = rows.Count - used.Count;
        if (flat > 0) flags.Add($"{flat} window(s) had no eligible parameter set (too few trades) and were left flat.");
        if (rows.Count(x => x.BestWasFragile) > rows.Count / 2) flags.Add("The raw in-sample optimum was a fragile spike in most windows.");

        return new WfaResult(rows, cm.ReturnPct, cm.Sharpe, cm.MaxDdPct, wfe, profitable, instability, cg.Select(x => x * 100).ToArray(),
            flags.Count == 0, flags.ToArray());
    }

    private static (List<Candle> data, int skip) Cut(IReadOnlyList<Candle> c, int start, int end, int warmup)
    {
        int from = Math.Max(0, start - warmup);
        var d = new List<Candle>(end - from);
        for (int i = from; i < end; i++) d.Add(c[i]);
        return (d, start - from);
    }
}
