namespace CsBot.Research;

/// <summary>Results of every parameter combination, stored row-major (last axis varies fastest).</summary>
public sealed class Grid
{
    public IReadOnlyList<Axis> Axes { get; }
    public int[] Dims { get; }
    public int Count { get; }
    public SegMetrics[] Cells { get; }
    public double[] Scores { get; }
    /// <summary>False for combinations the strategy rejected (e.g. fast EMA >= slow EMA).</summary>
    public bool[] Valid { get; }

    public Grid(IReadOnlyList<Axis> axes)
    {
        Axes = axes;
        Dims = axes.Select(a => a.Count).ToArray();
        Count = Dims.Aggregate(1, (a, b) => a * b);
        Cells = new SegMetrics[Count];
        Scores = new double[Count];
        Valid = new bool[Count];
    }

    public int Flatten(int[] idx)
    {
        int f = 0;
        for (int k = 0; k < Dims.Length; k++) f = f * Dims[k] + idx[k];
        return f;
    }

    public int[] Unflatten(int flat)
    {
        var idx = new int[Dims.Length];
        for (int k = Dims.Length - 1; k >= 0; k--) { idx[k] = flat % Dims[k]; flat /= Dims[k]; }
        return idx;
    }

    public ParamSet ParamsAt(int[] idx)
    {
        var d = new Dictionary<string, double>();
        for (int k = 0; k < Axes.Count; k++) d[Axes[k].Name] = Axes[k].ValueAt(idx[k]);
        return new ParamSet(d);
    }
}

public static class GridRunner
{
    public static long Size(IReadOnlyList<Axis> axes)
    {
        long n = 1;
        foreach (var a in axes) { a.Validate(); n *= a.Count; if (n > int.MaxValue) return n; }
        return n;
    }

    /// <param name="factory">Must build a NEW strategy instance on every call (strategies are stateful). May throw ArgumentException for invalid combinations.</param>
    /// <param name="warmup">Leading bars in <paramref name="candles"/> used only to warm indicators; excluded from metrics.</param>
    public static Grid Run(Func<ParamSet, Built> factory, IReadOnlyList<Axis> axes,
        IReadOnlyList<Candle> candles, CostConfig cost, double barsPerYear, Func<SegMetrics, double> objective,
        int warmup = 0, int maxCombos = 5000, int? maxDop = null,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        if (axes.Count == 0) throw new ArgumentException("At least one parameter range is required.");
        if (axes.Select(a => a.Name).Distinct().Count() != axes.Count) throw new ArgumentException("Duplicate parameter names.");
        long total = Size(axes);
        if (total > maxCombos) throw new ArgumentException($"{total} combinations exceeds the limit of {maxCombos}. Use wider steps or fewer parameters.");

        var grid = new Grid(axes);
        int done = 0;
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxDop ?? Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, grid.Count, opts, i =>
        {
            var p = grid.ParamsAt(grid.Unflatten(i));
            try
            {
                var b = factory(p);
                var r = Backtester.Run(b.Strategy, candles, cost, barsPerYear, minRebalance: b.Band);
                var m = Metrics.FromResult(r, warmup, barsPerYear);
                grid.Cells[i] = m; grid.Scores[i] = objective(m); grid.Valid[i] = true;
            }
            catch (ArgumentException) { grid.Cells[i] = new SegMetrics(0, 0, 0, 0, true, 0); }   // invalid combination: skipped
            progress?.Invoke(Interlocked.Increment(ref done) / (double)grid.Count);
        });
        return grid;
    }
}
