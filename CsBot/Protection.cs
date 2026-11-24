using System.Text.Json;

namespace CsBot;

// ============================================================================================
// Protection for leveraged positions.
//
// All four limits are measured in % OF YOUR CAPITAL, not in price %, because with leverage the
// two are very different: a position of L x capital loses (price move) x L of your capital.
//   stop loss   : close when the trade has lost this % of capital        -> price distance = stopPct / L
//   take profit : close when the trade has gained this % of capital      -> price distance = takePct / L
//   trailing    : close when it falls this % of capital from its best    -> price distance = trailPct / L
//   guard       : close when price has covered this % of the distance to liquidation (about 1/L away)
// L is the size of the position as a multiple of capital at entry (for example 10 for 10x fully in).
// 0 turns a limit off. They are read from the strategy's settings: stopPct, takePct, trailPct, guardPct.
//
// In backtests and paper trading the levels are checked against each candle's high and low, and the exit
// fills at the level (at the open if the candle gaps through it). In LIVE trading this wrapper can only act
// when a candle has closed; for protection that rests on the exchange, see LiveProtection.cs.
// ============================================================================================

public sealed record ProtectionSpec(double StopPct, double TakePct, double TrailPct, double GuardPct, bool ExchangeOrders)
{
    public static readonly string[] Keys = { "stopPct", "takePct", "trailPct", "guardPct", "exchStops" };
    public bool Any => StopPct > 0 || TakePct > 0 || TrailPct > 0 || GuardPct > 0;

    public static ProtectionSpec FromParams(Dictionary<string, JsonElement>? p) =>
        new(StrategyRegistry.Num(p, "stopPct", 0), StrategyRegistry.Num(p, "takePct", 0), StrategyRegistry.Num(p, "trailPct", 0),
            StrategyRegistry.Num(p, "guardPct", 0), StrategyRegistry.Num(p, "exchStops", 0) > 0);

    public void Validate()
    {
        foreach (var (name, v, max) in new[] { ("Stop loss", StopPct, 100.0), ("Take profit", TakePct, 10000.0), ("Trailing stop", TrailPct, 100.0), ("Liquidation guard", GuardPct, 95.0) })
            if (double.IsNaN(v) || v < 0 || v > max) throw new ArgumentException($"{name} must be between 0 and {max:0}% of capital (0 = off).");
    }

    /// <summary>Settings without the protection keys, plus the protection read from them. Used where leverage is applied AFTER the strategy is built.</summary>
    public static (Dictionary<string, JsonElement>? Clean, ProtectionSpec Spec) Split(Dictionary<string, JsonElement>? p)
    {
        var spec = FromParams(p);
        if (p == null) return (null, spec);
        return (p.Where(kv => !Keys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), spec);
    }
}

public static class Protection
{
    /// <summary>Stop price, take-profit price and trailing distance (as a price fraction) for a position entered at <paramref name="entry"/>.</summary>
    public static (double? Stop, double? Take, double TrailDist) Levels(ProtectionSpec s, int dir, double entry, double effLev)
    {
        double lev = Math.Max(effLev, 1e-9);
        double dist = double.PositiveInfinity;
        if (s.StopPct > 0) dist = s.StopPct / 100 / lev;
        if (s.GuardPct > 0) dist = Math.Min(dist, s.GuardPct / 100 / lev);          // liquidation is roughly 1/L away
        double? stop = double.IsInfinity(dist) ? null : entry * (1 - dir * Math.Min(dist, 0.999));
        double? take = null;
        if (s.TakePct > 0) { double x = s.TakePct / 100 / lev; if (dir > 0 || x < 0.999) take = entry * (1 + dir * x); }   // a short cannot gain more than 100%
        return (stop, take, s.TrailPct > 0 ? Math.Min(s.TrailPct / 100 / lev, 0.999) : 0);
    }

    /// <summary>Wraps a factory so every strategy it builds is protected. No limits set = the factory is returned untouched.</summary>
    public static Func<IStrategy> Apply(Func<IStrategy> factory, ProtectionSpec spec)
    {
        spec.Validate();
        return spec.Any ? () => new ProtectedStrategy(factory(), spec) : factory;
    }
}

public sealed class ProtectedStrategy : IStrategy
{
    private readonly IStrategy _s;
    private readonly ProtectionSpec _p;
    private int _dir, _blocked;
    private double _stop, _take, _trail, _extreme;

    public ProtectedStrategy(IStrategy inner, ProtectionSpec spec) { _s = inner; _p = spec; }
    public string Name => _s.Name;
    public int WarmupBars => _s.WarmupBars;
    /// <summary>The position being protected and its current levels (for logs and tests).</summary>
    public (int Dir, double Stop, double Take) State => (_dir, EffectiveStop(), _take);

    private double EffectiveStop()
    {
        double s = _stop;
        if (_trail > 0 && _dir != 0)
        {
            double t = _dir > 0 ? _extreme * (1 - _trail) : _extreme * (1 + _trail);
            s = s == 0 ? t : (_dir > 0 ? Math.Max(s, t) : Math.Min(s, t));            // the tighter of the fixed and the trailing stop
        }
        return s;
    }

    public Decision OnCandle(Candle c)
    {
        var d = _s.OnCandle(c);                                                        // the wrapped strategy always sees every candle
        double w = double.IsFinite(d.TargetWeight) ? d.TargetWeight : 0;
        double? exit = null;

        // 1) the position that was open during this candle: did it touch a protective level? (the stop wins if both are inside one candle)
        if (_dir != 0)
        {
            bool lg = _dir > 0;
            double stop = EffectiveStop();
            bool stopHit = stop > 0 && (lg ? c.Low <= stop : c.High >= stop);
            bool takeHit = _take > 0 && (lg ? c.High >= _take : c.Low <= _take);
            if (stopHit) exit = lg ? Math.Min(c.Open, stop) : Math.Max(c.Open, stop);   // a gap through the level fills at the open
            else if (takeHit) exit = lg ? Math.Max(c.Open, _take) : Math.Min(c.Open, _take);
            if (exit != null) { _blocked = _dir; _dir = 0; _stop = _take = _trail = 0; w = 0; }
            else if (_trail > 0) _extreme = lg ? Math.Max(_extreme, c.High) : Math.Min(_extreme, c.Low);
        }

        // 2) after a protective exit the strategy is ignored until it stops wanting that side (no instant re-entry into the same trade)
        if (exit == null && _blocked != 0) { if (Math.Sign(w) == _blocked) w = 0; else _blocked = 0; }

        // 3) the wrapped strategy closed the position itself (its own stop or target)
        if (exit == null && d.ExitPrice != null) { _dir = 0; _stop = _take = _trail = 0; }

        // 4) a new entry, or a flip: work out its levels from the entry price and its size
        int sgn = Math.Abs(w) < 1e-9 ? 0 : Math.Sign(w);
        if (sgn == 0) { _dir = 0; _stop = _take = _trail = 0; }
        else if (sgn != _dir)
        {
            _dir = sgn; _extreme = c.Close;
            var (stop, take, trail) = Protection.Levels(_p, sgn, c.Close, Math.Abs(w));
            _stop = stop ?? 0; _take = take ?? 0; _trail = trail;
        }
        return new Decision(w, exit ?? d.ExitPrice);
    }
}