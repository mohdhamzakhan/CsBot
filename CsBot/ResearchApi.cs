using System.Text.Json;
using CsBot.Research;

namespace CsBot;

public sealed record McReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, MonteCarloRequest? Options);
public sealed record SensReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, List<ParamRange> Ranges, string? Objective, int? MinTrades, int? Radius);
public sealed record WfaReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, List<ParamRange> Ranges, WfaRequest Wfa);

/// <summary>
/// Research endpoints. Each POST validates, loads candles, starts a background job and returns { id }.
/// Poll GET /api/research/jobs/{id}  ->  { state: queued|running|done|failed|cancelled, progress 0..1, result, error }.
/// Login protection comes from the /api/ middleware in Program.cs.
/// </summary>
public static class ResearchApi
{
    private const long MaxWfaRuns = 30_000;

    private static IResult Fail(Exception e) => e is ArgumentException or InvalidOperationException
        ? Results.BadRequest(new { error = e.Message }) : Results.Json(new { error = e.Message }, statusCode: 500);

    /// <summary>Builds a fresh strategy (and its rebalance band) for a set of parameters, on top of the strategy's saved settings.</summary>
    private static Func<ParamSet, Built> MakeFactory(StrategyIn st, Market m, double bpy, double maxLev) => ps =>
    {
        var pd = new Dictionary<string, JsonElement>(st.Params ?? new());
        foreach (var kv in ps.Values) pd[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
        var (factory, band) = StrategyRegistry.Create(st.Id, pd, st.Rules, m, bpy, maxLev, st.Code);
        return new Built(factory(), band);
    };

    private static async Task<(List<Candle> Candles, Market M, double Bpy, CostConfig Cost)> Load(MarketRef d, CostIn? c, int minCandles, CancellationToken ct)
    {
        var m = Api.ParseMarket(d.Market);
        var candles = await CandleStore.GetAsync(m, d.Exchange, d.Symbol, d.IntervalMin, d.Days, false, null, ct);
        if (candles.Count < minCandles) throw new ArgumentException($"Only {candles.Count} candles; this analysis needs at least {minCandles}. Increase the history or use a smaller timeframe.");
        return (candles, m, 365.25 * 1440 / d.IntervalMin, Api.BuildCost(m, c));
    }

    private static List<Axis> ToAxes(List<ParamRange>? ranges)
    {
        if (ranges == null || ranges.Count == 0) throw new ArgumentException("Pick at least one setting to vary.");
        if (ranges.Count > 3) throw new ArgumentException("Vary at most 3 settings at a time.");
        var axes = ranges.Select(Axis.From).ToList();
        foreach (var a in axes) a.Validate();
        return axes;
    }

    /// <summary>Indicator warm-up of the strategy with its current settings (used to exclude start-up bars from metrics).</summary>
    private static int WarmupOf(Func<ParamSet, Built> factory)
    {
        try { return Math.Max(50, factory(new ParamSet(new Dictionary<string, double>())).Strategy.WarmupBars); }
        catch (ArgumentException) { return 200; }
    }

    public static void MapResearch(this WebApplication app)
    {
        var jobs = new ResearchJobs();

        // ---------- Monte Carlo ----------
        app.MapPost("/api/research/montecarlo", async (McReq r, CancellationToken ct) =>
        {
            try
            {
                var (candles, m, bpy, cost) = await Load(r.Data, r.Costs, 300, ct);
                var factory = MakeFactory(r.Strategy, m, bpy, cost.MaxLeverage);
                factory(new ParamSet(new Dictionary<string, double>()));          // fail fast on a bad strategy / script
                var job = jobs.Start("montecarlo", (progress, jct) =>
                {
                    var b = factory(new ParamSet(new Dictionary<string, double>()));
                    var bt = Backtester.Run(b.Strategy, candles, cost, bpy, 100_000, b.Band);
                    progress(0.3);
                    return MonteCarlo.Run(bt, bpy, r.Options ?? new MonteCarloRequest(), jct);
                });
                return Results.Ok(new { id = job.Id });
            }
            catch (Exception e) { return Fail(e); }
        });

        // ---------- Parameter sensitivity ----------
        app.MapPost("/api/research/sensitivity", async (SensReq r, CancellationToken ct) =>
        {
            try
            {
                var axes = ToAxes(r.Ranges);
                var objective = Objectives.ByName(r.Objective);
                long combos = GridRunner.Size(axes);
                if (combos > 1500) throw new ArgumentException($"{combos} combinations is too many (limit 1500). Use bigger steps or narrower ranges.");
                var (candles, m, bpy, cost) = await Load(r.Data, r.Costs, 600, ct);
                var factory = MakeFactory(r.Strategy, m, bpy, cost.MaxLeverage);
                int warm = WarmupOf(factory);
                var job = jobs.Start("sensitivity", (progress, jct) =>
                {
                    var grid = GridRunner.Run(factory, axes, candles, cost, bpy, objective, warm, 1500, null, progress, jct);
                    var an = Sensitivity.Analyze(grid, r.MinTrades ?? 5, Math.Clamp(r.Radius ?? 1, 0, 3))
                        ?? throw new InvalidOperationException("No setting combination produced enough trades. Lower the minimum trades or widen the ranges.");
                    return Sensitivity.Report(grid, an);
                });
                return Results.Ok(new { id = job.Id, combinations = combos });
            }
            catch (Exception e) { return Fail(e); }
        });

        // ---------- Walk-forward optimisation ----------
        app.MapPost("/api/research/walkforward", async (WfaReq r, CancellationToken ct) =>
        {
            try
            {
                var axes = ToAxes(r.Ranges);
                var (candles, m, bpy, cost) = await Load(r.Data, r.Costs, 600, ct);
                long est = WalkForwardOptimizer.EstimateRuns(candles.Count, axes, r.Wfa);
                if (est == 0) throw new ArgumentException("The windows are longer than the available history. Shorten them or load more history.");
                if (est > MaxWfaRuns) throw new ArgumentException($"This would run about {est:N0} backtests (limit {MaxWfaRuns:N0}). Use bigger steps, fewer settings, or fewer windows.");
                var factory = MakeFactory(r.Strategy, m, bpy, cost.MaxLeverage);
                var job = jobs.Start("walkforward", (progress, jct) =>
                    WalkForwardOptimizer.Run(factory, axes, candles, cost, bpy, r.Wfa, progress, jct));
                return Results.Ok(new { id = job.Id, estimatedRuns = est });
            }
            catch (Exception e) { return Fail(e); }
        });

        // ---------- job status ----------
        app.MapGet("/api/research/jobs/{id}", (string id) =>
            jobs.Get(id) is { } j
                ? Results.Ok(new { j.Id, j.Kind, state = j.State.ToString().ToLowerInvariant(), j.Progress, j.Error, result = j.State == JobState.Done ? j.Result : null })
                : Results.NotFound(new { error = "Unknown job (it may have expired)." }));

        app.MapDelete("/api/research/jobs/{id}", (string id) => Results.Ok(new { cancelled = jobs.Cancel(id) }));
    }
}
