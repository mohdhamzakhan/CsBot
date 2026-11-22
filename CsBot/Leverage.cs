namespace CsBot;

/// <summary>
/// Multiplies a strategy's target position by the leverage YOU chose. A strategy's own weight is at most 1 (fully invested = 1x),
/// so position = weight x leverage x capital. Example: capital 100 USDT, leverage 10, strategy fully in -> a 1,000 USDT position.
/// Stops and targets (ExitPrice) pass through unchanged.
/// </summary>
public sealed class ScaledStrategy : IStrategy
{
    private readonly IStrategy _s;
    private readonly double _k;
    public ScaledStrategy(IStrategy s, double k) { _s = s; _k = k; }
    public string Name => _s.Name;
    public int WarmupBars => _s.WarmupBars;
    public Decision OnCandle(Candle c) { var d = _s.OnCandle(c); return new Decision(d.TargetWeight * _k, d.ExitPrice); }
}

public static class Leverage
{
    public const double Max = 50;

    /// <summary>Futures need an explicit choice (there is no silent default). Spot is always 1x.</summary>
    public static double Require(Market market, CostIn? costs)
    {
        if (market != Market.Futures) return 1;
        double? v = costs?.Leverage;
        if (v is null || !(v >= 1)) throw new ArgumentException("Choose a leverage for futures (for example 3x or 10x). The position is the strategy's signal x your leverage x your capital.");
        if (v > Max) throw new ArgumentException($"Leverage above {Max:0}x is not allowed here.");
        return v.Value;
    }

    /// <summary>Wraps a strategy factory so every new strategy it builds is scaled by the leverage (1x leaves it untouched).</summary>
    public static Func<IStrategy> Apply(Func<IStrategy> factory, double leverage) =>
        leverage > 1.0000001 ? () => new ScaledStrategy(factory(), leverage) : factory;
}