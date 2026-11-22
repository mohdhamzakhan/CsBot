using System.Text.Json;
using CsBot.Scripting;

namespace CsBot;

public sealed record ParamDef(string Key, string Label, double Default, double Min, double Max, double Step);
public sealed record StrategyDef(string Id, string Name, string Description, string Evidence, ParamDef[] Params);

public static class StrategyRegistry
{
    public static readonly IReadOnlyList<StrategyDef> All = new[]
    {
        new StrategyDef("trend_ema", "Trend following (EMA + ATR stop)",
            "EMA fast/slow trend filter, ATR trailing stop, volatility-targeted size. Longs only on spot; long/short on futures.",
            "Trend/time-series momentum has academic support in crypto, but results are period-dependent and fragile to costs. A 2026 preprint reports strong out-of-sample numbers; treat as unproven.",
            new[] { P("fast", "Fast EMA", 20, 5, 100, 1), P("slow", "Slow EMA", 100, 20, 400, 1), P("stopAtr", "ATR stop (x)", 3, 1, 8, 0.5),
                    P("targetVol", "Target volatility (annual)", 0.4, 0.1, 1.0, 0.05), P("fixedSize", "Fixed size (0/1)", 0, 0, 1, 1), P("band", "Rebalance band", 0.15, 0.05, 0.5, 0.05) }),
        new StrategyDef("donchian", "Donchian breakout (Turtle-style)",
            "Enter on an N-bar high, exit on an M-bar low. A classic, widely studied trend system.",
            "Long track record in managed futures; in crypto it works in strong trends and whipsaws in ranges.",
            new[] { P("entry", "Entry lookback (bars)", 55, 10, 200, 1), P("exit", "Exit lookback (bars)", 20, 5, 100, 1),
                    P("targetVol", "Target volatility (annual)", 0.4, 0.1, 1.0, 0.05), P("fixedSize", "Fixed size (0/1)", 0, 0, 1, 1), P("band", "Rebalance band", 0.15, 0.05, 0.5, 0.05) }),
        new StrategyDef("tsmom", "Time-series momentum",
            "Long when the past N-bar return is positive (futures: short when negative), with a hysteresis band to limit churn.",
            "Best-documented anomaly in the literature, but one study found crypto time-series momentum faded after BTC futures launched.",
            new[] { P("lookback", "Lookback (bars)", 90, 10, 400, 1), P("bandPct", "Entry band (%)", 2, 0, 20, 0.5),
                    P("targetVol", "Target volatility (annual)", 0.4, 0.1, 1.0, 0.05), P("band", "Rebalance band", 0.15, 0.05, 0.5, 0.05) }),
        new StrategyDef("script", "Script editor (write your own, Pine-style)",
            "Write a strategy as code with indicators, variables, inputs and plots. Start from a template or a blank script.",
            "Whatever you test. Each tweak you make to look better in a backtest raises overfitting risk; rely on the walk-forward check.",
            Array.Empty<ParamDef>()),
        new StrategyDef("rules", "Custom rules (point-and-click)",
            "Build entry/exit conditions from indicators. Tested with the same costs and gate as the built-ins.",
            "Whatever you test. Each extra rule you tweak increases overfitting risk; rely on the walk-forward check.",
            new[] { P("band", "Rebalance band", 0.15, 0.05, 0.5, 0.05) }),
       new StrategyDef("portfolio", "Portfolio (several strategies, one capital, risk limits)",
    "Runs the strategies you pick on ONE capital as a single position: opposite signals cancel, size is capped, and drawdown breakers cut or stop the position.",
    "Combining several strategies smooths the ride but cannot create an edge that none of them has. Judge it by its drawdown and by out-of-sample tests, not only its return.",
    new[] { P("riskScale", "Risk scale (x)", 1, 0.25, 3, 0.25), P("softDd", "Soft drawdown limit % (halve position)", 8, 2, 30, 1),
            P("hardDd", "Hard drawdown limit % (go flat)", 15, 4, 50, 1), P("stratDd", "Bench a strategy after drawdown %", 12, 3, 40, 1),
            P("cooldown", "Cooldown (bars)", 24, 1, 400, 1), P("band", "Rebalance band", 0.1, 0.02, 0.5, 0.02),
            P("minAgree", "Minimum agreement, in votes (0 = off)", 0, 0, 16, 0.5), P("exitAgree", "Hold while agreement >= (0 = minimum - 1)", 0, 0, 16, 0.5) }),
    };

    private static ParamDef P(string k, string l, double d, double mn, double mx, double st) => new(k, l, d, mn, mx, st);

    public static double Num(Dictionary<string, JsonElement>? p, string key, double def)
    {
        if (p == null || !p.TryGetValue(key, out var e)) return def;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String when double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
            JsonValueKind.True => 1, JsonValueKind.False => 0, _ => def
        };
    }

    /// <summary>Returns (factory, rebalanceBand). Throws ArgumentException for bad input.</summary>
    public static (Func<IStrategy> Factory, double Band) Create(string id, Dictionary<string, JsonElement>? p, JsonElement? rules,
        Market market, double barsPerYear, double maxLeverage, string? code = null)
    {
        bool fut = market == Market.Futures;
        double maxW = fut ? maxLeverage : 1;
        double band = Num(p, "band", 0.15);
        switch (id)
        {
            case "trend_ema":
            {
                var pr = new TrendFollowingStrategy.Params((int)Num(p, "fast", 20), (int)Num(p, "slow", 100), 14, Num(p, "stopAtr", 3),
                    Num(p, "targetVol", 0.4), 60, barsPerYear, maxW, fut, Num(p, "fixedSize", 0) > 0);
                if (pr.FastEma >= pr.SlowEma) throw new ArgumentException("Fast EMA must be smaller than slow EMA.");
                return (() => new TrendFollowingStrategy(pr), band);
            }
            case "donchian":
                return (() => new DonchianStrategy((int)Num(p, "entry", 55), (int)Num(p, "exit", 20), Num(p, "targetVol", 0.4), barsPerYear, maxW, fut, Num(p, "fixedSize", 0) > 0), band);
            case "tsmom":
                return (() => new TsMomentumStrategy((int)Num(p, "lookback", 90), Num(p, "bandPct", 2), Num(p, "targetVol", 0.4), barsPerYear, maxW, fut), band);
            case "script":
            {
                if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("The script is empty.");
                try { ScriptStrategy.Check(code); } catch (ScriptException e) { throw new ArgumentException(e.Message); }
                var prm = p?.Keys.Where(k => k != "band").ToDictionary(k => k, k => Num(p, k, double.NaN));
                return (() => ScriptStrategy.Create(code, prm, barsPerYear, maxW, fut), band);
            }
            case "rules":
            {
                if (rules == null) throw new ArgumentException("Missing rules.");
                var rs = RuleSet.FromJson(rules.Value);
                var err = rs.Validate(fut); if (err != null) throw new ArgumentException(err);
                return (() => new RuleStrategy(RuleSet.FromJson(rules.Value), maxW, fut), band);
            }
            case "portfolio":
                return PortfolioFactory.Create(p, code, market, barsPerYear, maxW, fut);
            default: throw new ArgumentException("Unknown strategy: " + id);
        }
    }
}
