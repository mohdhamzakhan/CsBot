namespace CsBot;

/// <summary>
/// All numbers are percentages and MUST be verified against your CoinSwitch PRO fee page / tier.
/// Published figures conflict (spot 0.04%-0.4%, futures 0.012/0.03% vs 0.024/0.065%), so defaults are conservative.
/// </summary>
public sealed class CostConfig
{
    public Market Market { get; init; }
    public double TakerFeePct { get; init; }       // before GST
    public double GstPct { get; init; } = 18;      // GST charged on the platform fee
    public double SlippagePct { get; init; } = 0.05;
    public double TdsPct { get; init; }            // Sec 194S: spot = on sell proceeds; futures = on every fill if > 0
    public double FundingPer8hPct { get; init; }   // futures only; charged to longs, never credited to shorts (conservative)
    public double TaxOnGainsPct { get; init; } = 30;
    public double MaxLeverage { get; init; } = 1;

    public double FeeRate => TakerFeePct / 100 * (1 + GstPct / 100);

    public static CostConfig DefaultSpot => new()
    {
        Market = Market.Spot, TakerFeePct = 0.4, TdsPct = 1, SlippagePct = 0.05, MaxLeverage = 1
    };

    // Futures TDS default 0: confirm how CoinSwitch applies TDS to derivatives before enabling.
    public static CostConfig DefaultFutures => new()
    {
        Market = Market.Futures, TakerFeePct = 0.065, TdsPct = 0, SlippagePct = 0.03, FundingPer8hPct = 0.01, MaxLeverage = 3
    };
}
