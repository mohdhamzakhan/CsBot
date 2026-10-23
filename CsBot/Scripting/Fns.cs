namespace CsBot.Scripting;

internal static class Fns
{
    // name -> (min args, max args)
    public static readonly Dictionary<string, (int Min, int Max)> Arity = new()
    {
        ["abs"] = (1, 1), ["max"] = (2, 2), ["min"] = (2, 2), ["sqrt"] = (1, 1), ["log"] = (1, 1), ["exp"] = (1, 1), ["pow"] = (2, 2),
        ["round"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1), ["sign"] = (1, 1), ["nz"] = (1, 2), ["na"] = (1, 1),
        ["sma"] = (2, 2), ["ema"] = (2, 2), ["rma"] = (2, 2), ["wma"] = (2, 2), ["stdev"] = (2, 2), ["highest"] = (2, 2), ["lowest"] = (2, 2),
        ["sum"] = (2, 2), ["change"] = (1, 2), ["roc"] = (2, 2), ["rsi"] = (2, 2), ["atr"] = (1, 1), ["crossover"] = (2, 2), ["crossunder"] = (2, 2),
        ["barssince"] = (1, 1), ["macd"] = (3, 3), ["macd_signal"] = (4, 4), ["macd_hist"] = (4, 4), ["bb_upper"] = (3, 3), ["bb_lower"] = (3, 3),
        ["stoch_k"] = (1, 1), ["adx"] = (1, 1), ["plus_di"] = (1, 1), ["minus_di"] = (1, 1), ["cci"] = (2, 2),
        ["supertrend"] = (2, 2), ["supertrend_dir"] = (2, 2), ["vol"] = (1, 1),
        ["session_min"] = (1, 1), ["new_session"] = (1, 1), ["vwap"] = (0, 1),
    };
    public static readonly HashSet<string> Stateful = new()
    {
        "sma", "ema", "rma", "wma", "stdev", "highest", "lowest", "sum", "change", "roc", "rsi", "atr", "crossover", "crossunder", "barssince",
        "macd", "macd_signal", "macd_hist", "bb_upper", "bb_lower", "adx", "plus_di", "minus_di", "cci", "supertrend", "supertrend_dir",
        "new_session", "vwap",
    };
    public static readonly HashSet<string> NeedsHist = new() { "sma", "wma", "stdev", "highest", "lowest", "sum", "change", "roc", "bb_upper", "bb_lower", "cci" };

    private static int P(double x) => double.IsNaN(x) ? 0 : (int)Math.Clamp(Math.Round(x), 0, 5000);
    private static bool Win(List<double> h, int p)
    {
        if (p < 1 || h.Count < p) return false;
        for (int i = h.Count - p; i < h.Count; i++) if (double.IsNaN(h[i])) return false;
        return true;
    }
    private static double Mean(List<double> h, int p) { if (!Win(h, p)) return double.NaN; double s = 0; for (int i = h.Count - p; i < h.Count; i++) s += h[i]; return s / p; }
    private static double Std(List<double> h, int p)
    {
        double m = Mean(h, p); if (double.IsNaN(m)) return double.NaN;
        double s = 0; for (int i = h.Count - p; i < h.Count; i++) s += (h[i] - m) * (h[i] - m);
        return Math.Sqrt(s / p);
    }

    public static double Run(CallNode n, Ctx c, double[] a)
    {
        switch (n.Name)
        {
            case "abs": return Math.Abs(a[0]);
            case "max": return Math.Max(a[0], a[1]);
            case "min": return Math.Min(a[0], a[1]);
            case "sqrt": return a[0] < 0 ? double.NaN : Math.Sqrt(a[0]);
            case "log": return a[0] <= 0 ? double.NaN : Math.Log(a[0]);
            case "exp": return Math.Exp(a[0]);
            case "pow": return Math.Pow(a[0], a[1]);
            case "round": return Math.Round(a[0]);
            case "floor": return Math.Floor(a[0]);
            case "ceil": return Math.Ceiling(a[0]);
            case "sign": return double.IsNaN(a[0]) ? double.NaN : Math.Sign(a[0]);
            case "nz": return double.IsNaN(a[0]) ? (a.Length > 1 ? a[1] : 0) : a[0];
            case "na": return double.IsNaN(a[0]) ? 1 : 0;

            case "sma": return Mean(n.H0, P(a[1]));
            case "sum": { int p = P(a[1]); var m = Mean(n.H0, p); return double.IsNaN(m) ? m : m * p; }
            case "wma":
            {
                int p = P(a[1]); if (!Win(n.H0, p)) return double.NaN;
                double s = 0; for (int i = 0; i < p; i++) s += n.H0[n.H0.Count - p + i] * (i + 1);
                return s / (p * (p + 1) / 2.0);
            }
            case "stdev": return Std(n.H0, P(a[1]));
            case "highest": { int p = P(a[1]); if (!Win(n.H0, p)) return double.NaN; double m = double.MinValue; for (int i = n.H0.Count - p; i < n.H0.Count; i++) m = Math.Max(m, n.H0[i]); return m; }
            case "lowest": { int p = P(a[1]); if (!Win(n.H0, p)) return double.NaN; double m = double.MaxValue; for (int i = n.H0.Count - p; i < n.H0.Count; i++) m = Math.Min(m, n.H0[i]); return m; }
            case "change": { int p = a.Length > 1 ? P(a[1]) : 1; int k = n.H0.Count - 1 - p; return p < 1 || k < 0 ? double.NaN : n.H0[^1] - n.H0[k]; }
            case "roc": { int p = P(a[1]); int k = n.H0.Count - 1 - p; return p < 1 || k < 0 || n.H0[k] == 0 ? double.NaN : 100 * (n.H0[^1] / n.H0[k] - 1); }
            case "ema": { int p = P(a[1]); if (p < 1) return double.NaN; n.E[0] ??= new EmaS(p, false); return n.E[0]!.Update(a[0]); }
            case "rma": { int p = P(a[1]); if (p < 1) return double.NaN; n.E[0] ??= new EmaS(p, true); return n.E[0]!.Update(a[0]); }
            case "rsi":
            {
                int p = P(a[1]); if (p < 1 || double.IsNaN(a[0])) return double.NaN;
                n.E[0] ??= new EmaS(p, true); n.E[1] ??= new EmaS(p, true);
                if (!n.Init) { n.Init = true; n.S0 = a[0]; return double.NaN; }
                double ch = a[0] - n.S0; n.S0 = a[0];
                double g = n.E[0]!.Update(Math.Max(ch, 0)), l = n.E[1]!.Update(Math.Max(-ch, 0));
                if (double.IsNaN(g) || double.IsNaN(l)) return double.NaN;
                return l == 0 ? 100 : 100 - 100 / (1 + g / l);
            }
            case "atr": { int p = P(a[0]); if (p < 1) return double.NaN; n.E[0] ??= new EmaS(p, true); return n.E[0]!.Update(c.Tr[c.Bar]); }
            case "crossover":
            case "crossunder":
            {
                bool valid = !double.IsNaN(a[0]) && !double.IsNaN(a[1]); bool r = false;
                if (n.Init && valid) r = n.Name == "crossover" ? n.S0 <= n.S1 && a[0] > a[1] : n.S0 >= n.S1 && a[0] < a[1];
                n.S0 = a[0]; n.S1 = a[1]; n.Init = valid; return r ? 1 : 0;
            }
            case "barssince":
            {
                if (Ctx.Truthy(a[0])) { n.Init = true; n.S0 = 0; } else if (n.Init) n.S0++;
                return n.Init ? n.S0 : double.NaN;
            }
            case "macd":
            case "macd_signal":
            case "macd_hist":
            {
                int f = P(a[1]), s = P(a[2]); if (f < 1 || s < 1) return double.NaN;
                n.E[0] ??= new EmaS(f, false); n.E[1] ??= new EmaS(s, false);
                double ef = n.E[0]!.Update(a[0]), es = n.E[1]!.Update(a[0]);
                double line = double.IsNaN(ef) || double.IsNaN(es) ? double.NaN : ef - es;
                if (n.Name == "macd") return line;
                int sg = P(a[3]); if (sg < 1) return double.NaN;
                n.E[2] ??= new EmaS(sg, false);
                double sig = n.E[2]!.Update(line);
                if (double.IsNaN(line) || double.IsNaN(sig)) return double.NaN;
                return n.Name == "macd_signal" ? sig : line - sig;
            }
            case "bb_upper":
            case "bb_lower":
            {
                int p = P(a[1]); double m = Mean(n.H0, p), sd = Std(n.H0, p);
                return double.IsNaN(m) ? double.NaN : (n.Name == "bb_upper" ? m + a[2] * sd : m - a[2] * sd);
            }
            case "stoch_k":
            {
                int p = P(a[0]); if (p < 1 || c.Bar + 1 < p) return double.NaN;
                double hh = double.MinValue, ll = double.MaxValue;
                for (int i = c.Bar - p + 1; i <= c.Bar; i++) { hh = Math.Max(hh, c.H[i]); ll = Math.Min(ll, c.L[i]); }
                return hh == ll ? double.NaN : (c.C[c.Bar] - ll) / (hh - ll) * 100;
            }
            case "adx":
            case "plus_di":
            case "minus_di":
            {
                int p = P(a[0]); if (p < 1 || c.Bar == 0) return double.NaN;
                int b = c.Bar; double up = c.H[b] - c.H[b - 1], dn = c.L[b - 1] - c.L[b];
                double pdm = up > dn && up > 0 ? up : 0, mdm = dn > up && dn > 0 ? dn : 0;
                for (int i = 0; i < 4; i++) n.E[i] ??= new EmaS(p, true);
                double st = n.E[0]!.Update(c.Tr[b]), sp = n.E[1]!.Update(pdm), sm = n.E[2]!.Update(mdm);
                if (double.IsNaN(st) || st == 0 || double.IsNaN(sp) || double.IsNaN(sm)) return double.NaN;
                double pdi = 100 * sp / st, mdi = 100 * sm / st;
                if (n.Name == "plus_di") return pdi; if (n.Name == "minus_di") return mdi;
                double dx = pdi + mdi == 0 ? 0 : 100 * Math.Abs(pdi - mdi) / (pdi + mdi);
                return n.E[3]!.Update(dx);
            }
            case "cci":
            {
                int p = P(a[1]); double m = Mean(n.H0, p); if (double.IsNaN(m)) return double.NaN;
                double md = 0; for (int i = n.H0.Count - p; i < n.H0.Count; i++) md += Math.Abs(n.H0[i] - m); md /= p;
                return md == 0 ? 0 : (n.H0[^1] - m) / (0.015 * md);
            }
            case "supertrend":
            case "supertrend_dir":
            {
                int p = P(a[0]); double mult = a[1]; if (p < 1) return double.NaN;
                n.E[0] ??= new EmaS(p, true);
                double atr = n.E[0]!.Update(c.Tr[c.Bar]); double close = c.C[c.Bar], src = (c.H[c.Bar] + c.L[c.Bar]) / 2;
                if (double.IsNaN(atr)) { n.S3 = close; return double.NaN; }
                double up = src - mult * atr, dn = src + mult * atr;
                if (!n.Init) { n.S0 = up; n.S1 = dn; n.S2 = 1; n.Init = true; n.S3 = close; }
                else
                {
                    double up1 = n.S0, dn1 = n.S1;
                    up = n.S3 > up1 ? Math.Max(up, up1) : up; dn = n.S3 < dn1 ? Math.Min(dn, dn1) : dn;
                    double dir = n.S2; if (dir == -1 && close > dn1) dir = 1; else if (dir == 1 && close < up1) dir = -1;
                    n.S2 = dir; n.S0 = up; n.S1 = dn; n.S3 = close;
                }
                return n.Name == "supertrend_dir" ? n.S2 : (n.S2 == 1 ? n.S0 : n.S1);
            }
            case "session_min":
            {   // minutes since the session started (session = a 24h cycle starting at a chosen UTC minute-of-day)
                double mod = Math.Floor(c.Tm[c.Bar] / 60) % 1440;
                return ((mod - a[0]) % 1440 + 1440) % 1440;
            }
            case "new_session":
            {
                double key = Math.Floor((c.Tm[c.Bar] / 60 - a[0]) / 1440);
                bool fresh = n.Init && key != n.S0; n.S0 = key; n.Init = true; return fresh ? 1 : 0;
            }
            case "vwap":
            {   // volume-weighted average price, restarting at the chosen UTC minute-of-day each day
                double start = a.Length > 0 ? a[0] : 0, key = Math.Floor((c.Tm[c.Bar] / 60 - start) / 1440);
                if (!n.Init || key != n.S2) { n.S0 = 0; n.S1 = 0; n.S2 = key; n.Init = true; }
                double tp = (c.H[c.Bar] + c.L[c.Bar] + c.C[c.Bar]) / 3, v = c.V[c.Bar];
                n.S0 += tp * v; n.S1 += v;
                return n.S1 > 0 ? n.S0 / n.S1 : tp;
            }
            case "vol":
            {
                int p = P(a[0]); if (p < 2 || c.Bar < p) return double.NaN;
                double s = 0, s2 = 0;
                for (int i = c.Bar - p + 1; i <= c.Bar; i++) { double r = Math.Log(c.C[i] / c.C[i - 1]); s += r; s2 += r * r; }
                double m = s / p, v = Math.Max(0, (s2 - p * m * m) / (p - 1));
                return Math.Sqrt(v) * Math.Sqrt(c.BarsPerYear);
            }
        }
        return double.NaN;
    }
}
