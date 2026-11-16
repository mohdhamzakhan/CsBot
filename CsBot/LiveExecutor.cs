using System.Text.Json;

namespace CsBot;

/// <summary>
/// REAL orders. Safeguards: hard risk limits, daily-loss halt, fixed allocated capital (no compounding),
/// orders confirmed by polling the exchange; unfilled spot limit orders are cancelled.
/// Position is tracked from confirmed fills only. NOT validated against the live exchange by the author - start tiny.
/// Every failure to place or confirm an order is written to the log with what was attempted, what CoinSwitch answered and the likely cause.
/// </summary>
public sealed class LiveExecutor : IExecutor
{
    private readonly CoinSwitchClient _cs; private readonly TraderConfig _cfg; private readonly double _band; private readonly Action<string> _log;
    private readonly List<Fill> _fills = new();
    private double _avg, _realized, _fees; private int _orders;
    public double Units { get; private set; }
    public bool Halted { get; private set; }
    public string HaltReason { get; private set; } = "";
    /// <summary>Most recent order problem (shown in the UI until an order succeeds).</summary>
    public string LastOrderError { get; private set; } = "";
    public long LastOrderErrorAt { get; private set; }
    public IReadOnlyList<Fill> Fills => _fills;

    public LiveExecutor(CoinSwitchClient cs, TraderConfig cfg, double band, Action<string> log) { _cs = cs; _cfg = cfg; _band = band; _log = log; }

    private string Quote => _cfg.Market == Market.Futures ? "USDT" : (_cfg.Symbol.Split('/').LastOrDefault() ?? "");

    private void NoteError(string msg)
    {
        LastOrderError = msg; LastOrderErrorAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _log(msg);
    }

    /// <summary>Runs an exchange call; on failure logs a plain-language explanation and throws an OrderException (never a silent cancel).</summary>
    private async Task<T> Guard<T>(string what, Func<Task<T>> call, CancellationToken ct, string? note = null, string label = "ORDER FAILED")
    {
        try { return await call(); }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            string msg = OrderDiagnostics.Explain(e, what, label) + (note != null ? " | " + note : "");
            NoteError(msg);
            throw new OrderException(msg, e);
        }
    }

    public async Task InitAsync(CancellationToken ct)
    {
        if (_cfg.Market == Market.Futures)
        {
            int lev = (int)Math.Max(1, Math.Floor(Math.Min(_cfg.Risk.MaxLeverage, _cfg.Cost.MaxLeverage)));
            await Guard($"set leverage {lev}x on {_cfg.Symbol}", async () => { await _cs.FuturesSetLeverageAsync(_cfg.Symbol, lev, ct); return true; }, ct, label: "SETUP FAILED");
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
        if (Math.Abs(targetUnits * price) > _cfg.Risk.MaxPositionNotional)
        { NoteError($"ORDER BLOCKED by your risk limit: target position {Math.Abs(targetUnits * price):F0} {Quote} exceeds 'Max position' {_cfg.Risk.MaxPositionNotional:F0}. Raise the limit or lower the capital."); return null; }
        double delta = targetUnits - Units;
        if (Math.Abs(delta) < Math.Pow(10, -_cfg.QtyPrecision))
        { _log($"ORDER SKIPPED: the needed change ({delta:0.########}) is smaller than the smallest quantity step ({_cfg.QtyPrecision} decimals). Raise the capital or 'Quantity decimals'."); return null; }

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
    private static string Short(JsonElement o) { var t = o.GetRawText(); return t.Length <= 300 ? t : t[..300] + "..."; }

    private async Task<Fill?> SpotAsync(double delta, double price, DateTime time, CancellationToken ct)
    {
        string side = delta > 0 ? "buy" : "sell";
        double qty = RoundDown(Math.Abs(delta), _cfg.QtyPrecision);
        if (side == "sell") qty = Math.Min(qty, RoundDown(Units, _cfg.QtyPrecision));
        if (qty <= 0)
        { _log($"ORDER SKIPPED: the {side} quantity rounds to 0 with {_cfg.QtyPrecision} decimals (wanted {Math.Abs(delta):0.########}). Raise the capital or 'Quantity decimals'."); return null; }
        double px = Math.Round(price * (side == "buy" ? 1.003 : 0.997), _cfg.PricePrecision);   // marketable limit (spot supports LIMIT only)
        if (side == "buy" && qty * px > _cfg.Risk.MaxOrderNotional)
        { NoteError($"ORDER BLOCKED by your risk limit: buy order {qty * px:F0} {Quote} exceeds 'Max single order' {_cfg.Risk.MaxOrderNotional:F0}. Raise the limit or lower the capital."); return null; }

        string what = $"spot {side.ToUpper()} {qty} {_cfg.Symbol} @ limit {px} (about {qty * px:0.##} {Quote})";
        _log($"SPOT {side.ToUpper()} {qty} {_cfg.Symbol} @ limit {px}");
        var o = await Guard(what, () => _cs.SpotPlaceLimitAsync(_cfg.Exchange, _cfg.Symbol, side, px, qty, Guid.NewGuid().ToString(), ct), ct);
        string id = CoinSwitchClient.Str(o, "order_id"); _orders++;
        if (id == "") { Halt("CoinSwitch answered but gave no order_id, so it is unknown whether the order exists. Check the CoinSwitch app. Response: " + Short(o)); return null; }

        string placed = $"The order WAS placed (order id {id}): check the CoinSwitch app before doing anything else.";
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(3000, ct);
            o = await Guard($"check status of spot order {id}", () => _cs.SpotGetOrderAsync(id, ct), ct, placed);
            if (Done(CoinSwitchClient.Str(o, "status"))) break;
        }
        if (!Done(CoinSwitchClient.Str(o, "status")))
        {
            _log($"Order {id} not filled after 45 seconds: cancelling it.");
            try { await _cs.SpotCancelAsync(id, ct); } catch (Exception e) { _log(OrderDiagnostics.Explain(e, $"cancel spot order {id}", "CANCEL FAILED") + " | Check the CoinSwitch app: the order may still be open."); }
            await Task.Delay(2000, ct);
            o = await Guard($"check status of spot order {id}", () => _cs.SpotGetOrderAsync(id, ct), ct, placed);
        }
        double ex = CoinSwitchClient.Num(o, "executed_qty"), avg = CoinSwitchClient.Num(o, "average_price");
        string status = CoinSwitchClient.Str(o, "status");
        _log($"Order {id}: status {status}, filled {ex} @ {avg}");
        if (ex <= 0)
        {
            NoteError($"ORDER NOT FILLED: {what} -> exchange status '{status}', nothing was bought or sold (order id {id}). " +
                      $"The price probably moved past the limit, or the order was rejected. Exchange reply: {Short(o)} | The bot tries again only after the next candle closes.");
            return null;
        }
        return Apply(side, ex, avg > 0 ? avg : px, ex * (avg > 0 ? avg : px) * _cfg.Cost.FeeRate, time);
    }

    private async Task<Fill?> FuturesAsync(double delta, double price, DateTime time, bool reduceOnly, CancellationToken ct)
    {
        string side = delta > 0 ? "BUY" : "SELL";
        double qty = RoundDown(Math.Abs(delta), _cfg.QtyPrecision);
        if (qty <= 0)
        { _log($"ORDER SKIPPED: the futures quantity rounds to 0 with {_cfg.QtyPrecision} decimals (wanted {Math.Abs(delta):0.########}). Raise the capital or 'Quantity decimals'."); return null; }
        if (!reduceOnly && qty * price > _cfg.Risk.MaxOrderNotional)
        { NoteError($"ORDER BLOCKED by your risk limit: order {qty * price:F0} {Quote} exceeds 'Max single order' {_cfg.Risk.MaxOrderNotional:F0}. Raise the limit or lower the capital."); return null; }

        string what = $"futures {side} {qty} {_cfg.Symbol} MARKET reduceOnly={reduceOnly} (about {qty * price:0.##} {Quote})";
        _log($"FUTURES {side} {qty} {_cfg.Symbol} MARKET reduceOnly={reduceOnly}");
        var o = await Guard(what, () => _cs.FuturesPlaceAsync(_cfg.Symbol, side, "MARKET", qty, null, reduceOnly, Guid.NewGuid().ToString(), ct), ct);
        string id = CoinSwitchClient.Str(o, "order_id"); _orders++;
        if (id == "") { Halt("CoinSwitch answered but gave no order_id, so it is unknown whether the order exists. Check the CoinSwitch app. Response: " + Short(o)); return null; }

        string placed = $"The order WAS placed (order id {id}): check the CoinSwitch app before doing anything else.";
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(2000, ct);
            o = await Guard($"check status of futures order {id}", () => _cs.FuturesGetOrderAsync(id, ct), ct, placed);
            if (Done(CoinSwitchClient.Str(o, "status"))) break;
        }
        double ex = CoinSwitchClient.Num(o, "exec_quantity"), avg = CoinSwitchClient.Num(o, "avg_execution_price"), fee = CoinSwitchClient.Num(o, "execution_fee");
        string status = CoinSwitchClient.Str(o, "status");
        _log($"Order {id}: status {status}, filled {ex} @ {avg}, fee {fee}");
        if (ex <= 0)
        {
            string msg = $"ORDER NOT FILLED: {what} -> exchange status '{status}' (order id {id}). Exchange reply: {Short(o)}";
            NoteError(msg);
            Halt($"Market order {id} shows no fill. Check the CoinSwitch app before restarting.");
            return null;
        }
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
        LastOrderError = "";                                          // an order just worked: clear the warning
        var f = new Fill(time, side, px, qty, fee, 0); _fills.Add(f);
        return f;
    }

    public object Snapshot(double price) => new
    {
        start = _cfg.Capital,
        equity = Equity(price),
        units = Units,
        avgEntry = _avg,
        realized = _realized,
        fees = _fees,
        trades = _orders,
        halted = Halted,
        haltReason = HaltReason,
        lastOrderError = LastOrderError,
        lastOrderErrorAt = LastOrderErrorAt,
        side = Units > 0 ? "long" : Units < 0 ? "short" : "flat",
        entryPrice = _avg,
        notional = Math.Abs(Units) * price,
        unrealizedPnl = Units == 0 ? 0 : Units * (price - _avg),
        unrealizedPct = Units == 0 || _avg == 0 ? 0 : Math.Sign(Units) * (price / _avg - 1) * 100,
        weight = _cfg.Capital > 0 ? Units * price / _cfg.Capital : 0
    };
}