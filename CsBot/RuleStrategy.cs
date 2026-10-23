using System.Text.Json;

namespace CsBot;

// ---- JSON model sent by the UI's rule builder ----
// Operand kinds: close, open, high, low, volume, value, ema, sma, rsi, roc (% change), highest, lowest (previous N bars), atr
// Ops: gt, lt, crossAbove, crossBelow.  Entries are AND-ed; exits are OR-ed.
public sealed class Operand { public string Kind { get; set; } = "close"; public int Period { get; set; } = 14; public double Value { get; set; } }
public sealed class Cond { public Operand Left { get; set; } = new(); public string Op { get; set; } = "gt"; public Operand Right { get; set; } = new(); }
public sealed class RuleSet
{
    public List<Cond> EntryLong { get; set; } = new();
    public List<Cond> ExitLong { get; set; } = new();
    public List<Cond> EntryShort { get; set; } = new();
    public List<Cond> ExitShort { get; set; } = new();
    public double StopLossPct { get; set; }
    public double TakeProfitPct { get; set; }
    public double Size { get; set; } = 1;   // fraction of equity (futures: up to your leverage cap)

    public static RuleSet FromJson(JsonElement e) =>
        JsonSerializer.Deserialize<RuleSet>(e.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuleSet();

    public string? Validate(bool allowShort)
    {
        if (EntryLong.Count == 0 && (!allowShort || EntryShort.Count == 0)) return "Add at least one entry condition.";
        if (EntryLong.Count > 0 && ExitLong.Count == 0 && StopLossPct <= 0) return "Long side needs an exit condition or a stop-loss, otherwise it never exits.";
        if (allowShort && EntryShort.Count > 0 && ExitShort.Count == 0 && StopLossPct <= 0) return "Short side needs an exit condition or a stop-loss.";
        var kinds = new HashSet<string> { "close", "open", "high", "low", "volume", "value", "ema", "sma", "rsi", "roc", "highest", "lowest", "atr" };
        foreach (var c in EntryLong.Concat(ExitLong).Concat(EntryShort).Concat(ExitShort))
        {
            if (!kinds.Contains(c.Left.Kind) || !kinds.Contains(c.Right.Kind)) return "Unknown indicator in a condition.";
            if (c.Op is not ("gt" or "lt" or "crossAbove" or "crossBelow")) return "Unknown comparison operator.";
            foreach (var o in new[] { c.Left, c.Right })
                if (o.Kind is "ema" or "sma" or "rsi" or "roc" or "highest" or "lowest" or "atr" && (o.Period < 2 || o.Period > 500)) return "Indicator period must be between 2 and 500.";
        }
        return null;
    }
}

public sealed class RuleStrategy : IStrategy
{
    private sealed class Rsi { public int N, Count; public double AvgG, AvgL, Last, Value = 50; }

    private readonly RuleSet _r; private readonly double _maxW; private readonly bool _short;
    private readonly List<double> _o = new(), _h = new(), _l = new(), _c = new(), _v = new();
    private readonly Dictionary<int, double> _ema = new(); private readonly Dictionary<int, Rsi> _rsi = new(); private readonly Dictionary<int, double> _atr = new();
    private readonly Dictionary<Cond, (double L, double R)> _prev = new(); private readonly Dictionary<Cond, bool> _now = new();
    private readonly List<Cond> _all; private readonly int _maxPeriod;
    private int _dir; private double _entryPx;

    public RuleStrategy(RuleSet rules, double maxWeight, bool allowShort)
    {
        _r = rules; _maxW = maxWeight; _short = allowShort;
        _all = rules.EntryLong.Concat(rules.ExitLong).Concat(rules.EntryShort).Concat(rules.ExitShort).ToList();
        foreach (var o in _all.SelectMany(c => new[] { c.Left, c.Right }))
        {
            if (o.Kind == "ema") _ema[o.Period] = 0;
            if (o.Kind == "rsi") _rsi[o.Period] = new Rsi { N = o.Period };
            if (o.Kind == "atr") _atr[o.Period] = 0;
        }
        _maxPeriod = _all.SelectMany(c => new[] { c.Left, c.Right }).Select(o => o.Kind is "close" or "open" or "high" or "low" or "volume" or "value" ? 1 : o.Period).DefaultIfEmpty(1).Max();
    }
    public string Name => "Custom rules";
    public int WarmupBars => _maxPeriod + 3;

    public Decision OnCandle(Candle c)
    {
        _o.Add(c.Open); _h.Add(c.High); _l.Add(c.Low); _c.Add(c.Close); _v.Add(c.Volume);
        int n = _c.Count;
        foreach (var p in _ema.Keys.ToList()) _ema[p] = n == 1 ? c.Close : _ema[p] + 2.0 / (p + 1) * (c.Close - _ema[p]);
        foreach (var s in _rsi.Values)
        {
            if (n > 1)
            {
                double ch = c.Close - s.Last, g = Math.Max(ch, 0), ls = Math.Max(-ch, 0);
                s.Count++;
                if (s.Count <= s.N) { s.AvgG += (g - s.AvgG) / s.Count; s.AvgL += (ls - s.AvgL) / s.Count; }
                else { s.AvgG = (s.AvgG * (s.N - 1) + g) / s.N; s.AvgL = (s.AvgL * (s.N - 1) + ls) / s.N; }
                s.Value = s.AvgL < 1e-12 ? 100 : 100 - 100 / (1 + s.AvgG / s.AvgL);
            }
            s.Last = c.Close;
        }
        foreach (var p in _atr.Keys.ToList())
        {
            double tr = n == 1 ? c.High - c.Low : Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - _c[n - 2]), Math.Abs(c.Low - _c[n - 2])));
            _atr[p] = n == 1 ? tr : (_atr[p] * (p - 1) + tr) / p;
        }
        if (n < WarmupBars) { Eval(); return new Decision(0); }   // keep 'prev' fresh for crossovers
        Eval();

        if (_dir == 0)
        {
            if (_r.EntryLong.Count > 0 && And(_r.EntryLong)) { _dir = 1; _entryPx = c.Close; }
            else if (_short && _r.EntryShort.Count > 0 && And(_r.EntryShort)) { _dir = -1; _entryPx = c.Close; }
        }
        else
        {
            double pnlPct = _dir * (c.Close / _entryPx - 1) * 100;
            bool stop = _r.StopLossPct > 0 && pnlPct <= -_r.StopLossPct;
            bool take = _r.TakeProfitPct > 0 && pnlPct >= _r.TakeProfitPct;
            bool exit = _dir == 1 ? Or(_r.ExitLong) : Or(_r.ExitShort);
            if (stop || take || exit) _dir = 0;
        }
        return new Decision(_dir * Math.Min(_r.Size, _maxW));
    }

    private bool And(List<Cond> l) => l.Count > 0 && l.All(x => _now[x]);
    private bool Or(List<Cond> l) => l.Any(x => _now[x]);

    private void Eval()
    {
        foreach (var cd in _all)
        {
            double a = Val(cd.Left), b = Val(cd.Right); bool ok = false;
            if (!double.IsNaN(a) && !double.IsNaN(b))
            {
                ok = cd.Op switch
                {
                    "gt" => a > b,
                    "lt" => a < b,
                    "crossAbove" => _prev.TryGetValue(cd, out var p1) && p1.L <= p1.R && a > b,
                    "crossBelow" => _prev.TryGetValue(cd, out var p2) && p2.L >= p2.R && a < b,
                    _ => false
                };
                _prev[cd] = (a, b);
            }
            _now[cd] = ok;
        }
    }

    private double Val(Operand o)
    {
        int n = _c.Count, p = o.Period;
        switch (o.Kind)
        {
            case "close": return _c[n - 1];
            case "open": return _o[n - 1];
            case "high": return _h[n - 1];
            case "low": return _l[n - 1];
            case "volume": return _v[n - 1];
            case "value": return o.Value;
            case "ema": return n >= p ? _ema[p] : double.NaN;
            case "sma": { if (n < p) return double.NaN; double s = 0; for (int i = n - p; i < n; i++) s += _c[i]; return s / p; }
            case "rsi": return _rsi[p].Count >= p ? _rsi[p].Value : double.NaN;
            case "atr": return n >= p ? _atr[p] : double.NaN;
            case "roc": return n > p ? (_c[n - 1] / _c[n - 1 - p] - 1) * 100 : double.NaN;
            case "highest": { if (n <= p) return double.NaN; double m = double.MinValue; for (int i = n - 1 - p; i < n - 1; i++) m = Math.Max(m, _h[i]); return m; }
            case "lowest": { if (n <= p) return double.NaN; double m = double.MaxValue; for (int i = n - 1 - p; i < n - 1; i++) m = Math.Min(m, _l[i]); return m; }
        }
        return double.NaN;
    }
}
