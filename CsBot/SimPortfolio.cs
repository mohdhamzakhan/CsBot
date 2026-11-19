namespace CsBot;

public sealed record Fill(DateTime Time, string Side, double Price, double Qty, double Fee, double Weight);

/// <summary>
/// Shared simulated account used by the backtester AND the paper trader, so both apply identical
/// fee / GST / slippage / TDS / funding rules.
/// </summary>
public sealed class SimPortfolio
{
    private readonly CostConfig _c;
    private readonly double _minRebalance, _maxLong, _maxShort;

    public double Cash, Units, Fees, TdsWithheld, Funding, Traded, SlipCost, Entry;
    public int Trades;
    /// <summary>Smallest order the exchange accepts, in coin units (0 = no minimum). Set by the paper trader so paper behaves like live.</summary>
    public double MinQty;

    public SimPortfolio(CostConfig cost, double startCash, double minRebalance = 0.15)
    {
        _c = cost; Cash = startCash; _minRebalance = minRebalance;
        double slip = cost.SlippagePct / 100;
        _maxLong = cost.Market == Market.Spot ? 1.0 / (1 + cost.FeeRate + slip + 1e-4) : cost.MaxLeverage;
        _maxShort = cost.Market == Market.Spot ? 0 : cost.MaxLeverage;
    }

    /// <summary>Money you can actually use (TDS withheld is locked until you claim it back).</summary>
    public double Liquid(double price) => Cash + Units * price;
    /// <summary>Liquid money + refundable TDS credit.</summary>
    public double Wealth(double price) => Liquid(price) + TdsWithheld;
    public double Weight(double price) { double e = Liquid(price); return e > 0 ? Units * price / e : 0; }

    public Fill? Rebalance(double target, double refPrice, DateTime time)
    {
        double w = Math.Clamp(target, -_maxShort, _maxLong);
        double eq0 = Liquid(refPrice);
        if (eq0 <= 0) return null;
        double curW = Units * refPrice / eq0;
        bool skip = (w == 0 && Units == 0)
            || (w != 0 && Math.Sign(w) == Math.Sign(curW) && Math.Abs(w - curW) < _minRebalance);
        if (skip) return null;

        double slip = _c.SlippagePct / 100;
        double targetUnits = w * eq0 / refPrice;
        double delta = targetUnits - Units;
        if (MinQty > 0 && targetUnits != 0)
        {
            // exchange minimum: a position smaller than the minimum cannot be opened; a resize smaller than the minimum cannot be sent
            if (Math.Abs(targetUnits) < MinQty) { if (Units == 0) return null; targetUnits = 0; delta = -Units; }
            else if (Math.Abs(delta) < MinQty) return null;
        }
        double fill = refPrice * (1 + Math.Sign(delta) * slip);
        double notional = Math.Abs(delta) * fill;
        double fee = notional * _c.FeeRate;
        SlipCost += Math.Abs(delta) * Math.Abs(fill - refPrice);
        Cash -= delta * fill + fee;
        Fees += fee;
        if (_c.TdsPct > 0 && (_c.Market == Market.Futures || delta < 0))
        {
            double t = notional * _c.TdsPct / 100;
            Cash -= t; TdsWithheld += t;
        }
        double oldUnits = Units;
        if (targetUnits == 0) Entry = 0;
        else if (oldUnits == 0 || Math.Sign(targetUnits) != Math.Sign(oldUnits)) Entry = fill;                       // new position
        else if (Math.Sign(delta) == Math.Sign(oldUnits)) Entry = (Entry * Math.Abs(oldUnits) + fill * Math.Abs(delta)) / Math.Abs(targetUnits);   // added to it
        Units = targetUnits; Trades++; Traded += notional;
        return new Fill(time, delta > 0 ? "buy" : "sell", fill, Math.Abs(delta), fee, w);
    }

    /// <summary>Futures funding: longs pay, shorts are NOT credited (conservative).</summary>
    public void AccrueFunding(double price, double hours)
    {
        if (_c.Market != Market.Futures || Units <= 0) return;
        double f = Units * price * _c.FundingPer8hPct / 100 * (hours / 8);
        Cash -= f; Funding += f;
    }
}