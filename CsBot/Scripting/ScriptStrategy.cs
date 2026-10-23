namespace CsBot.Scripting;

public sealed record PlotSeries(string Name, string Color, int Pane, double[] Values);

/// <summary>A user-written strategy in CsScript (a small Pine-inspired language), interpreted safely - no code is ever compiled or executed.</summary>
public sealed class ScriptStrategy : IStrategy
{
    private readonly Compiled _p; private readonly Ctx _c = new();

    private ScriptStrategy(Compiled p, double bpy, double maxW, bool allowShort)
    { _p = p; _c.BarsPerYear = bpy; _c.MaxW = maxW; _c.AllowShort = allowShort; }

    public string Name => "Script";
    public int WarmupBars => _p.Warmup;

    public static ScriptStrategy Create(string code, Dictionary<string, double>? prm, double bpy, double maxW, bool allowShort)
        => new(Parser.Compile(code, prm), bpy, maxW, allowShort);

    /// <summary>Validates a script; throws ScriptException with a line number on error.</summary>
    public static (IReadOnlyList<ScriptInput> Inputs, int Warmup) Check(string code)
    {
        var c = Parser.Compile(code, null);
        return (c.Inputs, c.Warmup);
    }

    public IEnumerable<PlotSeries> Plots => _p.Plots.Select(p => new PlotSeries(p.Title, p.Color, p.Pane, p.Values.ToArray()));

    public Decision OnCandle(Candle k)
    {
        var c = _c; c.Bar++;
        c.O.Add(k.Open); c.H.Add(k.High); c.L.Add(k.Low); c.C.Add(k.Close); c.V.Add(k.Volume);
        c.Tm.Add(new DateTimeOffset(DateTime.SpecifyKind(k.Time, DateTimeKind.Utc)).ToUnixTimeSeconds());
        double tr = c.Bar == 0 ? k.High - k.Low
            : Math.Max(k.High - k.Low, Math.Max(Math.Abs(k.High - c.C[c.Bar - 1]), Math.Abs(k.Low - c.C[c.Bar - 1])));
        c.Tr.Add(tr);
        if (c.HasExit) c.SinceExit++;

        // Absolute stop/target levels set on an earlier bar are tested against THIS bar's range first.
        // Stop is assumed to hit before target if both are inside one candle (conservative). Gaps fill at the open.
        double? exitPx = null;
        if (c.Position != 0 && (c.StopPx > 0 || c.TargetPx > 0))
        {
            bool lg = c.Position > 0;
            bool stopHit = c.StopPx > 0 && (lg ? k.Low <= c.StopPx : k.High >= c.StopPx);
            bool tgtHit = c.TargetPx > 0 && (lg ? k.High >= c.TargetPx : k.Low <= c.TargetPx);
            if (stopHit) exitPx = lg ? Math.Min(k.Open, c.StopPx) : Math.Max(k.Open, c.StopPx);
            else if (tgtHit) exitPx = lg ? Math.Max(k.Open, c.TargetPx) : Math.Min(k.Open, c.TargetPx);
            if (exitPx != null) { c.Position = 0; c.Target = 0; c.StopPx = 0; c.TargetPx = 0; c.HasExit = true; c.SinceExit = 0; }
        }

        foreach (var s in _p.Stmts) s.Exec(c);

        // Protective exits (stop-loss / take-profit / trailing stop), checked on the bar's close.
        if (c.Position != 0)
        {
            c.Extreme = c.Position > 0 ? Math.Max(c.Extreme, k.Close) : Math.Min(c.Extreme, k.Close);
            double pnl = c.Position * (k.Close / c.Entry - 1) * 100;
            bool hit = (c.Stop > 0 && pnl <= -c.Stop) || (c.Take > 0 && pnl >= c.Take)
                || (c.Trail > 0 && (c.Position > 0 ? k.Close <= c.Extreme * (1 - c.Trail / 100) : k.Close >= c.Extreme * (1 + c.Trail / 100)));
            if (hit) { c.Position = 0; c.Target = 0; c.StopPx = 0; c.TargetPx = 0; c.HasExit = true; c.SinceExit = 0; }
        }
        foreach (var v in _p.Vars) v.Hist.Add(v.Value);
        return new Decision(c.Target, exitPx);
    }
}
