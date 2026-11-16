namespace CsBot.Research;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum McMode { BlockBootstrap, Shuffle }

/// <param name="BlockSize">Bars per resampled block. Keeps streaks/autocorrelation intact; 1 = plain bootstrap.</param>
/// <param name="RuinDrawdownPct">A path counts as "ruined" if drawdown from its peak ever reaches this.</param>
/// <param name="Horizon">Bars per simulated path; defaults to the length of the backtest.</param>
public sealed record MonteCarloRequest(
    int Runs = 5000, McMode Mode = McMode.BlockBootstrap, int BlockSize = 20,
    double RuinDrawdownPct = 50, int? Seed = null, int? Horizon = null, int SamplePaths = 40);

public sealed record Dist(double P5, double P25, double P50, double P75, double P95, double Mean);

public sealed record MonteCarloResult(
    int Runs, int Observations, int Horizon, double ProbRuinPct, double ProbLossPct,
    Dist FinalReturnPct, Dist MaxDrawdownPct, Dist CagrPct, double[][] SamplePaths, string[] Warnings);

/// <summary>
/// Resamples the strategy's per-bar returns (already net of fees, GST, slippage, TDS, funding).
/// The engine trades target weights rather than discrete round-trips, so bars are the natural unit.
/// </summary>
public static class MonteCarlo
{
    public static MonteCarloResult Run(BacktestResult bt, double barsPerYear, MonteCarloRequest req, CancellationToken ct = default)
    {
        var g = Metrics.GrowthPath(bt, 0);
        if (g.Length < 30) throw new InvalidOperationException("Backtest is too short for Monte Carlo (need at least 30 bars).");

        var rets = new double[g.Length];
        double prev = 1;
        for (int i = 0; i < g.Length; i++) { rets[i] = Math.Max(-1.0, g[i] / prev - 1); prev = g[i]; }

        int runs = Math.Clamp(req.Runs, 100, 100_000);
        int h = req.Horizon is > 0 ? req.Horizon.Value : rets.Length;
        int block = Math.Clamp(req.BlockSize, 1, rets.Length);
        double ruinLimit = Math.Clamp(req.RuinDrawdownPct, 1, 100) / 100.0;
        int baseSeed = req.Seed ?? Environment.TickCount;
        int keep = Math.Clamp(req.SamplePaths, 0, runs);
        int stride = Math.Max(1, h / 200);

        var final = new double[runs]; var dd = new double[runs]; var cagr = new double[runs];
        var ruin = new bool[runs]; var samples = new double[keep][];
        double years = h / barsPerYear;
        int points = 0;
        for (int i = 0; i < h; i++) if (i % stride == 0 || i == h - 1) points++;   // length every sample path should have
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, runs, opts, run =>
        {
            var rng = new Random(unchecked(baseSeed * 7919 + run));
            var path = SamplePath(rets, h, block, req.Mode, rng);
            double eq = 1, peak = 1, maxDd = 0; bool ruined = false;
            List<double>? pts = run < keep ? new List<double>() : null;
            for (int i = 0; i < h; i++)
            {
                eq *= 1 + path[i];
                if (eq < 0) eq = 0;
                peak = Math.Max(peak, eq);
                double d = (peak - eq) / peak;
                if (d > maxDd) maxDd = d;
                if (d >= ruinLimit || eq <= 0) ruined = true;
                if (pts != null && (i % stride == 0 || i == h - 1)) pts.Add(eq * 100);
                if (eq <= 0) break;
            }
            final[run] = (eq - 1) * 100;
            dd[run] = maxDd * 100;
            cagr[run] = eq > 0 && years > 0 ? (Math.Pow(eq, 1 / years) - 1) * 100 : -100;
            ruin[run] = ruined;
            if (pts != null)
            {
                while (pts.Count < points) pts.Add(0);   // a path that busted stops early: pad with 0 so every path has the same length
                samples[run] = pts.ToArray();
            }
        });

        var warnings = new List<string>();
        if (rets.Length < 100) warnings.Add("Fewer than 100 bars: confidence intervals will be wide and unreliable.");
        double flat = rets.Count(x => x == 0) / (double)rets.Length;
        if (flat > 0.5) warnings.Add($"The strategy is flat on {flat:P0} of bars; results mostly reflect the few bars where it was invested.");
        if (req.Mode == McMode.Shuffle) warnings.Add("Shuffle keeps the same final return in every run; use block bootstrap for return confidence intervals.");

        return new MonteCarloResult(runs, rets.Length, h, ruin.Count(x => x) * 100.0 / runs, final.Count(x => x < 0) * 100.0 / runs,
            Describe(final), Describe(dd), Describe(cagr), samples, warnings.ToArray());
    }

    private static double[] SamplePath(double[] src, int h, int block, McMode mode, Random rng)
    {
        var o = new double[h]; int n = src.Length;
        if (mode == McMode.Shuffle)
        {
            var tmp = (double[])src.Clone();
            for (int i = n - 1; i > 0; i--) { int j = rng.Next(i + 1); (tmp[i], tmp[j]) = (tmp[j], tmp[i]); }
            for (int i = 0; i < h; i++) o[i] = tmp[i % n];
            return o;
        }
        int pos = 0;
        while (pos < h)
        {
            int s = rng.Next(n);                                    // circular block bootstrap
            for (int k = 0; k < block && pos < h; k++) o[pos++] = src[(s + k) % n];
        }
        return o;
    }

    private static Dist Describe(double[] x)
    {
        var s = (double[])x.Clone(); Array.Sort(s);
        return new Dist(Pct(s, .05), Pct(s, .25), Pct(s, .50), Pct(s, .75), Pct(s, .95), x.Average());
    }

    private static double Pct(double[] sorted, double p)
    {
        double pos = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
