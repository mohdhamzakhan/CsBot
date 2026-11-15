using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using CsBot.Scripting;

namespace CsBot;

public sealed record MarketRef(string Market, string Exchange, string Symbol, int IntervalMin, int Days);
public sealed record CostIn(double? Fee, double? Gst, double? Slippage, double? Tds, double? Funding, double? Leverage);
public sealed record StrategyIn(string Id, Dictionary<string, JsonElement>? Params, JsonElement? Rules, string? Code = null);
public sealed record KeysReq(string ApiKey, string SecretKey);
public sealed record ScriptReq(string Code);
public sealed record SaveScriptReq(string Name, string Code);
public sealed record BacktestReq(MarketRef Data, StrategyIn Strategy, CostIn? Costs, double? StartCash, bool? Refresh);
public sealed record RiskIn(double MaxOrderNotional, double MaxPositionNotional, double MaxLeverage, double DailyLossPct);
public sealed record TraderReq(string Mode, MarketRef Data, StrategyIn Strategy, CostIn? Costs, double Capital, RiskIn? Risk,
    int? PricePrecision, int? QtyPrecision, string? Confirm, bool? AllowUngated);

public static class Api
{
    internal static Market ParseMarket(string s) => s.Equals("futures", StringComparison.OrdinalIgnoreCase) ? Market.Futures : Market.Spot;
    private static double? N(double x) => double.IsFinite(x) ? Math.Round(x, 6) : null;
    private static long T(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static CostConfig BuildCost(Market m, CostIn? c)
    {
        var d = m == Market.Futures ? CostConfig.DefaultFutures : CostConfig.DefaultSpot;
        if (c == null) return d;
        return new CostConfig
        {
            Market = m, TakerFeePct = c.Fee ?? d.TakerFeePct, GstPct = c.Gst ?? d.GstPct, SlippagePct = c.Slippage ?? d.SlippagePct,
            TdsPct = c.Tds ?? d.TdsPct, FundingPer8hPct = c.Funding ?? d.FundingPer8hPct, MaxLeverage = c.Leverage ?? d.MaxLeverage
        };
    }

    private static object Metrics(BacktestResult r) => new
    {
        name = r.Strategy, startEquity = r.StartEquity, finalEquity = N(r.FinalEquity), returnPct = N(r.TotalReturnPct), postTaxReturnPct = N(r.PostTaxReturnPct),
        cagrPct = N(r.CagrPct), sharpe = N(r.Sharpe), maxDrawdownPct = N(r.MaxDrawdownPct), trades = r.Trades, fees = N(r.FeesPaid), slippage = N(r.SlippagePaid),
        tdsWithheld = N(r.TdsWithheld), funding = N(r.FundingPaid), turnover = N(r.Turnover), feeDragPct = N(r.FeeDragPct), estTax = N(r.EstTax), busted = r.Busted
    };

    /// <summary>Only the local machine's own browser may change keys (guards against other sites / DNS-rebinding tricks).</summary>
    private static bool IsLocal(HttpContext c)
    {
        var ip = c.Connection.RemoteIpAddress;
        if (ip != null && !IPAddress.IsLoopback(ip)) return false;
        static bool Loopback(string h) => h is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        if (!Loopback(c.Request.Host.Host)) return false;
        if (c.Request.Headers.TryGetValue("Origin", out var o) && (!Uri.TryCreate(o.ToString(), UriKind.Absolute, out var u) || !Loopback(u.Host))) return false;
        return true;
    }

    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });
    private static IResult Fail(Exception e) => e is ArgumentException or InvalidOperationException ? Bad(e.Message) : Results.Json(new { error = e.Message }, statusCode: 500);

    private static double[] Ema(List<Candle> c, int n)
    {
        var o = new double[c.Count]; double a = 2.0 / (n + 1);
        for (int i = 0; i < c.Count; i++) o[i] = i == 0 ? c[0].Close : o[i - 1] + a * (c[i].Close - o[i - 1]);
        return o;
    }

    private static object[] ScriptOverlays(ScriptStrategy ss, List<Candle> c) =>
        ss.Plots.Select(p => (object)new
        {
            name = p.Name, color = p.Color, pane = p.Pane,
            points = Enumerable.Range(0, Math.Min(p.Values.Length, c.Count)).Where(i => double.IsFinite(p.Values[i]))
                .Select(i => new { t = T(c[i].Time), v = Math.Round(p.Values[i], 6) }).ToArray()
        }).ToArray();

    private static object[] Overlays(string id, Dictionary<string, JsonElement>? p, JsonElement? rules, List<Candle> c)
    {
        var res = new List<object>();
        object Line(string name, string color, double[] v, int skip) =>
            new { name, color, pane = 0, points = Enumerable.Range(skip, Math.Max(0, c.Count - skip)).Where(i => double.IsFinite(v[i])).Select(i => new { t = T(c[i].Time), v = Math.Round(v[i], 6) }).ToArray() };
        switch (id)
        {
            case "trend_ema":
                { int f = (int)StrategyRegistry.Num(p, "fast", 20), s = (int)StrategyRegistry.Num(p, "slow", 100);
                  res.Add(Line($"EMA {f}", "#f5a623", Ema(c, f), f)); res.Add(Line($"EMA {s}", "#4a90e2", Ema(c, s), s)); break; }
            case "donchian":
                { int en = (int)StrategyRegistry.Num(p, "entry", 55), ex = (int)StrategyRegistry.Num(p, "exit", 20);
                  var up = new double[c.Count]; var lo = new double[c.Count];
                  for (int i = 0; i < c.Count; i++)
                  { up[i] = i >= en ? Enumerable.Range(i - en, en).Max(k => c[k].High) : double.NaN; lo[i] = i >= ex ? Enumerable.Range(i - ex, ex).Min(k => c[k].Low) : double.NaN; }
                  res.Add(Line($"High {en}", "#f5a623", up, en)); res.Add(Line($"Low {ex}", "#4a90e2", lo, ex)); break; }
            case "rules" when rules != null:
                { var rs = RuleSet.FromJson(rules.Value);
                  var ops = rs.EntryLong.Concat(rs.ExitLong).Concat(rs.EntryShort).Concat(rs.ExitShort).SelectMany(x => new[] { x.Left, x.Right })
                      .Where(o => o.Kind is "ema" or "sma").GroupBy(o => (o.Kind, o.Period)).Select(g => g.Key).Take(3).ToList();
                  string[] cols = { "#f5a623", "#4a90e2", "#bd10e0" }; int k = 0;
                  foreach (var (kind, per) in ops)
                  {
                      double[] v;
                      if (kind == "ema") v = Ema(c, per);
                      else { v = new double[c.Count]; for (int i = 0; i < c.Count; i++) v[i] = i + 1 >= per ? Enumerable.Range(i + 1 - per, per).Average(j => c[j].Close) : double.NaN; }
                      res.Add(Line($"{kind.ToUpper()} {per}", cols[k++ % 3], v, per));
                  }
                  break; }
        }
        return res.ToArray();
    }

    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/health", (HttpContext http) =>
        {
            bool keys = CoinSwitchClient.HasEnvKeys, match = false; string? err = null;
            if (keys) { try { using var c = CoinSwitchClient.FromEnv(); match = c.KeyMatchesSecret; } catch (Exception e) { err = e.Message; } }
            return new { hasKeys = keys, keyMatchesSecret = match, error = err, liveEnabled = Environment.GetEnvironmentVariable("CS_LIVE_ENABLED") == "1", keySource = KeyStore.Source, canEditKeys = IsLocal(http) };
        });

        // ---------------- API keys: store once, picked up automatically at every start ----------------
        app.MapGet("/api/keys/status", (HttpContext http) => new
        {
            canEditKeys = IsLocal(http),
            hasKeys = CoinSwitchClient.HasEnvKeys, source = KeyStore.Source, savePath = KeyStore.DefaultPath,
            apiKeyPreview = KeyStore.Mask(Environment.GetEnvironmentVariable("CS_API_KEY")),
            canRemove = KeyStore.Source != null && KeyStore.Source != "environment"
        });
        app.MapPost("/api/keys/save", async (KeysReq r, HttpContext http, CancellationToken ct) =>
        {
            if (!IsLocal(http)) return Results.Json(new { error = "Keys can only be changed from this computer's browser (localhost)." }, statusCode: 403);
            string api = (r.ApiKey ?? "").Trim().Trim('"', '\''), sec = (r.SecretKey ?? "").Trim().Trim('"', '\'');
            if (!Regex.IsMatch(api, "^[0-9a-fA-F]{64}$")) return Bad("The API key should be 64 hexadecimal characters (0-9, a-f). Check you copied all of it, with no spaces.");
            if (!Regex.IsMatch(sec, "^([0-9a-fA-F]{64}|[0-9a-fA-F]{128})$")) return Bad("The secret key should be 64 hexadecimal characters. Check you copied all of it, with no spaces.");
            string? warning = null;
            try
            {
                using var c = new CoinSwitchClient(api, sec);
                if (!c.KeyMatchesSecret) return Bad("This secret key does not belong to this API key. They must be the pair CoinSwitch generated together; the two may be swapped or from different pairs.");
                await c.SyncClockAsync(ct);
                using var v = await c.ValidateKeysAsync(ct);
            }
            catch (HttpRequestException e) when (e.Message.StartsWith("HTTP 401") || e.Message.StartsWith("HTTP 403")) { return Bad("CoinSwitch rejected these keys: " + e.Message); }
            catch (Exception e) { warning = "Saved, but I could not reach CoinSwitch to verify them just now: " + e.Message; }
            KeyStore.Save(api, sec);
            return Results.Ok(new { saved = true, path = KeyStore.DefaultPath, warning });
        });
        app.MapDelete("/api/keys", (HttpContext http) =>
        {
            if (!IsLocal(http)) return Results.Json(new { error = "Keys can only be changed from this computer's browser (localhost)." }, statusCode: 403);
            return Results.Ok(new { removed = KeyStore.Remove() });
        });

        app.MapGet("/api/strategies", () => StrategyRegistry.All);

        // ---------------- script editor ----------------
        app.MapGet("/api/script/templates", () => Templates.All);
        app.MapPost("/api/script/check", (ScriptReq r) =>
        {
            try { var (inputs, warm) = ScriptStrategy.Check(r.Code ?? ""); return Results.Ok(new { ok = true, inputs, warmupBars = warm }); }
            catch (ScriptException e) { return Results.Ok(new { ok = false, error = e.Message, line = e.Line }); }
        });
        string ScriptDir() { Directory.CreateDirectory(Path.Combine("data", "scripts")); return Path.Combine("data", "scripts"); }
        string Safe(string n) => Regex.Replace(n ?? "", @"[^A-Za-z0-9 _\-]", "").Trim();
        app.MapGet("/api/script/saved", () => Directory.GetFiles(ScriptDir(), "*.csscript").OrderBy(f => f)
            .Select(f => new { name = Path.GetFileNameWithoutExtension(f), code = File.ReadAllText(f) }));
        app.MapPost("/api/script/save", (SaveScriptReq r) =>
        {
            string name = Safe(r.Name);
            if (name.Length == 0 || name.Length > 60) return Bad("Give the script a name (letters, numbers, spaces, - and _).");
            if ((r.Code ?? "").Length > 40_000) return Bad("Script is too long.");
            File.WriteAllText(Path.Combine(ScriptDir(), name + ".csscript"), r.Code ?? "");
            return Results.Ok(new { saved = name });
        });
        app.MapDelete("/api/script/saved", (string name) =>
        {
            string f = Path.Combine(ScriptDir(), Safe(name) + ".csscript");
            if (File.Exists(f)) File.Delete(f);
            return Results.Ok(new { deleted = Safe(name) });
        });

        app.MapGet("/api/costs/defaults", () => new
        {
            spot = CostConfig.DefaultSpot, futures = CostConfig.DefaultFutures,
            notes = "Percentages. Spot fee default is the conservative worst case; use 'Load my fee' to read your real tier. Futures TDS defaults to 0 until you confirm how it applies to derivatives."
        });

        app.MapGet("/api/fees/spot", async (string? exchange, string? coin, CancellationToken ct) =>
        {
            try
            {
                if (!CoinSwitchClient.HasEnvKeys) return Bad("Set CS_API_KEY and CS_SECRET_KEY first.");
                using var cs = CoinSwitchClient.FromEnv();
                using var d = await cs.GetAsync("/trade/api/v2/tradingFee", new[] { new KeyValuePair<string, string>("exchange", exchange ?? "coinswitchx") }, ct);
                var (tk, mk) = FeeTools.FindCoinFee(d.RootElement, coin ?? "BTC");
                return Results.Ok(new { takerPct = tk, makerPct = mk, found = tk != null });
            }
            catch (Exception e) { return Fail(e); }
        });

        app.MapGet("/api/portfolio", async (string market, string? symbol, CancellationToken ct) =>
        {
            try { return Results.Ok(await PortfolioFeed.GetAsync(ParseMarket(market), symbol, ct)); }
            catch (Exception e) { return Fail(e); }
        });

        app.MapGet("/api/live", async (string market, string exchange, string symbol, int intervalMin, CancellationToken ct) =>
        {
            try { return Results.Ok(await LiveFeed.GetAsync(ParseMarket(market), exchange, symbol, intervalMin, ct)); }
            catch (Exception e) { return Fail(e); }
        });

        app.MapPost("/api/candles", async (MarketRef r, CancellationToken ct) =>
        {
            try
            {
                var c = await CandleStore.GetAsync(ParseMarket(r.Market), r.Exchange, r.Symbol, r.IntervalMin, r.Days, false, null, ct);
                return Results.Ok(new { candles = c.Select(x => new { t = T(x.Time), o = x.Open, h = x.High, l = x.Low, c = x.Close, v = x.Volume }), intervalSec = r.IntervalMin * 60 });
            }
            catch (Exception e) { return Fail(e); }
        });

        // Equity curves of several parameter sets on the same data and costs, for drawing them on one chart.
        app.MapPost("/api/curves", async (CurvesReq r, CancellationToken ct) =>
        {
            try
            {
                if (r.Strategy.Id == "rules") return Bad("Point-and-click rules have no numeric settings to compare.");
                if (r.Sets == null || r.Sets.Count == 0 || r.Sets.Count > 8) return Bad("Pick between 1 and 8 settings to compare.");
                var m = ParseMarket(r.Data.Market);
                var candles = await CandleStore.GetAsync(m, r.Data.Exchange, r.Data.Symbol, r.Data.IntervalMin, r.Data.Days, false, null, ct);
                if (candles.Count < 300) return Bad($"Only {candles.Count} candles; need at least 300.");
                double bpy = 365.25 * 1440 / r.Data.IntervalMin, cash = r.StartCash ?? 100_000;
                var cost = BuildCost(m, r.Costs);
                var benchCost = new CostConfig { Market = Market.Spot, TakerFeePct = cost.TakerFeePct, GstPct = cost.GstPct, SlippagePct = cost.SlippagePct, TdsPct = cost.TdsPct, MaxLeverage = 1 };
                int n = candles.Count, stride = Math.Max(1, (int)Math.Ceiling(n / 1500.0));
                var idx = Enumerable.Range(0, n).Where(i => i % stride == 0 || i == n - 1).ToArray();
                double[] Pick(double[] e) => idx.Select(i => Math.Round(e[i], 2)).ToArray();
                var bench = Backtester.Run(new BuyAndHoldStrategy(), candles, benchCost, bpy, cash);
                var curves = new object?[r.Sets.Count];
                await Task.Run(() => Parallel.For(0, r.Sets.Count, new ParallelOptions { CancellationToken = ct }, i =>
                {
                    var pd = new Dictionary<string, JsonElement>(r.Strategy.Params ?? new());
                    foreach (var kv in r.Sets[i]) pd[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
                    var (factory, band) = StrategyRegistry.Create(r.Strategy.Id, pd, r.Strategy.Rules, m, bpy, cost.MaxLeverage, r.Strategy.Code);
                    var res = Backtester.Run(factory(), candles, cost, bpy, cash, band);
                    curves[i] = new { @params = r.Sets[i], equity = Pick(res.Equity), postTaxPct = N(res.PostTaxReturnPct) };
                }), ct);
                return Results.Ok(new { t = idx.Select(i => T(candles[i].Time)).ToArray(), bench = Pick(bench.Equity), curves });
            }
            catch (Exception e) { return Fail(e); }
        });

        app.MapPost("/api/optimize", async (OptimizeReq r, CancellationToken ct) =>
        {
            try
            {
                if (r.Strategy.Id == "rules") return Bad("Point-and-click rules have no numeric settings to search. Use a built-in strategy or a script with input() settings.");
                var m = ParseMarket(r.Data.Market);
                var candles = await CandleStore.GetAsync(m, r.Data.Exchange, r.Data.Symbol, r.Data.IntervalMin, r.Data.Days, false, null, ct);
                if (candles.Count < 600) return Bad($"Only {candles.Count} candles; a parameter search needs at least 600. Increase the history or use a smaller timeframe.");
                var cost = BuildCost(m, r.Costs);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(120));
                var result = await Task.Run(() => Optimizer.Run(candles, r.Data, r.Strategy, cost, r.StartCash ?? 100_000, r.Ranges ?? new(), Math.Clamp(r.TrainPct ?? 0.7, 0.5, 0.9), cts.Token), cts.Token);
                return Results.Ok(result);
            }
            catch (OperationCanceledException) { return Bad("The search took longer than 2 minutes and was stopped. Use fewer combinations."); }
            catch (Exception e) { return Fail(e); }
        });

        // Prices at which the strategy would buy / exit / short, found by asking the real strategy about many hypothetical closes.
        app.MapPost("/api/triggers", async (TriggersReq r, CancellationToken ct) =>
        {
            try
            {
                if (r.Strategy.Id == "rules" && r.Strategy.Rules == null) return Bad("Missing rules.");
                var m = ParseMarket(r.Data.Market);
                // load exactly as much history as the trader itself uses (warm-up + 400 candles), whatever timeframe the chart is showing
                var (probe, _) = StrategyRegistry.Create(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, m, 365.25 * 1440 / r.Data.IntervalMin, m == Market.Futures ? 3 : 1, r.Strategy.Code);
                int need = Math.Min(1500, probe().WarmupBars + 400), days = (int)Math.Ceiling(need * r.Data.IntervalMin / 1440.0) + 3;
                var candles = await CandleStore.GetAsync(m, r.Data.Exchange, r.Data.Symbol, r.Data.IntervalMin, days, false, null, ct);
                var result = await Task.Run(() => Triggers.Compute(candles, r.Data, r.Strategy), ct);
                return Results.Ok(result);
            }
            catch (Exception e) { return Fail(e); }
        });

        app.MapPost("/api/backtest", async (BacktestReq r, CancellationToken ct) =>
        {
            try
            {
                var m = ParseMarket(r.Data.Market);
                var candles = await CandleStore.GetAsync(m, r.Data.Exchange, r.Data.Symbol, r.Data.IntervalMin, r.Data.Days, r.Refresh == true, null, ct);
                if (candles.Count < 300) return Bad($"Only {candles.Count} candles; need at least 300. Increase days or use a smaller interval.");
                double bpy = 365.25 * 1440 / r.Data.IntervalMin, cash = r.StartCash ?? 100_000;
                var cost = BuildCost(m, r.Costs);
                var (factory, band) = StrategyRegistry.Create(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, m, bpy, cost.MaxLeverage, r.Strategy.Code);
                var strat = factory();
                var res = Backtester.Run(strat, candles, cost, bpy, cash, band);
                var benchCost = new CostConfig { Market = Market.Spot, TakerFeePct = cost.TakerFeePct, GstPct = cost.GstPct, SlippagePct = cost.SlippagePct, TdsPct = cost.TdsPct, MaxLeverage = 1 };
                var bench = Backtester.Run(new BuyAndHoldStrategy(), candles, benchCost, bpy, cash);
                var wf = Backtester.WalkForward(factory, candles, cost, bpy, 5, factory().WarmupBars, band);
                bool pass = LiveGate.Passes(res, bench, wf, out var why);
                GateStore.Record(GateStore.Key(r.Data.Market, r.Data.Symbol, r.Data.IntervalMin, r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, r.Strategy.Code), pass);
                return Results.Ok(new
                {
                    candles = candles.Select(x => new { t = T(x.Time), o = x.Open, h = x.High, l = x.Low, c = x.Close, v = x.Volume }),
                    overlays = strat is ScriptStrategy ss ? ScriptOverlays(ss, candles) : Overlays(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, candles),
                    equity = res.Equity.Select((v, i) => new { t = T(candles[i].Time), v = Math.Round(v, 2) }),
                    benchEquity = bench.Equity.Select((v, i) => new { t = T(candles[i].Time), v = Math.Round(v, 2) }),
                    fills = res.Fills.Select(f => new { t = T(f.Time), side = f.Side, price = f.Price, qty = f.Qty }),
                    metrics = Metrics(res), benchmark = Metrics(bench), walkForward = wf.Select(x => N(x)), gate = new { pass, reason = why },
                    costs = cost, warmupBars = factory().WarmupBars, intervalSec = r.Data.IntervalMin * 60
                });
            }
            catch (Exception e) { return Fail(e); }
        });

        // ---------------- paper / live trading ----------------
        app.MapPost("/api/trader/start", (TraderReq r, TraderManager mgr) =>
        {
            try
            {
                var m = ParseMarket(r.Data.Market);
                var cost = BuildCost(m, r.Costs);
                double bpy = 365.25 * 1440 / r.Data.IntervalMin;
                var (factory, band) = StrategyRegistry.Create(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, m, bpy, cost.MaxLeverage, r.Strategy.Code);
                bool live = r.Mode == "live";
                var risk = r.Risk ?? new RiskIn(0, 0, 1, 0);
                if (!CoinSwitchClient.HasEnvKeys) return Bad("Set CS_API_KEY and CS_SECRET_KEY first.");
                if (r.Capital <= 0) return Bad("Capital must be positive.");
                if (live)
                {
                    if (Environment.GetEnvironmentVariable("CS_LIVE_ENABLED") != "1")
                        return Results.Json(new { error = "Live trading is disabled. Set environment variable CS_LIVE_ENABLED=1 and restart the server." }, statusCode: 403);
                    if (r.Confirm != "I UNDERSTAND THE RISKS") return Bad("Type the confirmation phrase exactly: I UNDERSTAND THE RISKS");
                    if (risk.MaxOrderNotional <= 0 || risk.MaxPositionNotional <= 0 || risk.DailyLossPct <= 0 || risk.MaxLeverage < 1)
                        return Bad("Live mode requires max order size, max position size, daily loss % and max leverage (>=1).");
                    if (risk.MaxPositionNotional > r.Capital * Math.Max(1, risk.MaxLeverage) * 1.01) return Bad("Max position size cannot exceed capital x leverage.");
                    string key = GateStore.Key(r.Data.Market, r.Data.Symbol, r.Data.IntervalMin, r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, r.Strategy.Code);
                    if (!GateStore.PassedRecently(key) && r.AllowUngated != true)
                        return Results.Json(new { error = "This exact strategy/settings has not passed the live gate in the last 24h. Run its backtest first (it must PASS), or explicitly allow ungated live trading." }, statusCode: 403);
                }
                if (mgr.Current is not { IsRunning: true } && SessionStore.LoadPosition() != null)
                    return Bad("An earlier session still has an OPEN POSITION on record. Resume it or discard it first (see the banner on the Paper / Live tab).");
                var t = TraderFactory.Build(r, TraderFactory.ToJson(r));
                mgr.Start(t);
                return Results.Ok(t.Status());
            }
            catch (Exception e) { return Fail(e); }
        });

        app.MapGet("/api/trader/status", (TraderManager mgr) => mgr.Current?.Status() ?? (object)new { state = "none", running = false });
        app.MapPost("/api/trader/stop", async (TraderManager mgr) => { if (mgr.Current != null) await mgr.Current.StopAsync(); return mgr.Current?.Status() ?? (object)new { state = "none" }; });
        app.MapPost("/api/trader/kill", async (TraderManager mgr) => { if (mgr.Current != null) await mgr.Current.KillAsync(); return mgr.Current?.Status() ?? (object)new { state = "none" }; });

        // ---- saved position / session (survives restarts) ----
        app.MapGet("/api/trader/session", (TraderManager mgr) => SessionResume.Describe(mgr));
        app.MapPost("/api/trader/resume", (ResumeReq r, TraderManager mgr) =>
        {
            try { return Results.Ok(SessionResume.Resume(mgr, r.Confirm).Status()); } catch (Exception e) { return Fail(e); }
        });
        app.MapPost("/api/trader/discard", (TraderManager mgr) =>
        {
            if (mgr.Current is { IsRunning: true }) return Bad("Stop the running trader first.");
            SessionStore.DeleteSession(); SessionStore.DeletePosition();
            return Results.Ok(new { discarded = true });
        });
        app.MapPost("/api/trader/close", async (TraderManager mgr) =>
        {
            if (mgr.Current is not { IsRunning: true } t) return Bad("No trader is running.");
            try { await t.CloseAsync(); return Results.Ok(t.Status()); } catch (Exception e) { return Fail(e); }
        });
        app.MapPost("/api/trader/forget", (TraderManager mgr) =>
        {
            if (mgr.Current is not { } t) return Bad("No trader.");
            if (t.Config.Mode != "live") return Bad("Only needed for live trading.");
            t.ForgetPosition(); return Results.Ok(t.Status());
        });
    }
}
