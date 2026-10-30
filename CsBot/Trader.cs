using System.Text.Json;

namespace CsBot;

public sealed record RiskLimits(double MaxOrderNotional, double MaxPositionNotional, double MaxLeverage, double DailyLossPct);

public sealed class TraderConfig
{
    public string Mode { get; init; } = "paper";            // paper | live
    public Market Market { get; init; }
    public string Exchange { get; init; } = "coinswitchx";
    public string Symbol { get; init; } = "BTC/INR";
    public int IntervalMin { get; init; } = 360;
    public string StrategyId { get; init; } = "trend_ema";
    public Dictionary<string, JsonElement>? Params { get; init; }
    public JsonElement? Rules { get; init; }
    public string? Code { get; init; }
    public CostConfig Cost { get; init; } = CostConfig.DefaultSpot;
    public double Capital { get; init; } = 100_000;          // quote currency allocated to the bot (INR for spot, USDT for futures)
    public RiskLimits Risk { get; init; } = new(0, 0, 1, 0);
    public int PricePrecision { get; init; } = 0;
    public int QtyPrecision { get; init; } = 5;
}

public static class GateStore
{
    private static readonly Dictionary<string, (bool Pass, DateTime At)> _m = new();
    public static string Key(string market, string symbol, int interval, string id, Dictionary<string, JsonElement>? p, JsonElement? rules, string? code = null) =>
        $"{market}|{symbol}|{interval}|{id}|" + string.Join(",", (p ?? new()).OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value.GetRawText()}")) + "|" + (rules?.GetRawText() ?? "") + "|" + (code ?? "");
    public static void Record(string key, bool pass) { lock (_m) _m[key] = (pass, DateTime.UtcNow); }
    public static bool PassedRecently(string key) { lock (_m) return _m.TryGetValue(key, out var v) && v.Pass && DateTime.UtcNow - v.At < TimeSpan.FromHours(24); }
}

public interface IExecutor
{
    double Units { get; }
    bool Halted { get; }
    string HaltReason { get; }
    Task<Fill?> RebalanceAsync(double target, double price, DateTime time, CancellationToken ct);
    void AccrueFunding(double price, double hours);
    double Equity(double price);
    IReadOnlyList<Fill> Fills { get; }
    object Snapshot(double price);
    ExecutorState Export();
    void Import(ExecutorState s);
    /// <summary>Drop the tracked position WITHOUT trading (you closed it yourself on the exchange). Returns false if there was none.</summary>
    bool ForgetPosition();
}

public sealed class PaperExecutor : IExecutor
{
    private readonly SimPortfolio _pf; private readonly double _start; private readonly List<Fill> _fills = new();
    public PaperExecutor(CostConfig cost, double capital, double band) { _pf = new SimPortfolio(cost, capital, band); _start = capital; }
    public double Units => _pf.Units;
    public bool Halted => false;
    public string HaltReason => "";
    public IReadOnlyList<Fill> Fills => _fills;
    public Task<Fill?> RebalanceAsync(double target, double price, DateTime time, CancellationToken ct)
    { var f = _pf.Rebalance(target, price, time); if (f != null) _fills.Add(f); return Task.FromResult(f); }
    public void AccrueFunding(double price, double hours) => _pf.AccrueFunding(price, hours);
    public double Equity(double price) => _pf.Wealth(price);
    public ExecutorState Export() => new(new Dictionary<string, double>
    { ["cash"] = _pf.Cash, ["units"] = _pf.Units, ["fees"] = _pf.Fees, ["tds"] = _pf.TdsWithheld, ["funding"] = _pf.Funding, ["traded"] = _pf.Traded,
      ["slip"] = _pf.SlipCost, ["entry"] = _pf.Entry, ["trades"] = _pf.Trades }, _fills.ToList());
    public void Import(ExecutorState s)
    {
        double G(string k) => s.Num.TryGetValue(k, out var v) ? v : 0;
        _pf.Cash = G("cash"); _pf.Units = G("units"); _pf.Fees = G("fees"); _pf.TdsWithheld = G("tds"); _pf.Funding = G("funding");
        _pf.Traded = G("traded"); _pf.SlipCost = G("slip"); _pf.Entry = G("entry"); _pf.Trades = (int)G("trades");
        _fills.Clear(); _fills.AddRange(s.Fills);
    }
    public bool ForgetPosition() => false;
    public object Snapshot(double price) => new { start = _start, equity = _pf.Wealth(price), liquid = _pf.Liquid(price), units = _pf.Units,
        fees = _pf.Fees, tdsWithheld = _pf.TdsWithheld, funding = _pf.Funding, trades = _pf.Trades,
        side = _pf.Units > 0 ? "long" : _pf.Units < 0 ? "short" : "flat", entryPrice = _pf.Entry, notional = Math.Abs(_pf.Units) * price,
        unrealizedPnl = _pf.Units == 0 ? 0 : _pf.Units * (price - _pf.Entry), unrealizedPct = _pf.Units == 0 || _pf.Entry == 0 ? 0 : Math.Sign(_pf.Units) * (price / _pf.Entry - 1) * 100,
        weight = _pf.Weight(price) };
}

/// <summary>
/// REAL orders. Safeguards: hard risk limits, daily-loss halt, fixed allocated capital (no compounding),
/// orders confirmed by polling the exchange; unfilled spot limit orders are cancelled.
/// Position is tracked from confirmed fills only. NOT validated against the live exchange by the author - start tiny.
/// </summary>
public sealed class LiveExecutor : IExecutor
{
    private readonly CoinSwitchClient _cs; private readonly TraderConfig _cfg; private readonly double _band; private readonly Action<string> _log;
    private readonly List<Fill> _fills = new();
    private double _avg, _realized, _fees; private int _orders;
    public double Units { get; private set; }
    public bool Halted { get; private set; }
    public string HaltReason { get; private set; } = "";
    public IReadOnlyList<Fill> Fills => _fills;

    public LiveExecutor(CoinSwitchClient cs, TraderConfig cfg, double band, Action<string> log) { _cs = cs; _cfg = cfg; _band = band; _log = log; }

    public async Task InitAsync(CancellationToken ct)
    {
        if (_cfg.Market == Market.Futures)
        {
            int lev = (int)Math.Max(1, Math.Floor(Math.Min(_cfg.Risk.MaxLeverage, _cfg.Cost.MaxLeverage)));
            await _cs.FuturesSetLeverageAsync(_cfg.Symbol, lev, ct);
            _log($"Futures leverage set to {lev}x on {_cfg.Symbol}");
        }
    }

    public ExecutorState Export() => new(new Dictionary<string, double> { ["units"] = Units, ["avg"] = _avg, ["realized"] = _realized, ["fees"] = _fees, ["orders"] = _orders }, _fills.ToList());
    public void Import(ExecutorState s)
    {
        double G(string k) => s.Num.TryGetValue(k, out var v) ? v : 0;
        Units = G("units"); _avg = G("avg"); _realized = G("realized"); _fees = G("fees"); _orders = (int)G("orders");
        _fills.Clear(); _fills.AddRange(s.Fills);
    }
    public bool ForgetPosition()
    {
        if (Units == 0) return false;
        _log($"Forgot tracked position {Units} @ {_avg} (you closed it yourself outside this app). No orders were sent.");
        Units = 0; _avg = 0; return true;
    }

    public void Halt(string reason) { if (!Halted) { Halted = true; HaltReason = reason; _log("HALTED: " + reason); } }
    public void AccrueFunding(double price, double hours) { }
    public double Equity(double price) => _cfg.Capital + _realized - _fees + Units * (price - _avg) * (Units == 0 ? 0 : 1);

    public static double RoundDown(double x, int dec) { double m = Math.Pow(10, dec); return Math.Sign(x) * Math.Floor(Math.Abs(x) * m + 1e-9) / m; }

    public async Task<Fill?> RebalanceAsync(double target, double price, DateTime time, CancellationToken ct)
    {
        if (Halted) return null;
        bool spot = _cfg.Market == Market.Spot;
        double maxLev = spot ? 1 : Math.Min(_cfg.Risk.MaxLeverage, _cfg.Cost.MaxLeverage);
        double w = Math.Clamp(target, spot ? 0 : -maxLev, maxLev);
        double curW = Units * price / _cfg.Capital;
        if ((w == 0 && Units == 0) || (w != 0 && Math.Sign(w) == Math.Sign(curW) && Math.Abs(w - curW) < _band)) return null;

        double targetUnits = RoundDown(w * _cfg.Capital / price, _cfg.QtyPrecision);
        if (Math.Abs(targetUnits * price) > _cfg.Risk.MaxPositionNotional) { _log($"BLOCKED: target position {Math.Abs(targetUnits * price):F0} exceeds max position {_cfg.Risk.MaxPositionNotional:F0}"); return null; }
        double delta = targetUnits - Units;
        if (Math.Abs(delta) < Math.Pow(10, -_cfg.QtyPrecision)) return null;

        Fill? last = null;
        if (spot) last = await SpotAsync(delta, price, time, ct);
        else
        {
            if (Units != 0 && Math.Sign(targetUnits) != Math.Sign(Units))
            {   // close first (reduce-only), then open the other side
                last = await FuturesAsync(-Units, price, time, true, ct) ?? last;
                if (Halted) return last;
                delta = targetUnits - Units;
            }
            if (Math.Abs(delta) >= Math.Pow(10, -_cfg.QtyPrecision))
                last = await FuturesAsync(delta, price, time, Units != 0 && Math.Sign(delta) != Math.Sign(Units), ct) ?? last;
        }
        return last;
    }

    private static bool Done(string s) => s.ToUpperInvariant() is "EXECUTED" or "CANCELLED" or "EXPIRED" or "DISCARDED" or "REJECTED" or "PARTIALLY_EXECUTED" or "FILLED";

    private async Task<Fill?> SpotAsync(double delta, double price, DateTime time, CancellationToken ct)
    {
        string side = delta > 0 ? "buy" : "sell";
        double qty = RoundDown(Math.Abs(delta), _cfg.QtyPrecision);
        if (side == "sell") qty = Math.Min(qty, RoundDown(Units, _cfg.QtyPrecision));
        if (qty <= 0) return null;
        double px = Math.Round(price * (side == "buy" ? 1.003 : 0.997), _cfg.PricePrecision);   // marketable limit (spot supports LIMIT only)
        if (side == "buy" && qty * px > _cfg.Risk.MaxOrderNotional) { _log($"BLOCKED: buy order {qty * px:F0} exceeds max order size {_cfg.Risk.MaxOrderNotional:F0}"); return null; }
        _log($"SPOT {side.ToUpper()} {qty} {_cfg.Symbol} @ limit {px}");
        var o = await _cs.SpotPlaceLimitAsync(_cfg.Exchange, _cfg.Symbol, side, px, qty, Guid.NewGuid().ToString(), ct);
        string id = CoinSwitchClient.Str(o, "order_id"); _orders++;
        if (id == "") { Halt("Order response had no order_id: " + o.GetRawText()); return null; }
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(3000, ct);
            o = await _cs.SpotGetOrderAsync(id, ct);
            if (Done(CoinSwitchClient.Str(o, "status"))) break;
        }
        if (!Done(CoinSwitchClient.Str(o, "status")))
        {
            try { await _cs.SpotCancelAsync(id, ct); } catch (Exception e) { _log("Cancel failed: " + e.Message); }
            await Task.Delay(2000, ct);
            o = await _cs.SpotGetOrderAsync(id, ct);
        }
        double ex = CoinSwitchClient.Num(o, "executed_qty"), avg = CoinSwitchClient.Num(o, "average_price");
        _log($"Order {id}: status {CoinSwitchClient.Str(o, "status")}, filled {ex} @ {avg}");
        if (ex <= 0) return null;
        return Apply(side, ex, avg > 0 ? avg : px, ex * (avg > 0 ? avg : px) * _cfg.Cost.FeeRate, time);
    }

    private async Task<Fill?> FuturesAsync(double delta, double price, DateTime time, bool reduceOnly, CancellationToken ct)
    {
        string side = delta > 0 ? "BUY" : "SELL";
        double qty = RoundDown(Math.Abs(delta), _cfg.QtyPrecision);
        if (qty <= 0) return null;
        if (!reduceOnly && qty * price > _cfg.Risk.MaxOrderNotional) { _log($"BLOCKED: order {qty * price:F0} exceeds max order size {_cfg.Risk.MaxOrderNotional:F0}"); return null; }
        _log($"FUTURES {side} {qty} {_cfg.Symbol} MARKET reduceOnly={reduceOnly}");
        var o = await _cs.FuturesPlaceAsync(_cfg.Symbol, side, "MARKET", qty, null, reduceOnly, Guid.NewGuid().ToString(), ct);
        string id = CoinSwitchClient.Str(o, "order_id"); _orders++;
        if (id == "") { Halt("Order response had no order_id: " + o.GetRawText()); return null; }
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(2000, ct);
            o = await _cs.FuturesGetOrderAsync(id, ct);
            if (Done(CoinSwitchClient.Str(o, "status"))) break;
        }
        double ex = CoinSwitchClient.Num(o, "exec_quantity"), avg = CoinSwitchClient.Num(o, "avg_execution_price"), fee = CoinSwitchClient.Num(o, "execution_fee");
        _log($"Order {id}: status {CoinSwitchClient.Str(o, "status")}, filled {ex} @ {avg}, fee {fee}");
        if (ex <= 0) { Halt($"Market order {id} shows no fill. Check the CoinSwitch app before restarting."); return null; }
        return Apply(side.ToLowerInvariant(), ex, avg > 0 ? avg : price, fee, time);
    }

    private Fill Apply(string side, double qty, double px, double fee, DateTime time)
    {
        double dq = side == "buy" ? qty : -qty;
        if (Units == 0 || Math.Sign(dq) == Math.Sign(Units)) { _avg = (_avg * Math.Abs(Units) + px * qty) / (Math.Abs(Units) + qty); Units += dq; }
        else
        {
            double closeQty = Math.Min(qty, Math.Abs(Units));
            _realized += closeQty * (px - _avg) * Math.Sign(Units);
            Units += dq;
            if (Math.Abs(Units) < 1e-12) { Units = 0; _avg = 0; }
            else if (Math.Sign(dq) == Math.Sign(Units)) _avg = px;   // flipped through zero
        }
        _fees += fee;
        var f = new Fill(time, side, px, qty, fee, 0); _fills.Add(f);
        return f;
    }

    public object Snapshot(double price) => new { start = _cfg.Capital, equity = Equity(price), units = Units, avgEntry = _avg, realized = _realized,
        fees = _fees, trades = _orders, halted = Halted, haltReason = HaltReason,
        side = Units > 0 ? "long" : Units < 0 ? "short" : "flat", entryPrice = _avg, notional = Math.Abs(Units) * price,
        unrealizedPnl = Units == 0 ? 0 : Units * (price - _avg), unrealizedPct = Units == 0 || _avg == 0 ? 0 : Math.Sign(Units) * (price / _avg - 1) * 100,
        weight = _cfg.Capital > 0 ? Units * price / _cfg.Capital : 0 };
}

public sealed class Trader
{
    private readonly TraderConfig _cfg; private IStrategy _strat; private readonly Func<IStrategy> _stratFactory; private readonly double _band; private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly Func<int, CancellationToken, Task<List<Candle>>> _fetch; private readonly IExecutor _ex;
    private readonly CancellationTokenSource _cts = new(); private Task? _task; private readonly List<string> _log = new();
    private double _lastPrice, _mark, _target, _dayStart; private DateTime _dayStartDate, _lastBar, _markAt; private string _state = "starting", _markErr = "";
    private readonly Func<CancellationToken, Task<double>> _price;
    /// <summary>How often the live price is refreshed (the strategy itself still decides only when a candle closes).</summary>
    public int TickSeconds { get; set; } = 5;
    private double Mark => _mark > 0 ? _mark : _lastPrice;
    private DateTime _lastCheck; private string _lastError = "";
    /// <summary>Scales the wait between retries (tests use a small value).</summary>
    public double BackoffScale { get; set; } = 1;
    private TimeSpan Backoff(int n) => TimeSpan.FromSeconds(Math.Min(60, 5.0 * Math.Max(1, n)) * BackoffScale);
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsRunning => _task is { IsCompleted: false };
    public TraderConfig Config => _cfg;
    /// <summary>Resources (API client) kept alive until the trader is replaced, so the kill switch can still flatten.</summary>
    public IDisposable? Owned { get; set; }

    private object? _details;
    /// <summary>Which strategy is running and with which settings (cached).</summary>
    private object Details => _details ??= new
    {
        strategy = StrategyInfo.Describe(_cfg.StrategyId, _cfg.Params, _cfg.Code, _band),
        mode = _cfg.Mode, market = _cfg.Market.ToString().ToLower(), exchange = _cfg.Exchange, symbol = _cfg.Symbol, intervalMin = _cfg.IntervalMin,
        capital = _cfg.Capital, rebalanceBand = _band, costs = StrategyInfo.Costs(_cfg.Cost), startedAt = new DateTimeOffset(_startedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
        risk = _cfg.Mode == "live" ? new { _cfg.Risk.MaxOrderNotional, _cfg.Risk.MaxPositionNotional, _cfg.Risk.MaxLeverage, _cfg.Risk.DailyLossPct, _cfg.PricePrecision, _cfg.QtyPrecision } : null
    };

    private readonly string _reqJson; private long? _posOpenedAt; private string _lastSaved = "";
    private readonly object _persistLock = new(); private readonly SemaphoreSlim _execLock = new(1, 1);

    public Trader(TraderConfig cfg, Func<int, CancellationToken, Task<List<Candle>>> fetch, CoinSwitchClient? cs, Func<IStrategy> factory, double band,
        Func<CancellationToken, Task<double>>? priceFeed = null, string requestJson = "", ExecutorState? restore = null, long? posOpenedAt = null)
    {
        _reqJson = requestJson; _posOpenedAt = posOpenedAt;
        _cfg = cfg; _fetch = fetch; _band = band; _stratFactory = factory; _strat = factory();
        _price = priceFeed ?? (async ct => (await LiveFeed.GetAsync(cfg.Market, cfg.Exchange, cfg.Symbol, cfg.IntervalMin, ct)).Price);
        _ex = cfg.Mode == "live" ? new LiveExecutor(cs ?? throw new ArgumentNullException(nameof(cs)), cfg, band, Log) : new PaperExecutor(cfg.Cost, cfg.Capital, band);
        if (restore != null) { _ex.Import(restore); Log($"Resumed from saved state: position {_ex.Units:0.######}"); }
    }

    public void Log(string m) { lock (_log) { _log.Add($"{DateTime.UtcNow:HH:mm:ss} {m}"); if (_log.Count > 300) _log.RemoveAt(0); } }
    public void Start() => _task = Task.Run(() => SuperviseAsync(_cts.Token));

    /// <summary>
    /// A PAPER trader that dies from any unexpected error is restarted after 30 seconds (it rebuilds its indicators from history and keeps its position).
    /// A LIVE trader is never restarted automatically: after an error you decide.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (true)
        {
            await LoopAsync(ct);
            if (ct.IsCancellationRequested || _cfg.Mode != "paper" || _state != "error") return;
            Log("Restarting automatically in 30 seconds...");
            try { await Task.Delay(TimeSpan.FromSeconds(30 * BackoffScale), ct); } catch (OperationCanceledException) { return; }
            _state = "starting";
        }
    }
    /// <summary>Stop the loop but keep any open position on record (so it is picked up again at the next start). With no position, the saved state is deleted.</summary>
    public async Task StopAsync()
    {
        _cts.Cancel(); if (_task != null) { try { await _task; } catch { } }
        _state = "stopped"; Persist(false);
    }

    /// <summary>Kill switch: stop the loop and close the tracked position. When flat, the saved position/session files are deleted.</summary>
    public async Task KillAsync()
    {
        _cts.Cancel();
        if (_task != null) { try { await _task; } catch { } }
        _state = "killed";
        await _execLock.WaitAsync();
        try { var f = await _ex.RebalanceAsync(0, Mark, DateTime.UtcNow, CancellationToken.None); Log(f != null ? "Position closed." : "No position to close."); }
        catch (Exception e) { Log("Closing failed - CLOSE MANUALLY in the CoinSwitch app: " + e.Message); }
        finally { _execLock.Release(); }
        Persist(false);
    }

    /// <summary>Close the position now but keep the trader running (it may open a new one at its next signal).</summary>
    public async Task CloseAsync()
    {
        await _execLock.WaitAsync();
        try
        {
            _target = 0;
            var f = await _ex.RebalanceAsync(0, Mark, DateTime.UtcNow, CancellationToken.None);
            Log(f != null ? $"Manual close: {f.Side} {f.Qty:0.#####} @ {f.Price:0.##}" : "No position to close.");
        }
        finally { _execLock.Release(); }
        Persist(IsRunning);
    }

    /// <summary>Live only: you closed the position yourself on the exchange; stop tracking it (sends no orders).</summary>
    public void ForgetPosition() { if (_ex.ForgetPosition()) { _target = 0; Persist(IsRunning); } }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Writes data/state/trader-session.json and (only while a position is open) data/state/open-position.json.</summary>
    private void Persist(bool wasRunning)
    {
        if (string.IsNullOrEmpty(_reqJson)) return;
        lock (_persistLock)
        {
            try
            {
                bool open = _ex.Units != 0;
                if (open && _posOpenedAt == null) _posOpenedAt = Now;
                if (!open) _posOpenedAt = null;
                if (!open && !wasRunning) { SessionStore.DeleteSession(); SessionStore.DeletePosition(); _lastSaved = ""; return; }
                long? lastBar = _lastBar == default ? null : new DateTimeOffset(_lastBar, TimeSpan.Zero).ToUnixTimeSeconds();
                var s = new SessionFile(1, _cfg.Mode, _reqJson, _ex.Export(), wasRunning, default, _posOpenedAt, lastBar);
                string key = JsonSerializer.Serialize(s);
                if (key == _lastSaved) return;
                SessionStore.SaveSession(s with { SavedAt = DateTime.UtcNow });
                _lastSaved = key;
                if (open)
                {
                    var acct = JsonSerializer.SerializeToElement(_ex.Snapshot(Mark));
                    SessionStore.SavePosition(new PositionFile(_cfg.Mode, _cfg.Market.ToString().ToLower(), _cfg.Symbol, _cfg.StrategyId, _cfg.IntervalMin,
                        _ex.Units > 0 ? "long" : "short", _ex.Units, acct.GetProperty("entryPrice").GetDouble(), Math.Abs(_ex.Units) * Mark, _posOpenedAt ?? Now, DateTime.UtcNow));
                }
                else SessionStore.DeletePosition();
            }
            catch (Exception e) { Log("Could not save state: " + e.Message); }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            _strat = _stratFactory();                                   // fresh indicators on every (re)start
            if (_ex is LiveExecutor le) await le.InitAsync(ct);
            int need = Math.Min(1500, _strat.WarmupBars + 400);
            Log($"Loading {need} bars of history...");
            var step = TimeSpan.FromMinutes(_cfg.IntervalMin);
            // Loading history must not give up on a hiccup (e.g. right after a server reboot the network may not be ready yet): retry until it works.
            List<Candle> hist;
            for (int attempt = 1; ; attempt++)
            {
                try { hist = (await _fetch(need, ct)).Where(c => c.Time + step <= DateTime.UtcNow).OrderBy(c => c.Time).ToList(); _lastError = ""; _lastCheck = DateTime.UtcNow; break; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _lastError = e.Message; Log($"Could not load history yet (attempt {attempt}): {e.Message}. Retrying..."); await Task.Delay(Backoff(attempt), ct); }
            }
            if (hist.Count < _strat.WarmupBars) { _state = "error"; Log($"Only {hist.Count} bars available; need {_strat.WarmupBars}."); return; }
            Decision d = default;
            foreach (var c in hist) { d = _strat.OnCandle(c); _lastBar = c.Time; _lastPrice = c.Close; }   // warm-up only: no orders
            _target = d.TargetWeight; _mark = _lastPrice;
            _dayStart = _ex.Equity(_lastPrice); _dayStartDate = DateTime.UtcNow.Date;
            Log($"Warm-up done on {hist.Count} bars. Strategy target weight now {_target:F2}. Syncing position...");
            _state = "running";
            await ExecuteAsync(ct);
            Persist(true);

            var lastBarCheck = DateTime.MinValue; int fetchErrors = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct);

                // every few seconds: refresh the live price so equity / P&L / position follow the market
                try { var px = await _price(ct); if (px > 0) { _mark = px; _markAt = DateTime.UtcNow; _markErr = ""; } }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _markErr = e.Message; }

                // the strategy only acts on CLOSED candles, so look for new ones every 30 seconds
                if (DateTime.UtcNow - lastBarCheck < TimeSpan.FromSeconds(30)) { await RiskChecksAsync(); if (_ex.Halted) return; continue; }
                lastBarCheck = DateTime.UtcNow;
                List<Candle> recent;
                try
                {   // a failed candle request is NOT fatal: note it, wait a little, and try again
                    recent = (await _fetch(8, ct)).Where(c => c.Time + step <= DateTime.UtcNow && c.Time > _lastBar).OrderBy(c => c.Time).ToList();
                    if (fetchErrors > 0) Log("Candle check works again.");
                    fetchErrors = 0; _lastError = ""; _lastCheck = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    fetchErrors++; _lastError = e.Message;
                    if (fetchErrors == 1 || fetchErrors % 10 == 0) Log($"Trouble reaching CoinSwitch for candles (retrying automatically): {e.Message}");
                    lastBarCheck = DateTime.MinValue; await RiskChecksAsync(); await Task.Delay(Backoff(fetchErrors), ct); continue;
                }
                foreach (var c in recent)
                {
                    _ex.AccrueFunding(c.Close, _cfg.IntervalMin / 60.0);
                    var dec = _strat.OnCandle(c);
                    if (dec.ExitPrice is double xp && _ex.Units != 0)
                    {   // a stop/target level was hit inside this candle: paper fills at the level; live exits at the current price (no resting stop orders)
                        await _execLock.WaitAsync(ct);
                        try
                        {
                            var xf = await _ex.RebalanceAsync(0, _cfg.Mode == "paper" ? xp : c.Close, DateTime.UtcNow, ct);
                            if (xf != null) Log($"{_cfg.Mode.ToUpper()} stop/target exit: {xf.Side} {xf.Qty:0.#####} @ {xf.Price:0.##}");
                        }
                        finally { _execLock.Release(); }
                    }
                    _target = dec.TargetWeight; _lastBar = c.Time; _lastPrice = c.Close; if (_mark <= 0) _mark = c.Close;
                    Log($"New closed bar {c.Time:yyyy-MM-dd HH:mm} close {c.Close} -> target weight {_target:F2}");
                    await ExecuteAsync(ct);
                }
                await RiskChecksAsync();
                if (_ex.Halted) return;
            }
        }
        catch (OperationCanceledException) { _state = "stopped"; }
        catch (Exception e) { _state = "error"; Log("ERROR: " + e.Message); }
    }

    /// <summary>Daily-loss limit (live) and halt handling, using the live mark price.</summary>
    private async Task RiskChecksAsync()
    {
        if (_ex is LiveExecutor live)
        {
            if (DateTime.UtcNow.Date != _dayStartDate) { _dayStartDate = DateTime.UtcNow.Date; _dayStart = _ex.Equity(Mark); }
            if (_cfg.Risk.DailyLossPct > 0 && _ex.Equity(Mark) < _dayStart * (1 - _cfg.Risk.DailyLossPct / 100))
                live.Halt($"Daily loss limit {_cfg.Risk.DailyLossPct}% hit");
        }
        if (_ex.Halted) { _state = "halted"; Log("Trader halted: " + _ex.HaltReason); await KillFlatten(); }
    }

    private async Task KillFlatten()
    {
        try { var f = await _ex.RebalanceAsync(0, _lastPrice, DateTime.UtcNow, CancellationToken.None); if (f != null) Log("Position flattened after halt."); }
        catch (Exception e) { Log("Flatten failed - CLOSE MANUALLY in the CoinSwitch app: " + e.Message); }
        Persist(false);
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        await _execLock.WaitAsync(ct);
        try
        {
            var f = await _ex.RebalanceAsync(_target, _lastPrice, DateTime.UtcNow, ct);
            if (f != null) Log($"{_cfg.Mode.ToUpper()} fill: {f.Side} {f.Qty:0.#####} @ {f.Price:0.##} (fee {f.Fee:0.##})");
            if (_ex is LiveExecutor l && l.Halted) _state = "halted";
        }
        finally { _execLock.Release(); }
        Persist(true);
    }

    public object Status()
    {
        string[] log; lock (_log) log = _log.TakeLast(60).ToArray();
        return new
        {
            id = Id, mode = _cfg.Mode, market = _cfg.Market.ToString().ToLower(), symbol = _cfg.Symbol, intervalMin = _cfg.IntervalMin,
            strategy = _cfg.StrategyId, state = _state, running = IsRunning, lastPrice = Mark, lastClose = _lastPrice,
            priceAgeSec = _markAt == default ? (double?)null : (DateTime.UtcNow - _markAt).TotalSeconds, priceError = _markErr,
            lastCheckAgeSec = _lastCheck == default ? (double?)null : (DateTime.UtcNow - _lastCheck).TotalSeconds, lastError = _lastError,
            lastBar = _lastBar == default ? null : (long?)new DateTimeOffset(_lastBar, TimeSpan.Zero).ToUnixTimeSeconds(),
            nextDecision = _lastBar == default ? null : (long?)(new DateTimeOffset(_lastBar, TimeSpan.Zero).ToUnixTimeSeconds() + 2L * _cfg.IntervalMin * 60),
            targetWeight = _target, account = _ex.Snapshot(Mark),
            strategyName = StrategyInfo.Describe(_cfg.StrategyId, _cfg.Params, _cfg.Code, _band).Name, config = Details,
            fills = _ex.Fills.TakeLast(50).Select(f => new { t = new DateTimeOffset(f.Time, TimeSpan.Zero).ToUnixTimeSeconds(), side = f.Side, price = f.Price, qty = f.Qty, fee = f.Fee }),
            log
        };
    }
}

public sealed class TraderManager
{
    private readonly object _lock = new(); public Trader? Current { get; private set; }
    public Trader Start(Trader t)
    {
        lock (_lock)
        {
            if (Current is { IsRunning: true }) throw new InvalidOperationException("A trader is already running. Stop it first.");
            Current?.Owned?.Dispose(); Current = t; t.Start(); return t;
        }
    }
}
