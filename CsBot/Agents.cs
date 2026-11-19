using System.Collections.Concurrent;
using System.Text.Json;
using CsBot.Scripting;

namespace CsBot;

// ============================================================================================
// Multi-agent engine: several strategies ("agents") run at the same time, each as its own PAPER
// trader with its own simulated account. Real-money trading stays on the single-trader path.
// ============================================================================================

public sealed record AgentReq(string? Name, MarketRef Data, StrategyIn Strategy, CostIn? Costs, double Capital);
public sealed record PresetReq(MarketRef Data, CostIn? Costs, double Capital, List<string>? Picks, bool? Replace);

internal sealed record AgentFile(string Id, string Name, string Color, string ReqJson, ExecutorState Exec, bool WasRunning, DateTime Created, double[][] Curve);
internal sealed record AgentsFile(int Version, List<AgentFile> Agents, double[][] Combined, DateTime SessionStart);

public sealed class Agent
{
    public string Id = "", Name = "", Color = "", Role = "", Quote = "", ReqJson = "";
    public TraderReq Req = null!;
    public Trader T = null!;
    public DateTime Created = DateTime.UtcNow;
    public bool Wanted = true;                                   // should be running (survives a restart)
    public readonly List<double[]> Curve = new();                // [unixSeconds, equity]
}

/// <summary>Candles shared by all agents: one download per symbol/timeframe instead of one per agent (futures klines are limited to 30 requests/min).</summary>
internal static class AgentCandles
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, int N, List<Candle> C)> Cache = new();

    public static async Task<List<Candle>> GetAsync(Market m, string exchange, string symbol, int intervalMin, int n, CancellationToken ct)
    {
        string key = $"{m}|{exchange}|{symbol}|{intervalMin}";
        if (Cache.TryGetValue(key, out var h) && h.N >= n && DateTime.UtcNow - h.At < TimeSpan.FromSeconds(20)) return h.C.TakeLast(n).ToList();
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out h) && h.N >= n && DateTime.UtcNow - h.At < TimeSpan.FromSeconds(20)) return h.C.TakeLast(n).ToList();
            int want = Math.Max(n, h.N);
            int days = (int)Math.Ceiling((want + 2) * intervalMin / 1440.0) + 3;
            var c = await CandleStore.GetAsync(m, exchange, symbol, intervalMin, days, false, null, ct);   // disk cache, downloads only what is missing
            Cache[key] = (DateTime.UtcNow, want, c);
            return c.TakeLast(n).ToList();
        }
        finally { gate.Release(); }
    }
}

/// <summary>Latest price shared by all agents on a symbol (one ticker call every few seconds, whatever the number of agents).</summary>
internal static class AgentPrices
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, double Px)> Cache = new();

    public static async Task<double> GetAsync(Market m, string exchange, string symbol, CancellationToken ct)
    {
        string key = $"{m}|{exchange}|{symbol}";
        if (Cache.TryGetValue(key, out var h) && DateTime.UtcNow - h.At < TimeSpan.FromSeconds(3)) return h.Px;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out h) && DateTime.UtcNow - h.At < TimeSpan.FromSeconds(3)) return h.Px;
            var cs = CoinSwitchClient.Shared;
            var t = m == Market.Spot ? LiveFeed.ParseSpot(await cs.SpotTickerAsync(exchange, symbol, ct)) : LiveFeed.ParseFutures(await cs.FuturesTickerAsync(symbol, ct));
            if (t.Price <= 0) throw new InvalidOperationException("The exchange returned no price for " + symbol);
            Cache[key] = (DateTime.UtcNow, t.Price);
            return t.Price;
        }
        finally { gate.Release(); }
    }
}

internal static class AgentStats
{
    /// <summary>Closed trades from the fill list. A trade closes when the position returns to zero (or flips). Profit includes fees; TDS is refundable and ignored.</summary>
    public static (double Realized, int Closed, int Wins) Of(IReadOnlyList<Fill> fills)
    {
        double units = 0, avg = 0, trade = 0, realized = 0; int closed = 0, wins = 0;
        foreach (var f in fills)
        {
            double dq = f.Side == "buy" ? f.Qty : -f.Qty;
            trade -= f.Fee;
            if (units == 0 || Math.Sign(dq) == Math.Sign(units))
            {
                avg = (avg * Math.Abs(units) + f.Price * f.Qty) / (Math.Abs(units) + f.Qty);
                units += dq;
                continue;
            }
            double closeQty = Math.Min(f.Qty, Math.Abs(units));
            trade += closeQty * (f.Price - avg) * Math.Sign(units);
            units += dq;
            if (Math.Abs(units) < 1e-12) { units = 0; avg = 0; closed++; if (trade > 0) wins++; realized += trade; trade = 0; }
            else if (Math.Sign(dq) == Math.Sign(units)) { closed++; if (trade > 0) wins++; realized += trade; trade = 0; avg = f.Price; }   // flipped through zero
        }
        return (realized, closed, wins);
    }
}

public sealed class AgentManager
{
    public const int Max = 8;
    private static readonly string[] Names = { "NOVA", "ORIN", "VESK", "LUMA", "KIRA", "DASH", "ECHO", "RUNE" };
    private static readonly string[] Colors = { "#1fa38a", "#2b9cd8", "#6a4de0", "#d6336c", "#d9a300", "#7a9a1f", "#e8590c", "#0b7285" };
    public static readonly string[] DefaultTeam = { "ema_trend", "macd_trend", "supertrend", "donchian", "rsi_pullback", "bollinger_revert" };

    private readonly List<Agent> _a = new();
    private readonly object _l = new(), _saveLock = new();
    private readonly List<double[]> _combined = new();           // [unixSeconds, combined P&L, average conviction 0..1]
    private DateTime _session = DateTime.UtcNow;
    private int _started;
    private static string FilePath => System.IO.Path.Combine("data", "state", "agents.json");

    private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static string QuoteOf(MarketRef d) =>
        d.Market.Equals("futures", StringComparison.OrdinalIgnoreCase) ? "USDT" : (d.Symbol.Split('/').LastOrDefault() ?? "INR").ToUpperInvariant();

    private static Trader Build(TraderReq r, ExecutorState? restore)
    {
        var m = Api.ParseMarket(r.Data.Market);
        var cost = Api.BuildCost(m, r.Costs);
        double bpy = 365.25 * 1440 / r.Data.IntervalMin;
        var (factory, band) = StrategyRegistry.Create(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, m, bpy, cost.MaxLeverage, r.Strategy.Code);   // throws ArgumentException on a bad strategy
        var cfg = new TraderConfig
        {
            Mode = "paper",
            Market = m,
            Exchange = r.Data.Exchange,
            Symbol = r.Data.Symbol,
            IntervalMin = r.Data.IntervalMin,
            StrategyId = r.Strategy.Id,
            Params = r.Strategy.Params,
            Rules = r.Strategy.Rules,
            Code = r.Strategy.Code,
            Cost = cost,
            Capital = r.Capital
        };
        return new Trader(cfg, (n, ct) => AgentCandles.GetAsync(m, cfg.Exchange, cfg.Symbol, cfg.IntervalMin, n, ct), null, factory, band,
            ct => AgentPrices.GetAsync(m, cfg.Exchange, cfg.Symbol, ct), "", restore);   // empty request json: the single-trader session files are NOT touched
    }

    // ---------------- lifecycle ----------------

    /// <summary>Call once at startup: restores saved agents and starts the sampler that records the equity curves.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            try { ResumeAll(); } catch (Exception e) { Console.WriteLine("Agents: could not restore saved agents: " + e.Message); }
            int n = 0;
            while (true)
            {
                await Task.Delay(15000);
                try { Sample(); if (++n % 2 == 0) Save(); } catch (Exception e) { Console.WriteLine("Agents: sampler error: " + e.Message); }
            }
        });
    }

    private void ResumeAll()
    {
        AgentsFile? f = null;
        try { f = File.Exists(FilePath) ? JsonSerializer.Deserialize<AgentsFile>(File.ReadAllText(FilePath)) : null; } catch { }
        if (f == null) return;
        _session = f.SessionStart == default ? DateTime.UtcNow : f.SessionStart;
        lock (_combined) _combined.AddRange(f.Combined ?? Array.Empty<double[]>());
        foreach (var af in f.Agents)
        {
            try
            {
                var req = JsonSerializer.Deserialize<TraderReq>(af.ReqJson)!;
                var a = new Agent { Id = af.Id, Name = af.Name, Color = af.Color, Req = req, ReqJson = af.ReqJson, Quote = QuoteOf(req.Data), Created = af.Created, Wanted = af.WasRunning };
                a.Role = StrategyInfo.Describe(req.Strategy.Id, req.Strategy.Params, req.Strategy.Code).Name;
                a.Curve.AddRange(af.Curve ?? Array.Empty<double[]>());
                a.T = Build(req, af.Exec);
                lock (_l) { if (_a.Count >= Max) break; _a.Add(a); }
                if (af.WasRunning && CoinSwitchClient.HasEnvKeys) a.T.Start();
            }
            catch (Exception e) { Console.WriteLine($"Agents: could not restore '{af.Name}': {e.Message}"); }
        }
        Console.WriteLine($"Agents: restored {_a.Count} agent(s).");
    }

    private void Save()
    {
        try
        {
            List<Agent> list; lock (_l) list = _a.ToList();
            double[][] comb; lock (_combined) comb = _combined.ToArray();
            var file = new AgentsFile(1, list.Select(a =>
            {
                double[][] c; lock (a.Curve) c = a.Curve.ToArray();
                return new AgentFile(a.Id, a.Name, a.Color, a.ReqJson, a.T.ExportState(), a.Wanted, a.Created, c);
            }).ToList(), comb, _session);
            lock (_saveLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception e) { Console.WriteLine("Agents: could not save: " + e.Message); }
    }

    private static void Push(List<double[]> l, double[] p)
    {
        lock (l)
        {
            l.Add(p);
            if (l.Count > 600) for (int i = l.Count - 2; i >= 1; i -= 2) l.RemoveAt(i);   // keep the whole history, but thinner
        }
    }

    private static double LastEq(Agent a) { lock (a.Curve) return a.Curve.Count > 0 ? a.Curve[^1][1] : a.Req.Capital; }
    private static double EquityOf(Agent a) { double e = a.T.Equity(); return double.IsFinite(e) ? e : LastEq(a); }

    private void Sample()
    {
        List<Agent> list; lock (_l) list = _a.ToList();
        if (list.Count == 0) return;
        long now = UnixNow; double pnl = 0, conv = 0; int run = 0;
        foreach (var a in list)
        {
            double eq = EquityOf(a);
            Push(a.Curve, new[] { (double)now, Math.Round(eq, 2) });
            pnl += eq - a.Req.Capital;
            if (a.T.IsRunning) { conv += Math.Abs(a.T.TargetWeight); run++; }
        }
        Push(_combined, new[] { (double)now, Math.Round(pnl, 2), run > 0 ? Math.Round(Math.Min(1, conv / run), 4) : 0 });
    }

    // ---------------- commands ----------------

    private Agent? Find(string id) { lock (_l) return _a.FirstOrDefault(x => x.Id == id); }

    public Agent Add(AgentReq r)
    {
        if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Connect your CoinSwitch API keys first (menu, top right). They are needed to download candles and prices.");
        if (r.Capital <= 0) throw new ArgumentException("Capital must be above zero.");
        var req = new TraderReq("paper", r.Data, r.Strategy, r.Costs, r.Capital, null, null, null, null, null);
        string quote = QuoteOf(r.Data);
        Agent a;
        lock (_l)
        {
            if (_a.Count >= Max) throw new InvalidOperationException($"At most {Max} agents at once. Remove one first.");
            var other = _a.FirstOrDefault();
            if (other != null && other.Quote != quote)
                throw new InvalidOperationException($"All agents must use the same quote currency so the combined balance makes sense. Your current agents use {other.Quote}; this one uses {quote}.");
            int idx = Enumerable.Range(0, Max).First(i => !_a.Any(x => x.Color == Colors[i]));
            string nm = string.IsNullOrWhiteSpace(r.Name) ? Names[idx] : r.Name.Trim();
            a = new Agent
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = nm.Length > 12 ? nm[..12] : nm,
                Color = Colors[idx],
                Quote = quote,
                Req = req,
                ReqJson = JsonSerializer.Serialize(req),
                Role = StrategyInfo.Describe(r.Strategy.Id, r.Strategy.Params, r.Strategy.Code).Name
            };
            a.T = Build(req, null);
            _a.Add(a);
        }
        Push(a.Curve, new[] { (double)UnixNow, r.Capital });
        a.T.Start();
        Start(); Save();
        return a;
    }

    public List<Agent> AddPreset(PresetReq r)
    {
        var picks = (r.Picks is { Count: > 0 } ? r.Picks : DefaultTeam.ToList()).Distinct().ToList();
        if (r.Replace == true) foreach (var id in AllIds()) RemoveAsync(id).GetAwaiter().GetResult();
        int have; lock (_l) have = _a.Count;
        if (have + picks.Count > Max) throw new InvalidOperationException($"{picks.Count} more agents would pass the limit of {Max}. Remove some first, or pick fewer.");
        var made = new List<Agent>();
        foreach (var id in picks)
        {
            var tpl = Templates.All.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException("Unknown ready-made script: " + id);
            made.Add(Add(new AgentReq(null, r.Data, new StrategyIn("script", new Dictionary<string, JsonElement>(), null, tpl.Code), r.Costs, r.Capital)));
        }
        return made;
    }

    private List<string> AllIds() { lock (_l) return _a.Select(x => x.Id).ToList(); }

    public async Task<bool> StopAsync(string id)
    {
        var a = Find(id); if (a == null) return false;
        a.Wanted = false;
        try { await a.T.StopAsync(); } catch { }
        Save(); return true;
    }

    public async Task StopAllAsync() { foreach (var id in AllIds()) await StopAsync(id); }

    public bool Resume(string id)
    {
        var a = Find(id); if (a == null) return false;
        if (a.T.IsRunning) return true;
        if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Connect your CoinSwitch API keys first.");
        var state = a.T.ExportState();                // a stopped trader cannot restart: build a new one that carries the same account
        a.T = Build(a.Req, state); a.Wanted = true; a.T.Start();
        Save(); return true;
    }

    public async Task<bool> CloseAsync(string id)
    {
        var a = Find(id); if (a == null) return false;
        if (a.T.LastPrice <= 0) throw new InvalidOperationException("No price yet: wait a few seconds for the agent to start.");
        await a.T.CloseAsync();
        Save(); return true;
    }

    public async Task<bool> RemoveAsync(string id)
    {
        Agent? a; lock (_l) { a = _a.FirstOrDefault(x => x.Id == id); if (a != null) _a.Remove(a); }
        if (a == null) return false;
        try { await a.T.StopAsync(); } catch { }
        Save(); return true;
    }

    // ---------------- what the page shows ----------------

    private sealed record Calc(Agent A, double Equity, double Realized, int Closed, int Wins, Fill[] Fills);

    private static Calc Compute(Agent a)
    {
        var fills = a.T.FillsCopy();
        var (re, cl, w) = AgentStats.Of(fills);
        return new Calc(a, EquityOf(a), re, cl, w, fills);
    }

    public object Snapshot()
    {
        List<Agent> list; lock (_l) list = _a.ToList();
        var calcs = list.Select(Compute).ToList();
        double[][] comb; lock (_combined) comb = _combined.ToArray();

        var rows = calcs.Select(c =>
        {
            var a = c.A; var t = a.T; bool run = t.IsRunning;
            double[][] curve; lock (a.Curve) curve = a.Curve.ToArray();
            return new
            {
                id = a.Id,
                name = a.Name,
                color = a.Color,
                role = a.Role,
                data = a.Req.Data,
                capital = a.Req.Capital,
                strategy = a.Req.Strategy,
                costs = a.Req.Costs,
                state = run ? t.State : "stopped",
                running = run,
                price = t.LastPrice,
                target = t.TargetWeight,
                account = t.Account(),
                equity = c.Equity,
                pnl = c.Equity - a.Req.Capital,
                realized = c.Realized,
                closed = c.Closed,
                wins = c.Wins,
                winRate = c.Closed > 0 ? c.Wins * 100.0 / c.Closed : (double?)null,
                signals = t.Signals,
                error = t.LastError,
                note = t.LastLog(),
                curve
            };
        }).ToList();

        var fills = calcs.SelectMany(c => c.Fills.TakeLast(25).Select(f => new
        {
            t = Unix(f.Time),
            agent = c.A.Id,
            name = c.A.Name,
            color = c.A.Color,
            symbol = c.A.Req.Data.Symbol,
            side = f.Side,
            price = f.Price,
            qty = f.Qty,
            fee = f.Fee
        })).OrderByDescending(x => x.t).Take(60).ToList();

        double cap = calcs.Sum(c => c.A.Req.Capital), eq = calcs.Sum(c => c.Equity);
        int closed = calcs.Sum(c => c.Closed), wins = calcs.Sum(c => c.Wins);
        var running = list.Where(a => a.T.IsRunning).ToList();
        return new
        {
            quote = list.FirstOrDefault()?.Quote,
            sessionStart = Unix(_session),
            now = UnixNow,
            max = Max,
            agents = rows,
            fills,
            combined = new
            {
                capital = cap,
                equity = eq,
                pnl = eq - cap,
                pnlPct = cap > 0 ? (eq / cap - 1) * 100 : 0,
                closed,
                wins,
                winRate = closed > 0 ? wins * 100.0 / closed : (double?)null,
                signals = list.Sum(a => a.T.Signals),
                fillsTotal = calcs.Sum(c => c.Fills.Length),
                active = running.Count,
                total = list.Count,
                conviction = running.Count > 0 ? Math.Min(1, running.Average(a => Math.Abs(a.T.TargetWeight))) : 0,
                curve = comb.Select(p => new[] { p[0], p[1] }),
                convictionCurve = comb.Select(p => new[] { p[0], p.Length > 2 ? p[2] : 0 })
            }
        };
    }
}

public static class AgentsApi
{
    private static IResult Fail(Exception e) => e is ArgumentException or InvalidOperationException
        ? Results.BadRequest(new { error = e.Message }) : Results.Json(new { error = e.Message }, statusCode: 500);

    public static void MapAgents(this WebApplication app)
    {
        app.MapGet("/api/agents", (AgentManager m) => Results.Ok(m.Snapshot()));

        app.MapPost("/api/agents", (AgentReq r, AgentManager m) =>
        {
            try { var a = m.Add(r); return Results.Ok(new { id = a.Id }); } catch (Exception e) { return Fail(e); }
        });

        // a whole team at once, e.g. six ready-made scripts on the same market
        app.MapPost("/api/agents/preset", (PresetReq r, AgentManager m) =>
        {
            try { var l = m.AddPreset(r); return Results.Ok(new { ids = l.Select(a => a.Id).ToList() }); } catch (Exception e) { return Fail(e); }
        });

        app.MapPost("/api/agents/stopall", async (AgentManager m) => { await m.StopAllAsync(); return Results.Ok(new { ok = true }); });

        app.MapPost("/api/agents/{id}/stop", async (string id, AgentManager m) =>
        {
            if (await m.StopAsync(id)) return Results.Ok(new { ok = true });
            return Results.NotFound(new { error = "Unknown agent." });
        });
        app.MapPost("/api/agents/{id}/resume", (string id, AgentManager m) =>
        {
            try { if (m.Resume(id)) return Results.Ok(new { ok = true }); return Results.NotFound(new { error = "Unknown agent." }); } catch (Exception e) { return Fail(e); }
        });
        app.MapPost("/api/agents/{id}/close", async (string id, AgentManager m) =>
        {
            try { if (await m.CloseAsync(id)) return Results.Ok(new { ok = true }); return Results.NotFound(new { error = "Unknown agent." }); } catch (Exception e) { return Fail(e); }
        });
        app.MapDelete("/api/agents/{id}", async (string id, AgentManager m) =>
        {
            if (await m.RemoveAsync(id)) return Results.Ok(new { ok = true });
            return Results.NotFound(new { error = "Unknown agent." });
        });
    }
}