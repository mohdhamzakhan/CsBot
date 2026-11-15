namespace CsBot.Research;

public sealed record SensitivityAnalysis(
    int BestFlat, int StableFlat, double BestScore, double StableSmoothedScore, double StableRawScore,
    double SpikeRatio, bool BestIsFragile, double PlateauShare, double GridMean, double GridStd);

/// <summary>JSON-friendly summary. Scores are row-major in Dims order (last axis fastest); Heatmap is axis0 x axis1 at the stable point.</summary>
public sealed record SensitivityReport(
    string[] AxisNames, double[][] AxisValues, int[] Dims, double[] Scores,
    IReadOnlyDictionary<string, double> BestParams, double BestScore,
    IReadOnlyDictionary<string, double> StableParams, double StableScore, double StableRawScore,
    double SpikeRatio, bool BestIsFragile, double PlateauShare, double[][]? Heatmap, bool[] Valid);

public static class Sensitivity
{
    /// <summary>
    /// Best = highest raw score. Stable = highest neighbourhood-averaged score (a plateau beats a spike).
    /// SpikeRatio = how far the best point sits above its own neighbours, in grid standard deviations (&gt;1 = fragile).
    /// PlateauShare = fraction of the grid within half a standard deviation of the stable score.
    /// Returns null when no combination satisfies the minimum-trades / not-busted filter.
    /// </summary>
    public static SensitivityAnalysis? Analyze(Grid g, int minTrades = 0, int radius = 1)
    {
        int n = g.Count;
        var valid = Enumerable.Range(0, n).Where(i => g.Valid[i]).ToList();
        if (valid.Count == 0) return null;

        var smooth = new double[n];
        foreach (int i in valid)
            smooth[i] = Neighborhood(g, g.Unflatten(i), radius, true).Where(j => g.Valid[j]).Average(j => g.Scores[j]);

        double mean = valid.Average(i => g.Scores[i]);
        double std = Math.Sqrt(valid.Sum(i => (g.Scores[i] - mean) * (g.Scores[i] - mean)) / valid.Count);

        int best = -1, stable = -1;
        foreach (int i in valid)
        {
            if (g.Cells[i].Busted || g.Cells[i].Trades < minTrades) continue;
            if (best < 0 || g.Scores[i] > g.Scores[best]) best = i;
            if (stable < 0 || smooth[i] > smooth[stable]) stable = i;
        }
        if (best < 0) return null;

        var nb = Neighborhood(g, g.Unflatten(best), radius, false).Where(j => g.Valid[j]).Select(j => g.Scores[j]).ToList();
        double spike = nb.Count > 0 && std > 1e-12 ? (g.Scores[best] - nb.Average()) / std : 0;
        double plateau = valid.Count(i => smooth[i] >= smooth[stable] - 0.5 * std) / (double)valid.Count;

        return new SensitivityAnalysis(best, stable, g.Scores[best], smooth[stable], g.Scores[stable],
            spike, spike > 1.0, plateau, mean, std);
    }

    public static SensitivityReport Report(Grid g, SensitivityAnalysis a)
    {
        var stableIdx = g.Unflatten(a.StableFlat);
        double[][]? heat = null;
        if (g.Dims.Length >= 2)
        {
            heat = new double[g.Dims[0]][];
            var idx = (int[])stableIdx.Clone();
            for (int x = 0; x < g.Dims[0]; x++)
            {
                heat[x] = new double[g.Dims[1]];
                idx[0] = x;
                for (int y = 0; y < g.Dims[1]; y++) { idx[1] = y; heat[x][y] = g.Scores[g.Flatten(idx)]; }
            }
        }
        var axisValues = g.Axes.Select(ax => Enumerable.Range(0, ax.Count).Select(ax.ValueAt).ToArray()).ToArray();
        return new SensitivityReport(
            g.Axes.Select(x => x.Name).ToArray(), axisValues, g.Dims, g.Scores,
            g.ParamsAt(g.Unflatten(a.BestFlat)).Values, a.BestScore,
            g.ParamsAt(stableIdx).Values, a.StableSmoothedScore, a.StableRawScore,
            a.SpikeRatio, a.BestIsFragile, a.PlateauShare, heat, g.Valid);
    }

    /// <summary>All grid cells within +/-radius steps on every axis (clipped at the edges).</summary>
    private static IEnumerable<int> Neighborhood(Grid g, int[] idx, int radius, bool includeSelf)
    {
        int d = idx.Length;
        var lo = new int[d]; var hi = new int[d];
        for (int j = 0; j < d; j++) { lo[j] = Math.Max(0, idx[j] - radius); hi[j] = Math.Min(g.Dims[j] - 1, idx[j] + radius); }
        var cur = (int[])lo.Clone();
        while (true)
        {
            bool isSelf = true;
            for (int j = 0; j < d; j++) if (cur[j] != idx[j]) { isSelf = false; break; }
            if (includeSelf || !isSelf) yield return g.Flatten(cur);

            int k = d - 1;
            while (k >= 0 && ++cur[k] > hi[k]) { cur[k] = lo[k]; k--; }
            if (k < 0) yield break;
        }
    }
}
