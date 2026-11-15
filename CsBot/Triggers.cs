using CsBot.Scripting;

namespace CsBot;

public sealed record TriggersReq(MarketRef Data, StrategyIn Strategy);

/// <summary>
/// Finds the prices at which a strategy would change its mind. It replays the same history the live trader uses, then asks the REAL strategy
/// "what would you do if the current candle closed at price P?" for many P and bisects to the exact price where the answer flips.
/// Works for every strategy (built-in, scripts, rules) without any strategy-specific code.
/// </summary>
public static class Triggers
{
    private static string Stance(double w) => w > 0.01 ? "long" : w < -0.01 ? "short" : "flat";

    public static object Compute(IReadOnlyList<Candle> closed, MarketRef data, StrategyIn st)
    {
        var m = Api.ParseMarket(data.Market);
        double bpy = 365.25 * 1440 / data.IntervalMin;
        var (factory, _) = StrategyRegistry.Create(st.Id, st.Params, st.Rules, m, bpy, m == Market.Futures ? 3 : 1, st.Code);
        int warm = factory().WarmupBars, need = Math.Min(1500, warm + 400);
        if (closed.Count < warm) throw new InvalidOperationException($"Only {closed.Count} candles are available; this strategy needs at least {warm}. Increase the history.");
        var hist = closed.Skip(Math.Max(0, closed.Count - need)).ToList();
        var last = hist[^1]; var step = TimeSpan.FromMinutes(data.IntervalMin);
        double vol = hist.TakeLast(20).Select(c => c.Volume).OrderBy(v => v).ElementAt(Math.Min(9, hist.TakeLast(20).Count() - 1));

        IStrategy Replay(Candle? hypo)
        {
            var s = factory();
            foreach (var c in hist) s.OnCandle(c);
            if (hypo is Candle h) s.OnCandle(h);
            return s;
        }
        // the strategy acts on the close of a candle; its answer after the CURRENT forming candle closes at P is what we probe
        string AfterClose(double p)
        {
            var s = Replay(null); var o = last.Close;
            return Stance(s.OnCandle(new Candle(last.Time + step, o, Math.Max(o, p), Math.Min(o, p), p, vol)).TargetWeight);
        }

        // current stance = what the strategy wants after the last CLOSED candle (the position it is holding or about to hold)
        var baseStrategy = factory(); double w0 = 0;
        foreach (var c in hist) w0 = baseStrategy.OnCandle(c).TargetWeight;
        var cur = Stance(w0);
        var boundaries = new List<object>();
        foreach (double span in new[] { 0.25, 0.6 })
        {
            const int n = 51;
            var ps = Enumerable.Range(0, n).Select(i => last.Close * Math.Exp(-span + 2 * span * i / (n - 1))).ToArray();
            var sts = new string[n];
            Parallel.For(0, n, i => sts[i] = AfterClose(ps[i]));
            for (int i = 0; i + 1 < n; i++)
            {
                if (sts[i] == sts[i + 1]) continue;
                double lo = ps[i], hi = ps[i + 1]; string sLo = sts[i], sHi = sts[i + 1];
                for (int k = 0; k < 14; k++) { double mid = (lo + hi) / 2; if (AfterClose(mid) == sLo) lo = mid; else hi = mid; }
                double b = (lo + hi) / 2;
                boundaries.Add(new { price = Math.Round(b, 8), pct = Math.Round((b / last.Close - 1) * 100, 3), below = sLo, above = sHi });
            }
            if (boundaries.Count > 0) break;           // widen the search only if nothing was found
        }

        object? levels = null;
        if (baseStrategy is ScriptStrategy ss)
        {
            var (pos, entry, stop, target) = ss.Levels;
            if (pos != 0 && (stop > 0 || target > 0)) levels = new { side = pos > 0 ? "long" : "short", entry, stop = stop > 0 ? stop : (double?)null, target = target > 0 ? target : (double?)null };
        }
        long lastStart = new DateTimeOffset(DateTime.SpecifyKind(last.Time, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return new { lastClose = last.Close, current = cur, boundaries, levels, nextDecisionAt = lastStart + 2L * data.IntervalMin * 60, barsUsed = hist.Count };
    }

}
