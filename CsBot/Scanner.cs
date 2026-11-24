using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsBot.Scripting;

namespace CsBot;

// ============================================================================================
// Background scanner. Runs every combination of (strategy x parameter variant x timeframe x symbol)
// as a lightweight PAPER "cell" on live candles. Cells share candle downloads (one per symbol and
// timeframe) and execute on paper when they signal. Nothing here can place a real order.
// A cell only starts trading after the strategy has gone flat once, so it reacts to FRESH signals.
// ============================================================================================

public sealed record ScanReq(string Market, List<string> Symbols, List<int> Timeframes, List<string> Picks, int? Variants, double Capital, CostIn? Costs, Dictionary<string, double>? Protect = null);
public sealed record ScanSignal(long T, string Cell, string Name, string Symbol, int Tf, string Params, string From, string To, double Price);
internal sealed record CellState(string Id, Dictionary<string, double> Pf, List<Fill> Fills, string Stance, bool Armed, double Peak, double MaxDd, long LastSignalAt, string LastSignal);
internal sealed record ScanFile(ScanReq Req, bool Running, DateTime Started, List<CellState> Cells, List<ScanSignal> Signals);

internal sealed class ScanCell
{
    public string Id = "", Name = "", Symbol = "", Pick = "", ParamText = "", Problem = "", Stance = "flat", LastSignal = "";
    public int Tf;
    public StrategyIn Strategy = null!;
    public MarketRef Data = null!;
    public double Capital, Band, Peak, MaxDd, LastClose;
    public CostConfig Cost = null!;
    public double Bpy, BtReturn, BtSharpe, BtMaxDd;       // backtest on the history loaded at start (pre-tax)
    public int BtTrades, BtBars;
    public bool BtDone;
    public Func<IStrategy> Factory = null!;
    public IStrategy? S;
    public SimPortfolio Pf = null!;
    public readonly List<Fill> Fills = new();
    public bool Armed, Ready;
    public int Bars;
    public long LastSignalAt;

    public static string StanceOf(double w) => w > 0.01 ? "long" : w < -0.01 ? "short" : "flat";
    private static long Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Replays history through a fresh strategy WITHOUT trading, so indicators are warm and the current stance is known.</summary>
    public void Warm(IReadOnlyList<Candle> hist)
    {
        S = Factory(); Stance = "flat"; Bars = 0;
        foreach (var c in hist) { var d = S.OnCandle(c); Stance = StanceOf(d.TargetWeight); LastClose = c.Close; Bars++; }
        Ready = hist.Count >= S.WarmupBars;
        if (!Ready) Problem = $"needs {S.WarmupBars} candles, only {hist.Count} available";
        Armed = Stance == "flat";                       // already long/short at start: wait for the strategy to go flat before taking the next entry
        Peak = Pf.Wealth(LastClose > 0 ? LastClose : 1);
    }

    /// <summary>One newly closed candle. Returns a signal when the stance changed (only once armed).</summary>
    public ScanSignal? Step(Candle c)
    {
        if (S == null || !Ready) return null;
        Bars++; LastClose = c.Close;
        var close = c.Time.AddMinutes(Tf);
        Pf.AccrueFunding(c.Close, Tf / 60.0);
        var d = S.OnCandle(c);
        if (Armed && d.ExitPrice is double xp && Pf.Units != 0) { var xf = Pf.Rebalance(0, xp, close); if (xf != null) Fills.Add(xf); }   // stop / target hit inside the candle
        string st = StanceOf(d.TargetWeight);
        ScanSignal? sig = null;
        if (!Armed) { if (st == "flat") Armed = true; }
        else
        {
            if (st != Stance)
            {
                sig = new ScanSignal(Unix(close), Id, Name, Symbol, Tf, ParamText, Stance, st, c.Close);
                LastSignalAt = sig.T; LastSignal = $"{Stance} \u2192 {st}";
            }
            var f = Pf.Rebalance(d.TargetWeight, c.Close, close);
            if (f != null) { Fills.Add(f); if (Fills.Count > 5000) Fills.RemoveAt(0); }
        }
        Stance = st;
        double eq = Pf.Wealth(c.Close);
        Peak = Math.Max(Peak, eq);
        if (Peak > 0) MaxDd = Math.Max(MaxDd, (Peak - eq) / Peak);
        return sig;
    }

    public Fill[] FillsCopy() { for (int i = 0; i < 3; i++) { try { return Fills.ToArray(); } catch { } } return Array.Empty<Fill>(); }

    public CellState Export() => new(Id, new Dictionary<string, double>
    {
        ["cash"] = Pf.Cash,
        ["units"] = Pf.Units,
        ["fees"] = Pf.Fees,
        ["tds"] = Pf.TdsWithheld,
        ["funding"] = Pf.Funding,
        ["traded"] = Pf.Traded,
        ["slip"] = Pf.SlipCost,
        ["entry"] = Pf.Entry,
        ["trades"] = Pf.Trades
    }, FillsCopy().ToList(), Stance, Armed, Peak, MaxDd, LastSignalAt, LastSignal);

    public void Restore(CellState s)
    {
        double G(string k) => s.Pf.TryGetValue(k, out var v) ? v : 0;
        Pf.Cash = G("cash"); Pf.Units = G("units"); Pf.Fees = G("fees"); Pf.TdsWithheld = G("tds"); Pf.Funding = G("funding");
        Pf.Traded = G("traded"); Pf.SlipCost = G("slip"); Pf.Entry = G("entry"); Pf.Trades = (int)G("trades");
        Fills.Clear(); Fills.AddRange(s.Fills);
        Stance = s.Stance; Armed = s.Armed; Peak = s.Peak; MaxDd = s.MaxDd; LastSignalAt = s.LastSignalAt; LastSignal = s.LastSignal;
    }
}

public sealed class ScannerManager
{
    public const int MaxCells = 400;
    private readonly AgentManager _agents;
    private readonly object _l = new(), _saveLock = new();
    private List<ScanCell> _cells = new();
    private readonly List<ScanSignal> _signals = new();
    private readonly ConcurrentDictionary<string, string> _groups = new();
    private ScanReq? _req;
    private CancellationTokenSource? _cts;
    private DateTime _started;
    private bool _running;
    private int _initDone;
    private static string FilePath => System.IO.Path.Combine("data", "state", "scanner.json");
    private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public ScannerManager(AgentManager agents) { _agents = agents; }

    // ---------------- building the combinations ----------------

    internal static double Snap(double v, (string Key, double Def, double Min, double Max, double Step) i)
    {
        double step = i.Step > 0 ? i.Step : 1;
        return Math.Round(Math.Clamp(Math.Round(v / step) * step, i.Min, i.Max), 6);
    }

    /// <summary>The default settings first, then one-setting-at-a-time variations (round-robin over the settings), at most <paramref name="count"/> in total.</summary>
    internal static List<Dictionary<string, double>> Variants((string Key, double Def, double Min, double Max, double Step)[] inputs, int count)
    {
        var res = new List<Dictionary<string, double>> { inputs.ToDictionary(i => i.Key, i => i.Def) };
        string K(Dictionary<string, double> d) => string.Join(",", d.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
        var seen = new HashSet<string> { K(res[0]) };
        foreach (double f in new[] { 0.7, 1.4, 0.5, 2.0 })
            foreach (var i in inputs)
            {
                if (res.Count >= count) return res;
                double v = Snap(i.Def * f, i);
                if (Math.Abs(v - i.Def) < 1e-9) continue;
                var d = new Dictionary<string, double>(res[0]) { [i.Key] = v };
                if (seen.Add(K(d))) res.Add(d);
            }
        return res;
    }

    private static string ExchangeFor(Market m, string sym) => m == Market.Futures ? CoinSwitchClient.FuturesExchange : sym.EndsWith("/USDT", StringComparison.OrdinalIgnoreCase) ? "c2c1" : "coinswitchx";

    private static (string StrategyId, string? Code, string Name, (string Key, double Def, double Min, double Max, double Step)[] Inputs) Resolve(string pick)
    {
        if (pick.StartsWith("t:"))
        {
            var t = Templates.All.FirstOrDefault(x => x.Id == pick[2..]) ?? throw new ArgumentException("Unknown ready-made script: " + pick[2..]);
            var inputs = ScriptStrategy.Check(t.Code).Inputs.Select(i => (i.Name, i.Default, i.Min ?? Math.Min(0, i.Default), i.Max ?? Math.Max(i.Default * 4, 1), i.Step)).ToArray();
            return ("script", t.Code, t.Name, inputs);
        }
        if (pick.StartsWith("b:"))
        {
            var d = StrategyRegistry.All.FirstOrDefault(x => x.Id == pick[2..]) ?? throw new ArgumentException("Unknown strategy: " + pick[2..]);
            var inputs = d.Params.Where(p => p.Key is not ("band" or "fixedSize")).Select(p => (p.Key, p.Default, p.Min, p.Max, p.Step)).ToArray();
            return (d.Id, null, d.Name, inputs);
        }
        throw new ArgumentException("Pick strategies as t:<script id> or b:<built-in id>.");
    }

    internal List<ScanCell> BuildCells(ScanReq r)
    {
        if (r.Capital <= 0) throw new ArgumentException("Capital must be above zero.");
        if (r.Symbols is not { Count: > 0 } || r.Symbols.Count > 4) throw new ArgumentException("Pick 1 to 4 symbols.");
        if (r.Timeframes is not { Count: > 0 } || r.Timeframes.Any(t => t < 1)) throw new ArgumentException("Pick at least one timeframe.");
        if (r.Picks is not { Count: > 0 } || r.Picks.Count > 24) throw new ArgumentException("Pick 1 to 24 strategies.");
        var m = Api.ParseMarket(r.Market);
        int variants = Math.Clamp(r.Variants ?? 4, 1, 8);
        var cost = Api.BuildCost(m, r.Costs);
        double lev = Leverage.Require(m, r.Costs);          // futures: you must choose the leverage
        var cells = new List<ScanCell>();
        foreach (var pick in r.Picks.Distinct())
        {
            var (sid, code, name, inputs) = Resolve(pick);
            foreach (var v in Variants(inputs, variants))
                foreach (var tf in r.Timeframes.Distinct())
                    foreach (var symRaw in r.Symbols.Distinct())
                    {
                        string sym = symRaw.Trim().ToUpperInvariant();
                        var pd = v.ToDictionary(k => k.Key, k => JsonSerializer.SerializeToElement(k.Value));
                        foreach (var kv in r.Protect ?? new()) if (ProtectionSpec.Keys.Contains(kv.Key)) pd[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);   // stop / take profit, same for every combination
                        var strat = new StrategyIn(sid, pd, null, code);
                        double bpy = 365.25 * 1440 / tf;
                        Func<IStrategy> factory; double band;
                        var (cleanP, protect) = ProtectionSpec.Split(strat.Params);
                        try { (factory, band) = StrategyRegistry.Create(strat.Id, cleanP, null, m, bpy, 1, strat.Code); }
                        catch (ArgumentException) { continue; }          // invalid combination (e.g. fast EMA >= slow EMA)
                        factory = Protection.Apply(Leverage.Apply(factory, lev), protect);
                        string ptxt = string.Join(" \u00B7 ", v.Select(x => $"{x.Key} {x.Value:0.##}"));
                        string raw = $"{m}|{sym}|{tf}|{pick}|{ptxt}";
                        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..10].ToLowerInvariant();
                        cells.Add(new ScanCell
                        {
                            Id = id,
                            Name = name,
                            Symbol = sym,
                            Tf = tf,
                            Pick = pick,
                            ParamText = ptxt,
                            Strategy = strat,
                            Capital = r.Capital,
                            Band = band,
                            Factory = factory,
                            Cost = cost,
                            Bpy = bpy,
                            Data = new MarketRef(m == Market.Futures ? "futures" : "spot", ExchangeFor(m, sym), sym, tf, 30),
                            Pf = new SimPortfolio(cost, r.Capital, band)
                        });
                    }
        }
        if (cells.Count == 0) throw new ArgumentException("No valid combinations to run.");
        if (cells.Count > MaxCells) throw new ArgumentException($"{cells.Count} combinations is too many (limit {MaxCells}). Pick fewer strategies, timeframes or variants.");
        return cells;
    }

    public object Preview(ScanReq r)
    {
        var cells = BuildCells(r);
        return new { cells = cells.Count, groups = cells.Select(c => (c.Symbol, c.Tf)).Distinct().Count(), limit = MaxCells };
    }

    // ---------------- lifecycle ----------------

    public void Start()                          // called once at startup: resume a scan that was running
    {
        if (Interlocked.Exchange(ref _initDone, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                var f = File.Exists(FilePath) ? JsonSerializer.Deserialize<ScanFile>(File.ReadAllText(FilePath)) : null;
                if (f != null && f.Running && CoinSwitchClient.HasEnvKeys) { StartInternal(f.Req, f); Console.WriteLine("Scanner: resumed."); }
                else if (f != null) { lock (_l) { _req = f.Req; _signals.AddRange(f.Signals); _started = f.Started; } }
            }
            catch (Exception e) { Console.WriteLine("Scanner: could not resume: " + e.Message); }
            while (true) { await Task.Delay(60000); try { if (_running) Save(); } catch { } }
        });
    }

    public void Begin(ScanReq r)
    {
        if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Connect your CoinSwitch API keys first. They are needed to download candles.");
        StartInternal(r, null);
    }

    private void StartInternal(ScanReq r, ScanFile? restore)
    {
        var cells = BuildCells(r);
        Stop(false);
        var m = Api.ParseMarket(r.Market);
        var cts = new CancellationTokenSource();
        lock (_l)
        {
            _cells = cells; _req = r; _cts = cts; _running = true; _started = restore?.Started ?? DateTime.UtcNow;
            _signals.Clear(); if (restore != null) _signals.AddRange(restore.Signals);
        }
        _groups.Clear();
        var saved = restore?.Cells.ToDictionary(c => c.Id) ?? new Dictionary<string, CellState>();
        foreach (var g in cells.GroupBy(c => (c.Symbol, c.Tf)))
        {
            var list = g.ToList(); var (sym, tf) = g.Key;
            _ = Task.Run(() => RunGroup(list, m, sym, tf, saved, cts.Token));
        }
        Save();
    }

    public void Stop(bool save = true)
    {
        lock (_l) { _cts?.Cancel(); _cts = null; _running = false; }
        if (save) Save();
    }

    public void Clear()
    {
        Stop(false);
        lock (_l) { _cells = new(); _signals.Clear(); _req = null; }
        _groups.Clear();
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    private void AddSignal(ScanSignal s) { lock (_l) { _signals.Add(s); if (_signals.Count > 300) _signals.RemoveAt(0); } }

    private async Task RunGroup(List<ScanCell> cells, Market m, string symbol, int tf, Dictionary<string, CellState> saved, CancellationToken ct)
    {
        string key = $"{symbol}|{tf}", ex = cells[0].Data.Exchange;
        var step = TimeSpan.FromMinutes(tf);
        try
        {
            _groups[key] = "loading history";
            int need = Math.Min(1500, cells.Max(c => { try { return c.Factory().WarmupBars; } catch { return 200; } }) + 400);
            var hist = (await AgentCandles.GetAsync(m, ex, symbol, tf, need, ct)).Where(c => c.Time + step <= DateTime.UtcNow).OrderBy(c => c.Time).ToList();
            await Task.Run(() => Parallel.ForEach(cells, c =>
            {
                try
                {
                    var bt = Backtester.Run(c.Factory(), hist, c.Cost, c.Bpy, c.Capital, c.Band);          // same costs, same candles, a fresh strategy: the "what would it have done" column
                    c.BtReturn = bt.TotalReturnPct; c.BtSharpe = bt.Sharpe; c.BtMaxDd = bt.MaxDrawdownPct; c.BtTrades = bt.Trades; c.BtBars = hist.Count; c.BtDone = true;
                }
                catch (Exception) { /* a failed backtest must not stop the live forward test */ }
                try { c.Warm(hist); if (saved.TryGetValue(c.Id, out var st)) c.Restore(st); }
                catch (Exception e) { c.Problem = e.Message; c.Ready = false; }
            }), ct);
            long last = hist.Count > 0 ? new DateTimeOffset(DateTime.SpecifyKind(hist[^1].Time, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
            _groups[key] = "running";
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                List<Candle> recent;
                try
                {
                    recent = (await AgentCandles.GetAsync(m, ex, symbol, tf, 8, ct))
                        .Where(c => c.Time + step <= DateTime.UtcNow && new DateTimeOffset(DateTime.SpecifyKind(c.Time, DateTimeKind.Utc)).ToUnixTimeSeconds() > last)
                        .OrderBy(c => c.Time).ToList();
                    _groups[key] = "running";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _groups[key] = "retrying: " + e.Message; continue; }
                foreach (var cd in recent)
                {
                    var sigs = new ConcurrentBag<ScanSignal>();
                    Parallel.ForEach(cells, c => { var s = c.Step(cd); if (s != null) sigs.Add(s); });
                    foreach (var s in sigs.OrderBy(x => x.Name)) AddSignal(s);
                    last = new DateTimeOffset(DateTime.SpecifyKind(cd.Time, DateTimeKind.Utc)).ToUnixTimeSeconds();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _groups[key] = "error: " + e.Message; foreach (var c in cells) c.Problem = e.Message; }
    }

    private void Save()
    {
        try
        {
            ScanReq? r; List<ScanCell> cells; List<ScanSignal> sigs; DateTime started; bool running;
            lock (_l) { r = _req; cells = _cells.ToList(); sigs = _signals.ToList(); started = _started; running = _running; }
            if (r == null) return;
            var f = new ScanFile(r, running, started, cells.Where(c => c.Ready).Select(c => c.Export()).ToList(), sigs);
            lock (_saveLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(f));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception e) { Console.WriteLine("Scanner: could not save: " + e.Message); }
    }

    // ---------------- what the page shows ----------------

    private static double Fin(double x) => double.IsFinite(x) ? Math.Round(x, 2) : 0;

    public object Snapshot()
    {
        List<ScanCell> cells; List<ScanSignal> sigs; ScanReq? r; bool running; DateTime started;
        lock (_l) { cells = _cells.ToList(); sigs = _signals.ToList(); r = _req; running = _running; started = _started; }
        var rows = cells.Select(c =>
        {
            var (_, cl, w) = AgentStats.Of(c.FillsCopy());
            double eq = c.Pf.Wealth(c.LastClose > 0 ? c.LastClose : 1);
            return new
            {
                id = c.Id,
                name = c.Name,
                symbol = c.Symbol,
                tf = c.Tf,
                @params = c.ParamText,
                capital = c.Capital,
                equity = eq,
                pnlPct = c.Capital > 0 ? (eq / c.Capital - 1) * 100 : 0,
                trades = c.Pf.Trades,
                closed = cl,
                winRate = cl > 0 ? w * 100.0 / cl : (double?)null,
                maxDd = c.MaxDd * 100,
                stance = c.Stance,
                armed = c.Armed,
                ready = c.Ready,
                bars = c.Bars,
                problem = c.Problem,
                lastSignalAt = c.LastSignalAt,
                lastSignal = c.LastSignal,
                btDone = c.BtDone,
                btReturn = Fin(c.BtReturn),
                btSharpe = Fin(c.BtSharpe),
                btMaxDd = Fin(c.BtMaxDd),
                btTrades = c.BtTrades,
                btDays = Math.Round(c.BtBars * c.Tf / 1440.0, 1)
            };
        }).ToList();
        long day = UnixNow - 86400;
        return new
        {
            running,
            startedAt = new DateTimeOffset(DateTime.SpecifyKind(started == default ? DateTime.UtcNow : started, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            req = r,
            summary = new
            {
                cells = rows.Count,
                ready = rows.Count(x => x.ready),
                armed = rows.Count(x => x.armed && x.ready),
                inPosition = rows.Count(x => x.stance != "flat"),
                profitable = rows.Count(x => x.ready && x.pnlPct > 0),
                signals24h = sigs.Count(x => x.T >= day),
                maxCells = MaxCells
            },
            groups = _groups.Select(g => new { key = g.Key, status = g.Value }).OrderBy(g => g.key).ToList(),
            cells = rows,
            signals = sigs.OrderByDescending(x => x.T).Take(60).ToList()
        };
    }

    /// <summary>Turn a scanner cell into a visible paper agent (continuous tracking, and the starting point for "Go live").</summary>
    public string Promote(string cellId)
    {
        ScanCell? c; ScanReq? r;
        lock (_l) { c = _cells.FirstOrDefault(x => x.Id == cellId); r = _req; }
        if (c == null) throw new ArgumentException("Unknown scanner cell.");
        var a = _agents.Add(new AgentReq(null, c.Data, c.Strategy, r?.Costs, c.Capital));
        return a.Id;
    }
}

public static class ScannerApi
{
    private static IResult Fail(Exception e) => e is ArgumentException or InvalidOperationException
        ? Results.BadRequest(new { error = e.Message }) : Results.Json(new { error = e.Message }, statusCode: 500);

    public static void MapScanner(this WebApplication app)
    {
        app.MapGet("/api/scanner", (ScannerManager s) => Results.Ok(s.Snapshot()));
        app.MapPost("/api/scanner/preview", (ScanReq r, ScannerManager s) => { try { return Results.Ok(s.Preview(r)); } catch (Exception e) { return Fail(e); } });
        app.MapPost("/api/scanner/start", (ScanReq r, ScannerManager s) => { try { s.Begin(r); return Results.Ok(new { ok = true }); } catch (Exception e) { return Fail(e); } });
        app.MapPost("/api/scanner/stop", (ScannerManager s) => { s.Stop(); return Results.Ok(new { ok = true }); });
        app.MapDelete("/api/scanner", (ScannerManager s) => { s.Clear(); return Results.Ok(new { ok = true }); });
        app.MapPost("/api/scanner/{id}/promote", (string id, ScannerManager s) =>
        {
            try { return Results.Ok(new { agentId = s.Promote(id) }); } catch (Exception e) { return Fail(e); }
        });
    }
}