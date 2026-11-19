using System.Text.Json;
using System.Text.RegularExpressions;
using CsBot.Scripting;

namespace CsBot;

// ============================================================================================
// Portfolio: several strategies run on ONE capital as a single composite strategy, so it plugs
// into the normal backtest, live gate, research tools and Paper / Live trader unchanged.
//
//   net position = average of the members' target positions (each member limited to +-1)
//                  x risk scale x breaker scale, limited to your leverage cap
//
// Risk mitigation built in:
//   * Netting: strategies that disagree cancel out, so you never hold opposite positions.
//   * Equal allocation: each of N strategies gets 1/N of the capital budget.
//   * Per-strategy bench: a member whose own simulated equity falls stratDd% from its peak is
//     switched off for `cooldown` bars, then restarts with a fresh peak.
//   * Portfolio breaker: simulated portfolio drawdown >= softDd% halves the position (back to full
//     size once it recovers to half the limit); >= hardDd% goes flat for `cooldown` bars.
// The breakers use the portfolio's OWN simulated equity (from its signals), not your account.
// Your real account limits (max order / position, daily loss, kill switch) still apply on top.
// ============================================================================================

public sealed class PortfolioMember { public string Pick { get; set; } = ""; public Dictionary<string, double>? Params { get; set; } }
public sealed class PortfolioSpec { public List<PortfolioMember> Members { get; set; } = new(); }
public sealed record PortfolioRisk(double RiskScale, double SoftDdPct, double HardDdPct, double StratDdPct, int CooldownBars);

public sealed class PortfolioStrategy : IStrategy
{
    private readonly IReadOnlyList<IStrategy> _m;
    private readonly PortfolioRisk _r;
    private readonly double _maxNet;
    private readonly bool _short;
    private readonly double[] _prevW, _eq, _peak;
    private readonly int[] _bench;
    private double _prevClose, _prevNet, _pEq = 1, _pPeak = 1;
    private int _halt;
    private bool _soft;

    public PortfolioStrategy(IReadOnlyList<IStrategy> members, PortfolioRisk risk, double maxNet, bool allowShort)
    {
        _m = members; _r = risk; _maxNet = maxNet; _short = allowShort;
        int n = members.Count;
        _prevW = new double[n]; _eq = Enumerable.Repeat(1.0, n).ToArray(); _peak = Enumerable.Repeat(1.0, n).ToArray(); _bench = new int[n];
    }

    public string Name => $"Portfolio of {_m.Count}";
    public int WarmupBars => _m.Max(x => x.WarmupBars) + 2;
    /// <summary>How many members are currently benched, and whether the portfolio breaker is soft-limiting or halted (for logs and tests).</summary>
    public (int Benched, bool Soft, bool Halted) Risk => (_bench.Count(b => b > 0), _soft, _halt > 0);

    private static double Clean(double x) => double.IsFinite(x) ? x : 0;

    public Decision OnCandle(Candle c)
    {
        int n = _m.Count;
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = Math.Clamp(Clean(_m[i].OnCandle(c).TargetWeight), -1, 1);   // every member sees every candle, benched or not

        if (_prevClose > 0)
        {
            double ret = c.Close / _prevClose - 1;
            for (int i = 0; i < n; i++)
            {
                _eq[i] *= 1 + _prevW[i] * ret;                                                   // what the member's own signal would have earned
                if (_eq[i] > _peak[i]) _peak[i] = _eq[i];
                if (_bench[i] > 0) { if (--_bench[i] == 0) { _eq[i] = 1; _peak[i] = 1; } }      // back from the bench with a fresh peak
                else if (_peak[i] > 0 && (_peak[i] - _eq[i]) / _peak[i] * 100 >= _r.StratDdPct) _bench[i] = Math.Max(1, _r.CooldownBars);
            }
            _pEq *= 1 + _prevNet * ret;
            if (_pEq > _pPeak) _pPeak = _pEq;
            double dd = _pPeak > 0 ? (_pPeak - _pEq) / _pPeak * 100 : 0;
            if (_halt > 0) { if (--_halt == 0) { _pEq = 1; _pPeak = 1; _soft = false; } }
            else if (dd >= _r.HardDdPct) _halt = Math.Max(1, _r.CooldownBars);
            else if (dd >= _r.SoftDdPct) _soft = true;
            else if (dd < _r.SoftDdPct * 0.5) _soft = false;
        }

        double sum = 0;
        for (int i = 0; i < n; i++) if (_bench[i] == 0) sum += w[i];
        double scale = _halt > 0 ? 0 : _soft ? 0.5 : 1;
        double net = Math.Clamp(sum / n * _r.RiskScale * scale, _short ? -_maxNet : 0, _maxNet);

        Array.Copy(w, _prevW, n);
        _prevNet = net; _prevClose = c.Close;
        return new Decision(net);
    }
}

/// <summary>Builds a PortfolioStrategy from the request: members are given as picks ("t:" ready-made script, "b:" built-in, "s:" your saved script) in the strategy's Code field as JSON.</summary>
public static class PortfolioFactory
{
    public static PortfolioSpec Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("The portfolio has no strategies. Pick at least one.");
        try { return JsonSerializer.Deserialize<PortfolioSpec>(code, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PortfolioSpec(); }
        catch (JsonException) { throw new ArgumentException("The portfolio definition is damaged."); }
    }

    private static Func<IStrategy> Member(PortfolioMember m, Market market, double bpy)
    {
        var prm = (m.Params ?? new()).ToDictionary(k => k.Key, k => JsonSerializer.SerializeToElement(k.Value));
        string pick = m.Pick ?? "", id, code = "";
        if (pick.StartsWith("t:"))
        {
            var t = Templates.All.FirstOrDefault(x => x.Id == pick[2..]) ?? throw new ArgumentException("Unknown ready-made script: " + pick[2..]);
            id = "script"; code = t.Code;
        }
        else if (pick.StartsWith("b:"))
        {
            id = pick[2..];
            if (id is "portfolio" or "script" or "rules") throw new ArgumentException($"'{id}' cannot be a portfolio member.");
        }
        else if (pick.StartsWith("s:"))
        {
            string file = System.IO.Path.Combine("data", "scripts", Regex.Replace(pick[2..], @"[^A-Za-z0-9 _\-]", "").Trim() + ".csscript");
            if (!File.Exists(file)) throw new ArgumentException("Saved script not found: " + pick[2..]);
            id = "script"; code = File.ReadAllText(file);
        }
        else throw new ArgumentException("Pick strategies as t:<script id>, b:<built-in id> or s:<saved script name>.");
        // members are capped at 1x: the portfolio applies the risk scale and your leverage cap itself
        return StrategyRegistry.Create(id, prm, null, market, bpy, 1, code.Length > 0 ? code : null).Factory;
    }

    public static (Func<IStrategy> Factory, double Band) Create(Dictionary<string, JsonElement>? p, string? code, Market market, double barsPerYear, double maxW, bool allowShort)
    {
        var spec = Parse(code);
        if (spec.Members.Count is 0 or > 8) throw new ArgumentException("Pick between 1 and 8 strategies for the portfolio.");
        var risk = new PortfolioRisk(StrategyRegistry.Num(p, "riskScale", 1), StrategyRegistry.Num(p, "softDd", 8), StrategyRegistry.Num(p, "hardDd", 15),
            StrategyRegistry.Num(p, "stratDd", 12), (int)StrategyRegistry.Num(p, "cooldown", 24));
        if (risk.RiskScale <= 0 || risk.RiskScale > 5) throw new ArgumentException("Risk scale must be above 0 and at most 5.");
        if (risk.SoftDdPct <= 0 || risk.HardDdPct <= risk.SoftDdPct) throw new ArgumentException("The hard drawdown limit must be larger than the soft one.");
        var facs = spec.Members.Select(m => Member(m, market, barsPerYear)).ToList();     // validates every member now
        return (() => new PortfolioStrategy(facs.Select(f => f()).ToList(), risk, maxW, allowShort), StrategyRegistry.Num(p, "band", 0.1));
    }
}