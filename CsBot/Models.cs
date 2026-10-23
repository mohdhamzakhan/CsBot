namespace CsBot;

public readonly record struct Candle(DateTime Time, double Open, double High, double Low, double Close, double Volume);

public enum Market { Spot, Futures }

/// <summary>ExitPrice (optional) = the open position was closed INSIDE this candle at that price (a stop or target level was hit); TargetWeight then applies from the next open.
/// TargetWeight = desired position as a fraction of equity. +1 = 100% long, -1 = 100% short (futures only), 0 = flat.</summary>
public readonly record struct Decision(double TargetWeight, double? ExitPrice = null);

public interface IStrategy
{
    string Name { get; }
    int WarmupBars { get; }
    /// <summary>Called once per CLOSED candle. The returned weight is executed at the NEXT candle's open (no lookahead).</summary>
    Decision OnCandle(Candle candle);
}
