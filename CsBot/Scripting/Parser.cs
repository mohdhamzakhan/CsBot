namespace CsBot.Scripting;

internal abstract class Stmt { public int Line; public abstract void Exec(Ctx c); }

internal sealed class AssignStmt : Stmt
{
    public Var V; public Node E; public bool Once;
    public AssignStmt(Var v, Node e, bool once) { V = v; E = e; Once = once; }
    public override void Exec(Ctx c) { if (Once) { if (c.Bar == 0) V.Value = E.Eval(c); } else V.Value = E.Eval(c); }
}

internal sealed class InputStmt : Stmt
{
    public Var V; public double Value;
    public InputStmt(Var v, double val) { V = v; Value = val; }
    public override void Exec(Ctx c) { V.Value = Value; }
}

internal sealed class PlotStmt : Stmt
{
    public Node E; public string Title, Color; public int Pane; public readonly List<double> Values = new();
    public PlotStmt(Node e, string t, string col, int pane) { E = e; Title = t; Color = col; Pane = pane; }
    public override void Exec(Ctx c) { Values.Add(E.Eval(c)); }
}

internal sealed class ActionStmt : Stmt
{
    public string Name; public Node[] Args;
    public ActionStmt(string n, Node[] a) { Name = n; Args = a; }
    public override void Exec(Ctx c)
    {
        double v = Args.Length > 0 ? Args[0].Eval(c) : 1;
        switch (Name)
        {
            case "long": Enter(c, 1, v); break;
            case "short": if (c.AllowShort) Enter(c, -1, v); else Exit(c); break;   // on spot, short() just means "be flat"
            case "flat": case "exit": Exit(c); break;
            case "stop_loss": c.Stop = double.IsNaN(v) ? 0 : Math.Max(0, v); break;
            case "take_profit": c.Take = double.IsNaN(v) ? 0 : Math.Max(0, v); break;
            case "trail_stop": c.Trail = double.IsNaN(v) ? 0 : Math.Max(0, v); break;
            case "stop_at": c.StopPx = double.IsNaN(v) ? 0 : Math.Max(0, v); break;      // absolute price levels; call AFTER long()/short()
            case "target_at": c.TargetPx = double.IsNaN(v) ? 0 : Math.Max(0, v); break;
        }
    }
    private static void Enter(Ctx c, int dir, double size)
    {
        if (double.IsNaN(size)) return;
        size = Math.Clamp(size, 0, c.MaxW);
        if (size <= 0) { Exit(c); return; }
        if (c.Position != dir) { c.Entry = c.C[c.Bar]; c.Extreme = c.C[c.Bar]; c.StopPx = 0; c.TargetPx = 0; }
        c.Position = dir; c.Target = dir * size;
    }
    private static void Exit(Ctx c) { c.Position = 0; c.Target = 0; c.StopPx = 0; c.TargetPx = 0; }
}

internal sealed class IfStmt : Stmt
{
    public List<(Node Cond, List<Stmt> Body)> Branches = new(); public List<Stmt>? Else;
    public override void Exec(Ctx c)
    {
        foreach (var (cond, body) in Branches)
            if (Ctx.Truthy(cond.Eval(c))) { foreach (var s in body) s.Exec(c); return; }
        if (Else != null) foreach (var s in Else) s.Exec(c);
    }
}

internal sealed class Compiled
{
    public List<Stmt> Stmts = new(); public List<ScriptInput> Inputs = new(); public List<Var> Vars = new(); public List<PlotStmt> Plots = new(); public int Warmup;
}

internal sealed class Parser
{
    private static readonly HashSet<string> Actions = new() { "long", "short", "flat", "exit", "stop_loss", "take_profit", "trail_stop", "stop_at", "target_at", "plot", "plot_sub" };
    private static readonly HashSet<string> Keywords = new() { "if", "then", "elif", "else", "end", "var", "and", "or", "not", "true", "false", "na", "pi", "input" };

    private readonly List<Tok> _t; private int _p; private readonly Dictionary<string, double>? _prm;
    private readonly Dictionary<string, Var> _vars = new(); private readonly Compiled _out = new(); private int _depth; private int _maxLit = 0;

    private Parser(List<Tok> t, Dictionary<string, double>? prm) { _t = t; _prm = prm; }

    public static Compiled Compile(string code, Dictionary<string, double>? prm)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ScriptException(1, "The script is empty.");
        if (code.Length > 40_000) throw new ScriptException(1, "Script is too long (max 40,000 characters).");
        var p = new Parser(Lexer.Lex(code), prm);
        p.Program();
        return p._out;
    }

    // ---- token helpers ----
    private Tok Peek(int k = 0) => _t[Math.Min(_p + k, _t.Count - 1)];
    private Tok Next() => _t[Math.Min(_p++, _t.Count - 1)];
    private bool IsOp(string s, int k = 0) { var t = Peek(k); return t.T == TT.Op && t.S == s; }
    private bool IsId(string s, int k = 0) { var t = Peek(k); return t.T == TT.Id && t.S == s; }
    private bool AcceptOp(string s) { if (IsOp(s)) { _p++; return true; } return false; }
    private bool AcceptId(string s) { if (IsId(s)) { _p++; return true; } return false; }
    private ScriptException Err(Tok t, string m) => new(t.Line, m);
    private void ExpectOp(string s) { if (!AcceptOp(s)) throw Err(Peek(), $"Expected '{s}' but found '{Describe(Peek())}'"); }
    private static string Describe(Tok t) => t.T switch { TT.NL => "end of line", TT.End => "end of script", _ => t.S };
    private void SkipNL() { while (Peek().T == TT.NL || IsOp(";")) _p++; }

    // ---- statements ----
    private void Program()
    {
        SkipNL();
        while (Peek().T != TT.End)
        {
            _out.Stmts.Add(Stmt(true));
            if (Peek().T != TT.NL && Peek().T != TT.End && !IsOp(";")) throw Err(Peek(), $"Unexpected '{Describe(Peek())}' - one statement per line");
            SkipNL();
        }
        _out.Vars.AddRange(_vars.Values);
        if (_out.Stmts.Count > 2000) throw new ScriptException(1, "Too many statements (max 2000).");
        foreach (var s in _out.Stmts) Validate(s, false);
        int w = Math.Max(10, _maxLit);
        foreach (var i in _out.Inputs) if (i.Default >= 2 && i.Default <= 1000) w = Math.Max(w, (int)i.Default);
        _out.Warmup = Math.Min(1500, w + 5);
    }

    private Stmt Stmt(bool top)
    {
        var t = Peek();
        if (t.T != TT.Id) throw Err(t, $"Unexpected '{Describe(t)}'");
        if (t.S == "if") return ParseIf();
        if (t.S == "var")
        {
            Next(); var nameTok = Next(); if (nameTok.T != TT.Id) throw Err(nameTok, "Expected a variable name after 'var'");
            CheckName(nameTok); ExpectOp("=");
            var e = Expr(); var v = Declare(nameTok.S);
            return new AssignStmt(v, e, true) { Line = t.Line };
        }
        if (Keywords.Contains(t.S) && t.S != "input") throw Err(t, $"Unexpected keyword '{t.S}'");
        if (IsOp("=", 1))
        {
            Next(); Next(); CheckName(t);
            if (IsId("input")) { if (!top || _depth > 0) throw Err(Peek(), "input() can only be used at the top level: name = input(default, \"label\", min, max, step)"); return ParseInput(t); }
            var e = Expr(); var v = Declare(t.S);
            return new AssignStmt(v, e, false) { Line = t.Line };
        }
        if (IsOp(":=", 1))
        {
            Next(); Next();
            if (!_vars.TryGetValue(t.S, out var v)) throw Err(t, $"'{t.S}' is not defined yet - declare it first with: var {t.S} = 0");
            return new AssignStmt(v, Expr(), false) { Line = t.Line };
        }
        if (IsOp("(", 1))
        {
            if (!Actions.Contains(t.S)) throw Err(t, $"'{t.S}(...)' cannot be used as a statement. Actions: long, short, flat, exit, stop_loss, take_profit, trail_stop, stop_at, target_at, plot, plot_sub");
            return ParseAction();
        }
        throw Err(t, $"Cannot understand this line starting with '{t.S}'");
    }

    private void CheckName(Tok t)
    {
        string n = t.S;
        if (n.Contains('.')) throw Err(t, $"Invalid name '{n}'");
        if (Keywords.Contains(n) || BuiltinNode.Names.Contains(n) || Fns.Arity.ContainsKey(n) || Actions.Contains(n) || n == "band")
            throw Err(t, $"'{n}' is a reserved name - pick another variable name");
    }
    private Var Declare(string name)
    {
        if (!_vars.TryGetValue(name, out var v)) { v = new Var { Name = name }; _vars[name] = v; }
        return v;
    }

    private Stmt ParseInput(Tok nameTok)
    {
        var callTok = Next(); ExpectOp("(");
        var args = new List<Node>();
        if (!IsOp(")")) do { args.Add(ConstArg()); } while (AcceptOp(","));
        ExpectOp(")");
        if (args.Count == 0 || args[0] is not NumNode def) throw Err(callTok, "input() needs a default number, e.g. input(20, \"Fast EMA\", 2, 200, 1)");
        string label = args.Count > 1 && args[1] is StrNode sn ? sn.S : nameTok.S;
        int off = args.Count > 1 && args[1] is StrNode ? 2 : 1;
        double? mn = args.Count > off && args[off] is NumNode a ? a.V : null;
        double? mx = args.Count > off + 1 && args[off + 1] is NumNode b ? b.V : null;
        double step = args.Count > off + 2 && args[off + 2] is NumNode c ? c.V : (def.V == Math.Floor(def.V) ? 1 : 0.01);
        double val = _prm != null && _prm.TryGetValue(nameTok.S, out var pv) && !double.IsNaN(pv) ? pv : def.V;
        if (mn != null) val = Math.Max(val, mn.Value); if (mx != null) val = Math.Min(val, mx.Value);
        _out.Inputs.Add(new ScriptInput(nameTok.S, label, def.V, mn, mx, step));
        return new InputStmt(Declare(nameTok.S), val) { Line = nameTok.Line };
    }
    private Node ConstArg()
    {
        var t = Peek();
        if (t.T == TT.Str) { Next(); return new StrNode(t.S) { Line = t.Line }; }
        bool neg = AcceptOp("-"); var n = Next();
        if (n.T != TT.Num) throw Err(n, "input() arguments must be plain numbers or text");
        return new NumNode(neg ? -n.N : n.N) { Line = n.Line };
    }

    private Stmt ParseAction()
    {
        var name = Next(); ExpectOp("(");
        var args = new List<Node>();
        if (!IsOp(")")) do { args.Add(name.S is "plot" or "plot_sub" && Peek().T == TT.Str ? StrArg() : Expr()); } while (AcceptOp(","));
        ExpectOp(")");
        if (name.S is "plot" or "plot_sub")
        {
            if (_depth > 0) throw Err(name, "plot() must be at the top level, not inside an if-block");
            if (args.Count == 0) throw Err(name, "plot(value, \"title\", \"#color\")");
            string title = args.Count > 1 && args[1] is StrNode s1 ? s1.S : $"plot {_out.Plots.Count + 1}";
            string color = args.Count > 2 && args[2] is StrNode s2 ? s2.S : new[] { "#f5a623", "#4a90e2", "#bd10e0", "#50e3c2", "#e84393", "#b8e986" }[_out.Plots.Count % 6];
            if (_out.Plots.Count >= 12) throw Err(name, "At most 12 plots are allowed");
            var ps = new PlotStmt(args[0], title, color, name.S == "plot" ? 0 : 1) { Line = name.Line };
            _out.Plots.Add(ps); return ps;
        }
        int max = name.S is "flat" or "exit" ? 0 : 1;
        if (args.Count > max) throw Err(name, $"{name.S}() takes {(max == 0 ? "no arguments" : "at most one argument")}");
        if ((name.S is "stop_loss" or "take_profit" or "trail_stop" or "stop_at" or "target_at") && args.Count != 1) throw Err(name, $"{name.S}(percent) needs one number");
        return new ActionStmt(name.S, args.ToArray()) { Line = name.Line };
    }
    private Node StrArg() { var t = Next(); return new StrNode(t.S) { Line = t.Line }; }

    private Stmt ParseIf()
    {
        var ifTok = Next(); var st = new IfStmt { Line = ifTok.Line };
        var cond = Expr(); AcceptId("then");
        bool block = Peek().T == TT.NL || Peek().T == TT.End;
        _depth++;
        if (!block)
        {
            var body = new List<Stmt>();
            do { body.Add(Stmt(false)); } while (AcceptOp(";") && Peek().T != TT.NL && Peek().T != TT.End);
            st.Branches.Add((cond, body)); _depth--; return st;
        }
        st.Branches.Add((cond, Body()));
        while (true)
        {
            SkipNL();
            if (AcceptId("elif")) { var c2 = Expr(); AcceptId("then"); st.Branches.Add((c2, Body())); continue; }
            if (AcceptId("else")) { st.Else = Body(); continue; }
            if (AcceptId("end")) break;
            throw Err(Peek(), "Missing 'end' for this if-block");
        }
        _depth--; return st;
    }
    private List<Stmt> Body()
    {
        var list = new List<Stmt>(); SkipNL();
        while (!(IsId("elif") || IsId("else") || IsId("end")))
        {
            if (Peek().T == TT.End) throw Err(Peek(), "Missing 'end' for an if-block");
            list.Add(Stmt(false));
            if (Peek().T != TT.NL && !IsOp(";") && !(IsId("elif") || IsId("else") || IsId("end"))) throw Err(Peek(), $"Unexpected '{Describe(Peek())}'");
            SkipNL();
        }
        return list;
    }

    // ---- expressions ----
    private Node Expr() => Ternary();
    private Node Ternary()
    {
        var c = Or(); var t = Peek();
        if (AcceptOp("?")) { var a = Ternary(); ExpectOp(":"); var b = Ternary(); return new TernNode(c, a, b) { Line = t.Line }; }
        return c;
    }
    private Node Or() { var l = And(); while (IsId("or") || IsOp("||")) { var t = Next(); l = new BinNode("or", l, And()) { Line = t.Line }; } return l; }
    private Node And() { var l = Not(); while (IsId("and") || IsOp("&&")) { var t = Next(); l = new BinNode("and", l, Not()) { Line = t.Line }; } return l; }
    private Node Not() { if (IsId("not") || IsOp("!")) { var t = Next(); return new UnaryNode("!", Not()) { Line = t.Line }; } return Cmp(); }
    private Node Cmp()
    {
        var l = Add();
        while (Peek().T == TT.Op && Peek().S is "==" or "!=" or "<" or ">" or "<=" or ">=") { var t = Next(); l = new BinNode(t.S, l, Add()) { Line = t.Line }; }
        return l;
    }
    private Node Add() { var l = Mul(); while (IsOp("+") || IsOp("-")) { var t = Next(); l = new BinNode(t.S, l, Mul()) { Line = t.Line }; } return l; }
    private Node Mul() { var l = Unary(); while (IsOp("*") || IsOp("/") || IsOp("%")) { var t = Next(); l = new BinNode(t.S, l, Unary()) { Line = t.Line }; } return l; }
    private Node Unary()
    {
        if (IsOp("-")) { var t = Next(); return new UnaryNode("-", Unary()) { Line = t.Line }; }
        if (IsOp("+")) { Next(); return Unary(); }
        return Pow();
    }
    private Node Pow() { var b = Postfix(); if (IsOp("^")) { var t = Next(); return new BinNode("^", b, Unary()) { Line = t.Line }; } return b; }
    private Node Postfix()
    {
        var e = Primary();
        while (IsOp("[")) { var t = Next(); var idx = Expr(); ExpectOp("]"); e = new IndexNode(e, idx) { Line = t.Line }; }
        return e;
    }
    private Node Primary()
    {
        var t = Next();
        switch (t.T)
        {
            case TT.Num: if (t.N >= 2 && t.N <= 1000 && t.N == Math.Floor(t.N)) _maxLit = Math.Max(_maxLit, (int)t.N); return new NumNode(t.N) { Line = t.Line };
            case TT.Str: throw Err(t, "Text is only allowed inside input() and plot()");
            case TT.Op when t.S == "(": { var e = Expr(); ExpectOp(")"); return e; }
            case TT.Id:
                if (t.S == "true") return new NumNode(1) { Line = t.Line };
                if (t.S == "false") return new NumNode(0) { Line = t.Line };
                if (t.S == "na" && !IsOp("(")) return new NumNode(double.NaN) { Line = t.Line };   // na = empty value; na(x) = test for empty
                if (t.S == "pi") return new NumNode(Math.PI) { Line = t.Line };
                if (IsOp("("))
                {
                    if (!Fns.Arity.TryGetValue(t.S, out var ar))
                        throw Err(t, t.S == "input" ? "input() must be written as: name = input(default, \"label\")" : $"Unknown function '{t.S}'");
                    Next(); var args = new List<Node>();
                    if (!IsOp(")")) do { args.Add(Expr()); } while (AcceptOp(","));
                    ExpectOp(")");
                    if (args.Count < ar.Min || args.Count > ar.Max) throw Err(t, $"{t.S}() takes {(ar.Min == ar.Max ? ar.Min.ToString() : $"{ar.Min}-{ar.Max}")} argument(s) but got {args.Count}");
                    foreach (var a in args) if (a is NumNode nn && nn.V >= 2 && nn.V <= 1000 && nn.V == Math.Floor(nn.V)) _maxLit = Math.Max(_maxLit, (int)nn.V);
                    return new CallNode(t.S, args.ToArray()) { Line = t.Line };
                }
                if (BuiltinNode.Names.Contains(t.S)) return new BuiltinNode(t.S) { Line = t.Line };
                if (_vars.TryGetValue(t.S, out var v)) return new VarNode(v) { Line = t.Line };
                throw Err(t, $"Unknown name '{t.S}'. Define it first (e.g. {t.S} = ...) or check the spelling.");
        }
        throw Err(t, $"Unexpected '{Describe(t)}' in an expression");
    }

    // ---- validation: indicators must see every bar ----
    private static void Validate(Stmt s, bool inBlock)
    {
        switch (s)
        {
            case AssignStmt a: Walk(a.E, inBlock || a.Once, a.Line); break;
            case ActionStmt x: foreach (var e in x.Args) Walk(e, inBlock, x.Line); break;
            case PlotStmt p: Walk(p.E, false, p.Line); break;
            case IfStmt f:
                for (int i = 0; i < f.Branches.Count; i++)
                {
                    Walk(f.Branches[i].Cond, inBlock || i > 0, f.Line);
                    foreach (var b in f.Branches[i].Body) Validate(b, true);
                }
                if (f.Else != null) foreach (var b in f.Else) Validate(b, true);
                break;
        }
    }
    private static void Walk(Node n, bool inBlock, int line)
    {
        if (inBlock && n.Stateful)
            throw new ScriptException(n.Line > 0 ? n.Line : line,
                $"{n.Label} keeps history, so it must be calculated on every bar at the top level (not inside an if-block or a 'var' initialiser). Assign it to a variable first, then use the variable here.");
        foreach (var k in n.Kids) Walk(k, inBlock, line);
    }
}
