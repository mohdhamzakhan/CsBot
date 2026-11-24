using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsBot;

// ============================================================================================
// LIVE futures protection that RESTS ON THE EXCHANGE.
//
// A strategy can only look at a candle when it closes. With leverage that is far too slow, so when the
// setting "exchStops" is on, every time the position changes this places two position-level, reduce-only
// orders on CoinSwitch (documented order types STOP_MARKET and TAKE_PROFIT_MARKET, quantity 0):
//     stop loss     at   entry -/+ stopPct  / L   (or the liquidation guard, whichever is tighter)
//     take profit   at   entry +/- takePct  / L
// where L is the position size as a multiple of capital. They are cancelled whenever the position is closed,
// and replaced whenever it changes. The trailing stop cannot rest on the exchange (it stays candle-based).
//
// Safety design:
//   * If a protective order cannot be placed after 3 tries, the trader HALTS and the position is closed
//     rather than left unprotected (the existing halt-and-flatten path).
//   * Every few seconds the bot compares its position with CoinSwitch's. If the exchange closed it (a stop,
//     a target, or liquidation), the fill is booked, the leftover order is cancelled and the bot carries on flat.
//   * Orders placed by this app carry the client id prefix "csbot-", so leftovers (for example after a restart)
//     can be found and cleaned up without touching your other orders.
// NOT VALIDATED AGAINST THE LIVE EXCHANGE BY THE AUTHOR. Test with the smallest size, and watch the orders appear
// and disappear in the CoinSwitch app. Symbol format, price decimals and the position-size unit may need adjusting.
// ============================================================================================

public sealed partial class LiveExecutor
{
    /// <summary>Pause between retries when a protective order is refused (tests shorten it).</summary>
    internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const string SlTag = "csbot-sl-", TpTag = "csbot-tp-";
    private string _slId = "", _tpId = "";

    private ProtectionSpec Spec => ProtectionSpec.FromParams(_cfg.Params);
    internal bool UseExchangeStops => _cfg.Market == Market.Futures && Spec.ExchangeOrders && Spec.Any;

    private double SignedSize(JsonElement positions)
    {
        double total = 0;
        if (positions.ValueKind != JsonValueKind.Array) return 0;
        foreach (var p in positions.EnumerateArray())
        {
            string sym = CoinSwitchClient.Str(p, "symbol");
            if (sym.Length > 0 && !sym.Equals(_cfg.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            double size = CoinSwitchClient.Num(p, "position_size");
            total += Regex.IsMatch(CoinSwitchClient.Str(p, "position_side"), "short|sell", RegexOptions.IgnoreCase) ? -size : size;
        }
        return total;
    }

    private async Task<string> PlaceTriggerAsync(string type, string side, double trigger, string tag, CancellationToken ct)
    {
        string what = $"{type} {side} trigger {trigger} on {_cfg.Symbol} (whole position, reduce-only)";
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var o = await _cs.FuturesPlaceTriggerAsync(_cfg.Symbol, side, type, trigger, tag + Guid.NewGuid().ToString("N")[..12], ct);
                string id = CoinSwitchClient.Str(o, "order_id");
                if (id != "") { _log($"PROTECTION: {what} placed (order {id})"); return id; }
                last = new InvalidOperationException("no order_id in the reply: " + Short(o));
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested)) { last = e; }
            if (attempt < 3) await Task.Delay(RetryDelay, ct);
        }
        string msg = OrderDiagnostics.Explain(last!, "place " + what, "PROTECTION FAILED");
        NoteError(msg);
        Halt("The protective order could not be placed, so the position is being closed instead of left unprotected. " + msg);
        return "";
    }

    /// <summary>Cancels the protective orders this app placed (known ids, plus any open order whose client id starts with "csbot-").</summary>
    private async Task CancelProtectiveAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>();
        if (_slId != "") ids.Add(_slId);
        if (_tpId != "") ids.Add(_tpId);
        _slId = _tpId = "";
        try
        {
            var open = await _cs.FuturesOpenOrdersAsync(_cfg.Symbol, ct);
            if (open.ValueKind == JsonValueKind.Array)
                foreach (var o in open.EnumerateArray())
                {
                    string cid = CoinSwitchClient.Str(o, "client_order_id");
                    if (cid.StartsWith(SlTag) || cid.StartsWith(TpTag)) ids.Add(CoinSwitchClient.Str(o, "order_id"));
                }
        }
        catch (Exception e) when (e is not OperationCanceledException) { _log("Could not list open orders to clean up protective orders: " + e.Message); }
        foreach (var id in ids.Where(i => i != ""))
        {
            try { await _cs.FuturesCancelAsync(id, ct); _log($"PROTECTION: cancelled resting order {id}"); }
            catch (Exception e) when (e is not OperationCanceledException)
            { _log(OrderDiagnostics.Explain(e, $"cancel protective order {id}", "CANCEL FAILED") + " (it may already have triggered or been cancelled)"); }
        }
    }

    /// <summary>Called after every change of the position: removes the old protective orders and places new ones for what is held now.</summary>
    internal async Task SyncProtectionAsync(CancellationToken ct)
    {
        if (!UseExchangeStops) return;
        await CancelProtectiveAsync(ct);
        if (Units == 0 || _avg <= 0) return;
        int dir = Units > 0 ? 1 : -1;
        double effLev = Math.Abs(Units) * _avg / _cfg.Capital;                    // size as a multiple of capital
        var (stop, take, _) = Protection.Levels(Spec, dir, _avg, effLev);
        string side = dir > 0 ? "SELL" : "BUY";
        if (stop is double s) _slId = await PlaceTriggerAsync("STOP_MARKET", side, Math.Round(s, _cfg.PricePrecision), SlTag, ct);
        if (!Halted && take is double t) _tpId = await PlaceTriggerAsync("TAKE_PROFIT_MARKET", side, Math.Round(t, _cfg.PricePrecision), TpTag, ct);
    }

    /// <summary>Compares the bot's position with the exchange's. If the exchange closed it, books the fill and carries on flat.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        if (!UseExchangeStops || Units == 0) return;
        JsonElement pos;
        try { pos = await _cs.FuturesPositionsAsync(_cfg.Symbol, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _log("Position check failed (will retry): " + e.Message); return; }
        double real = SignedSize(pos);
        if (Math.Abs(real - Units) <= Math.Max(1e-9, Math.Abs(Units) * 0.02)) return;                 // still holding what we think
        if (Math.Abs(real) >= Math.Pow(10, -_cfg.QtyPrecision))
        {
            NoteError($"POSITION MISMATCH: the bot tracks {Units:0.######} {_cfg.Symbol} but CoinSwitch shows {real:0.######}. Check the CoinSwitch app. The bot changed nothing.");
            return;
        }

        // the exchange closed the position: one of our resting orders fired, or it was liquidated, or closed by hand
        bool booked = false;
        foreach (var id in new[] { _slId, _tpId })
        {
            if (id == "") continue;
            try
            {
                var o = await _cs.FuturesGetOrderAsync(id, ct);
                double ex = CoinSwitchClient.Num(o, "exec_quantity"), avg = CoinSwitchClient.Num(o, "avg_execution_price"), fee = CoinSwitchClient.Num(o, "execution_fee");
                if (ex > 0 && avg > 0)
                {
                    string kind = id == _slId ? "stop-loss" : "take-profit";
                    var f = Apply(Units > 0 ? "sell" : "buy", Math.Min(ex, Math.Abs(Units)), avg, fee, DateTime.UtcNow);      // booked like any other fill
                    _log($"PROTECTION TRIGGERED: the {kind} order closed the position on the exchange: {f.Side} {f.Qty:0.#####} @ {f.Price:0.##}");
                    booked = true; break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { _log($"Could not read protective order {id}: {e.Message}"); }
        }
        if (!booked) { _log($"Position {Units:0.######} is gone on the exchange (liquidated or closed by hand). Forgot it; no orders were sent."); Units = 0; _avg = 0; }
        await CancelProtectiveAsync(ct);                                          // remove the leg that did not fire
    }
}