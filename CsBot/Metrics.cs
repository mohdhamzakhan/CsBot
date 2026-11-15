namespace CsBot.Research;

/// <summary>Performance of a backtest measured only over a segment (warm-up bars excluded). Pre-tax.</summary>
public sealed record SegMetrics(double ReturnPct, double Sharpe, double MaxDdPct, int Trades, bool Busted, int Bars);

public static class Metrics
{
    /// <summary>
    /// Equity after the warm-up, rebased so the first measured bar starts from 1.0.
    /// Post-bust zeros are trimmed; a busted run ends at exactly 0.
    /// </summary>
    public static double[] GrowthPath(BacktestResult r, int skipBars)
    {
        var eq = r.Equity;
        if (eq.Length == 0) return Array.Empty<double>();
        int last = eq.Length - 1;
        while (last > 0 && eq[last] == 0) last--;
        if (r.Busted && last < eq.Length - 1) last++;           // keep the bust bar itself
        int s = Math.Clamp(skipBars, 0, last);
        double baseEq = s > 0 ? eq[s - 1] : r.StartEquity;
        if (baseEq <= 0) return Array.Empty<double>();
        var g = new double[last - s + 1];
        for (int k = 0; k < g.Length; k++) g[k] = Math.Max(0, eq[s + k] / baseEq);
        if (r.Busted) g[^1] = 0;
        return g;
    }

    public static SegMetrics FromGrowth(double[] g, int trades, bool busted, double barsPerYear)
    {
        if (g.Length == 0) return new SegMetrics(-100, 0, 100, trades, true, 0);
        double prev = 1, peak = 1, maxDd = 0, sum = 0, sumSq = 0; int n = 0;
        foreach (var v in g)
        {
            peak = Math.Max(peak, v);
            maxDd = Math.Max(maxDd, (peak - v) / peak);
            if (prev > 0) { double x = v / prev - 1; sum += x; sumSq += x * x; n++; }
            prev = v;
        }
        double mean = n > 0 ? sum / n : 0;
        double sd = n > 1 ? Math.Sqrt(Math.Max(0, (sumSq - n * mean * mean) / (n - 1))) : 0;
        double sharpe = sd > 0 ? mean / sd * Math.Sqrt(barsPerYear) : 0;
        return new SegMetrics((g[^1] - 1) * 100, sharpe, maxDd * 100, trades, busted, g.Length);
    }

    public static SegMetrics FromResult(BacktestResult r, int skipBars, double barsPerYear)
        => FromGrowth(GrowthPath(r, skipBars), r.Trades, r.Busted, barsPerYear);
}

/// <summary>What the optimiser maximises. Busted runs get a large penalty so they never win or look "stable".</summary>
public static class Objectives
{
    public static double Return(SegMetrics m) => m.Busted ? -100 : m.ReturnPct;
    public static double Sharpe(SegMetrics m) => m.Busted ? -10 : m.Sharpe;
    public static double ReturnOverDrawdown(SegMetrics m) => m.Busted ? -100 : m.ReturnPct / Math.Max(m.MaxDdPct, 1.0);

    public static Func<SegMetrics, double> ByName(string? name) => (name ?? "return").ToLowerInvariant() switch
    {
        "return" => Return,
        "sharpe" => Sharpe,
        "return_dd" or "calmar" => ReturnOverDrawdown,
        _ => throw new ArgumentException($"Unknown objective '{name}'. Use return, sharpe or return_dd.")
    };
}
