namespace CsBot;

public sealed record BacktestResult(
    string Strategy, double StartEquity, double FinalEquity, double CagrPct, double Sharpe,
    double MaxDrawdownPct, int Trades, double FeesPaid, double TdsWithheld, double FundingPaid,
    double EstTax, double PostTaxEquity, bool Busted, double Turnover, double SlippagePaid)
{
    public double TotalReturnPct => (FinalEquity / StartEquity - 1) * 100;
    public double PostTaxReturnPct => (PostTaxEquity / StartEquity - 1) * 100;
    public double GrossProfit => FinalEquity - StartEquity + FeesPaid + FundingPaid + SlippagePaid;
    /// <summary>Share of gross profit eaten by fees+funding+slippage. NaN if no gross profit.</summary>
    public double FeeDragPct => GrossProfit > 0 ? (FeesPaid + FundingPaid + SlippagePaid) / GrossProfit * 100 : double.NaN;
    public IReadOnlyList<Fill> Fills { get; init; } = Array.Empty<Fill>();
    public double[] Equity { get; init; } = Array.Empty<double>();
}

public static class Backtester
{
    public static BacktestResult Run(IStrategy strategy, IReadOnlyList<Candle> candles, CostConfig cost,
        double barsPerYear, double startCash = 100_000, double minRebalance = 0.15)
    {
        var pf = new SimPortfolio(cost, startCash, minRebalance);
        var fills = new List<Fill>();
        var eq = new double[candles.Count];
        double target = 0; bool pending = false, busted = false;

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            if (pending) { var f = pf.Rebalance(target, c.Open, c.Time); if (f != null) fills.Add(f); }  // next-open execution
            if (i > 0) pf.AccrueFunding(c.Close, (c.Time - candles[i - 1].Time).TotalHours);
            var d = strategy.OnCandle(c);
            if (d.ExitPrice is double xp && pf.Units != 0) { var xf = pf.Rebalance(0, xp, c.Time); if (xf != null) fills.Add(xf); }   // stop/target hit inside the candle
            eq[i] = pf.Wealth(c.Close);
            if (pf.Liquid(c.Close) <= 0) { busted = true; break; }
            target = d.TargetWeight;
            pending = true;
        }
        return Summarize(strategy.Name, eq, startCash, candles, pf, busted, cost, barsPerYear) with { Fills = fills, Equity = eq };
    }

    private static BacktestResult Summarize(string name, double[] eq, double start, IReadOnlyList<Candle> candles,
        SimPortfolio pf, bool busted, CostConfig cost, double barsPerYear)
    {
        double final = busted ? 0 : eq[^1];
        double peak = start, maxDd = 0, sum = 0, sumSq = 0; int n = 0;
        for (int i = 0; i < eq.Length; i++)
        {
            peak = Math.Max(peak, eq[i]);
            if (peak > 0) maxDd = Math.Max(maxDd, (peak - eq[i]) / peak);
            if (i > 0 && eq[i - 1] > 0) { double r = eq[i] / eq[i - 1] - 1; sum += r; sumSq += r * r; n++; }
        }
        double mean = n > 0 ? sum / n : 0;
        double sd = n > 1 ? Math.Sqrt(Math.Max(0, (sumSq - n * mean * mean) / (n - 1))) : 0;
        double sharpe = sd > 0 ? mean / sd * Math.Sqrt(barsPerYear) : 0;
        double years = candles.Count / barsPerYear;
        double cagr = final > 0 && years > 0 ? (Math.Pow(final / start, 1 / years) - 1) * 100 : -100;

        // India VDA tax estimate: flat 30% on gains, no loss set-off, fees not deductible (added back). TDS is already inside 'final'.
        double taxableGain = final - start + pf.Fees;
        double tax = cost.TaxOnGainsPct / 100 * Math.Max(0, taxableGain);
        return new BacktestResult(name, start, final, cagr, sharpe, maxDd * 100, pf.Trades, pf.Fees, pf.TdsWithheld,
            pf.Funding, tax, final - tax, busted, pf.Traded / start, pf.SlipCost);
    }

    /// <summary>Fixed-parameter performance across sequential segments (post-tax return %). No optimisation = no overfitting.</summary>
    public static IReadOnlyList<double> WalkForward(Func<IStrategy> factory, IReadOnlyList<Candle> candles,
        CostConfig cost, double barsPerYear, int segments, int warmup, double minRebalance = 0.15)
    {
        var res = new List<double>();
        int len = candles.Count / segments;
        for (int k = 0; k < segments; k++)
        {
            int s = k * len, e = k == segments - 1 ? candles.Count : s + len;
            int from = Math.Max(0, s - warmup);
            var slice = candles.Skip(from).Take(e - from).ToList();
            res.Add(Run(factory(), slice, cost, barsPerYear, minRebalance: minRebalance).PostTaxReturnPct);
        }
        return res;
    }
}

public static class LiveGate
{
    /// <summary>Strategy may go live only if it is profitable post-tax, beats buy&hold on Sharpe, and wins >=60% of segments.</summary>
    public static bool Passes(BacktestResult s, BacktestResult bench, IReadOnlyList<double> walkForward, out string reason)
    {
        double winShare = walkForward.Count(r => r > 0) / (double)walkForward.Count;
        if (s.Busted) { reason = "account busted"; return false; }
        if (s.PostTaxReturnPct <= 0) { reason = "post-tax return <= 0"; return false; }
        if (s.Sharpe <= bench.Sharpe) { reason = "Sharpe does not beat buy & hold"; return false; }
        if (winShare < 0.6) { reason = $"only {winShare:P0} of walk-forward segments profitable"; return false; }
        reason = "passed"; return true;
    }
}
