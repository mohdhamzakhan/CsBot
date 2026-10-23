namespace CsBot.Scripting;

internal sealed class Var { public string Name = ""; public double Value = double.NaN; public readonly List<double> Hist = new(); }

internal sealed class Ctx
{
    public readonly List<double> O = new(), H = new(), L = new(), C = new(), V = new(), Tm = new(), Tr = new();
    public int Bar = -1;
    public double BarsPerYear, MaxW; public bool AllowShort;
    public double Position, Entry, Extreme, Target, Stop, Take, Trail, StopPx, TargetPx;
    public bool HasExit; public int SinceExit;
    public static bool Truthy(double x) => !double.IsNaN(x) && x != 0;
}

internal abstract class Node
{
    public int Line;
    public abstract double Eval(Ctx c);
    public virtual bool Stateful => false;
    public virtual IEnumerable<Node> Kids => Array.Empty<Node>();
    public virtual string Label => GetType().Name;
}

internal sealed class NumNode : Node { public double V; public NumNode(double v) { V = v; } public override double Eval(Ctx c) => V; }
internal sealed class StrNode : Node { public string S; public StrNode(string s) { S = s; } public override double Eval(Ctx c) => double.NaN; }
internal sealed class VarNode : Node { public Var V; public VarNode(Var v) { V = v; } public override double Eval(Ctx c) => V.Value; }

internal sealed class BuiltinNode : Node
{
    public static readonly HashSet<string> Names = new() { "open", "high", "low", "close", "volume", "hl2", "hlc3", "ohlc4", "tr", "bar_index", "time", "position", "entry_price", "bars_since_exit", "minute_of_day", "hour", "day_index", "tf_minutes" };
    public string Name; public BuiltinNode(string n) { Name = n; }
    public override double Eval(Ctx c) => Name switch
    {
        "position" => c.Position,
        "entry_price" => c.Position == 0 ? double.NaN : c.Entry,
        "bars_since_exit" => c.HasExit ? c.SinceExit : double.NaN,
        _ => Val(c, c.Bar)
    };
    public double Val(Ctx c, int b)
    {
        if (b < 0 || b > c.Bar) return double.NaN;
        return Name switch
        {
            "open" => c.O[b], "high" => c.H[b], "low" => c.L[b], "close" => c.C[b], "volume" => c.V[b],
            "hl2" => (c.H[b] + c.L[b]) / 2, "hlc3" => (c.H[b] + c.L[b] + c.C[b]) / 3, "ohlc4" => (c.O[b] + c.H[b] + c.L[b] + c.C[b]) / 4,
            "tr" => c.Tr[b], "bar_index" => b, "time" => c.Tm[b],
            "minute_of_day" => Math.Floor(c.Tm[b] / 60) % 1440, "hour" => Math.Floor(c.Tm[b] / 3600) % 24,
            "day_index" => Math.Floor(c.Tm[b] / 86400), "tf_minutes" => 525960 / c.BarsPerYear, _ => b == c.Bar ? Eval(c) : double.NaN
        };
    }
}

internal sealed class UnaryNode : Node
{
    public string Op; public Node E;
    public UnaryNode(string op, Node e) { Op = op; E = e; }
    public override IEnumerable<Node> Kids => new[] { E };
    public override double Eval(Ctx c) { double x = E.Eval(c); return Op == "-" ? -x : (double.IsNaN(x) ? double.NaN : (x == 0 ? 1 : 0)); }
}

internal sealed class BinNode : Node
{
    public string Op; public Node L, R;
    public BinNode(string op, Node l, Node r) { Op = op; L = l; R = r; }
    public override IEnumerable<Node> Kids => new[] { L, R };
    public override double Eval(Ctx c)
    {
        double a = L.Eval(c), b = R.Eval(c);   // both sides always evaluated (no short-circuit) so indicator state stays in sync
        double r = Op switch
        {
            "+" => a + b, "-" => a - b, "*" => a * b,
            "/" => b == 0 ? double.NaN : a / b,
            "%" => b == 0 ? double.NaN : a % b,
            "^" => Math.Pow(a, b),
            "<" => B(a < b, a, b), ">" => B(a > b, a, b), "<=" => B(a <= b, a, b), ">=" => B(a >= b, a, b),
            "==" => B(a == b, a, b), "!=" => B(a != b, a, b),
            "and" => Ctx.Truthy(a) && Ctx.Truthy(b) ? 1 : 0,
            "or" => Ctx.Truthy(a) || Ctx.Truthy(b) ? 1 : 0,
            _ => double.NaN
        };
        return double.IsInfinity(r) ? double.NaN : r;
    }
    private static double B(bool v, double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? 0 : (v ? 1 : 0);
}

internal sealed class TernNode : Node
{
    public Node Cond, A, B;
    public TernNode(Node c, Node a, Node b) { Cond = c; A = a; B = b; }
    public override IEnumerable<Node> Kids => new[] { Cond, A, B };
    public override double Eval(Ctx c) { double k = Cond.Eval(c), a = A.Eval(c), b = B.Eval(c); return Ctx.Truthy(k) ? a : b; }
}

internal sealed class IndexNode : Node
{
    public Node Inner, Idx; private readonly List<double> _hist = new();
    private readonly bool _generic;
    public IndexNode(Node inner, Node idx) { Inner = inner; Idx = idx; _generic = !(inner is VarNode || inner is BuiltinNode); }
    public override bool Stateful => _generic;
    public override string Label => "[ ] on an expression";
    public override IEnumerable<Node> Kids => new[] { Inner, Idx };
    public override double Eval(Ctx c)
    {
        double nn = Idx.Eval(c);
        if (double.IsNaN(nn) || nn < 0 || nn > 5000) return double.NaN;
        int n = (int)nn;
        if (Inner is VarNode vn)
        {
            if (n == 0) return vn.V.Value;
            int k = vn.V.Hist.Count - n; return k >= 0 ? vn.V.Hist[k] : double.NaN;
        }
        if (Inner is BuiltinNode bn) return n == 0 ? bn.Eval(c) : bn.Val(c, c.Bar - n);
        double cur = Inner.Eval(c); _hist.Add(cur);
        int k2 = _hist.Count - 1 - n; return k2 >= 0 ? _hist[k2] : double.NaN;
    }
}

internal sealed class EmaS
{
    private readonly int _n; private readonly double _a; private int _cnt; private double _sum; public double Val = double.NaN;
    public EmaS(int n, bool wilder) { _n = n; _a = wilder ? 1.0 / n : 2.0 / (n + 1); }
    public double Update(double x)
    {
        if (double.IsNaN(x)) return Val;
        if (_cnt < _n) { _cnt++; _sum += x; if (_cnt == _n) Val = _sum / _n; return _cnt == _n ? Val : double.NaN; }
        Val += _a * (x - Val); return Val;
    }
}

internal sealed class CallNode : Node
{
    public string Name; public Node[] Args;
    public readonly List<double> H0 = new();
    public double S0, S1, S2, S3; public bool Init; public readonly EmaS?[] E = new EmaS?[4];
    public CallNode(string name, Node[] args) { Name = name; Args = args; }
    public override bool Stateful => Fns.Stateful.Contains(Name);
    public override string Label => Name + "()";
    public override IEnumerable<Node> Kids => Args;
    public override double Eval(Ctx c)
    {
        var a = new double[Args.Length];
        for (int i = 0; i < a.Length; i++) a[i] = Args[i].Eval(c);
        if (Fns.NeedsHist.Contains(Name)) H0.Add(a[0]);
        return Fns.Run(this, c, a);
    }
}
