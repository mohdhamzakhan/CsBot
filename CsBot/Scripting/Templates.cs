namespace CsBot.Scripting;

public sealed record ScriptTemplate(string Id, string Name, string Description, string Evidence, string Code);

/// <summary>
/// Classic strategies written in CsScript. They are starting points to read, edit and test - none is a guaranteed edge.
/// short() means "be flat" on spot, so the same script works on both markets.
/// </summary>
public static class Templates
{
    private const string Trend = "Trend-following family: the best-documented style for crypto, but results depend on the period and costs.";
    private const string MeanRev = "Mean-reversion family: weaker evidence in crypto (strong trends hurt it) and fees/TDS bite because it trades often. Treat with extra suspicion.";

    public static readonly IReadOnlyList<ScriptTemplate> All = new[]
    {
        new ScriptTemplate("ema_trend", "EMA trend (state-based)", "Long while the fast EMA is above the slow EMA and price is above the slow EMA; short/flat when below.", Trend, """
// Trend following: ride the trend defined by two moving averages.
fast = input(20, "Fast EMA", 2, 200, 1)
slow = input(100, "Slow EMA", 5, 400, 1)
emaF = ema(close, fast)
emaS = ema(close, slow)
plot(emaF, "Fast EMA")
plot(emaS, "Slow EMA")
up = emaF > emaS and close > emaS
down = emaF < emaS and close < emaS
if up then long(1)
if down then short(1)       // on spot, short() simply means "be flat"
if not up and not down then flat()
"""),
        new ScriptTemplate("macd_trend", "MACD cross + 200 EMA filter", "Trade MACD/signal crossovers only in the direction of the long-term EMA.", Trend, """
// MACD momentum filtered by a long-term trend EMA.
fastLen = input(12, "MACD fast", 2, 50, 1)
slowLen = input(26, "MACD slow", 5, 100, 1)
sigLen = input(9, "Signal", 2, 50, 1)
trendLen = input(200, "Trend EMA", 50, 400, 1)
line = macd(close, fastLen, slowLen)
sig = macd_signal(close, fastLen, slowLen, sigLen)
trend = ema(close, trendLen)
plot(trend, "Trend EMA")
plot_sub(line, "MACD")
plot_sub(sig, "Signal")
if line > sig and close > trend then long(1)
if line < sig and close < trend then short(1)
if line > sig and close <= trend then flat()
if line < sig and close >= trend then flat()
"""),
        new ScriptTemplate("supertrend", "Supertrend follower", "ATR-based trailing trend line; long when price is above it, short/flat when below.", Trend, """
// Supertrend: an ATR-based trailing line that flips with the trend.
len = input(10, "ATR length", 2, 100, 1)
mult = input(3, "ATR multiplier", 1, 8, 0.5)
st = supertrend(len, mult)
dir = supertrend_dir(len, mult)
plot(st, "Supertrend")
if dir == 1 then long(1)
if dir == -1 then short(1)
"""),
        new ScriptTemplate("adx_trend", "ADX-filtered EMA trend", "Only trade the EMA trend when ADX says the market is actually trending; otherwise stay flat (fewer whipsaw trades).", Trend, """
// Trend following with a trend-strength filter to avoid sideways chop.
fast = input(20, "Fast EMA", 2, 200, 1)
slow = input(100, "Slow EMA", 5, 400, 1)
adxLen = input(14, "ADX length", 5, 50, 1)
adxMin = input(22, "Min ADX", 10, 50, 1)
emaF = ema(close, fast)
emaS = ema(close, slow)
strength = adx(adxLen)
plot(emaF, "Fast EMA")
plot(emaS, "Slow EMA")
plot_sub(strength, "ADX")
trending = strength > adxMin
if trending and emaF > emaS then long(1)
if trending and emaF < emaS then short(1)
if not trending then flat()
"""),
        new ScriptTemplate("donchian", "Donchian breakout (Turtle-style)", "Enter on a new N-bar high, exit on a shorter M-bar low (mirror for shorts).", Trend, """
// Classic channel breakout used by trend-following funds.
entryLen = input(55, "Entry lookback", 10, 200, 1)
exitLen = input(20, "Exit lookback", 5, 100, 1)
hiE = highest(high, entryLen)
loE = lowest(low, entryLen)
hiX = highest(high, exitLen)
loX = lowest(low, exitLen)
plot(hiE, "Entry high")
plot(loX, "Exit low")
if close > hiE[1] then long(1)
if position == 1 and close < loX[1] then flat()
if close < loE[1] then short(1)
if position == -1 and close > hiX[1] then flat()
"""),
        new ScriptTemplate("atr_channel", "ATR channel (Keltner) breakout", "Enter when price closes beyond an EMA +/- ATR band, exit back at the EMA.", Trend, """
// Volatility-adjusted breakout: the band widens when the market is wild.
len = input(20, "EMA/ATR length", 5, 100, 1)
mult = input(2, "ATR multiplier", 1, 6, 0.5)
mid = ema(close, len)
rng = atr(len)
upper = mid + mult * rng
lower = mid - mult * rng
plot(upper, "Upper")
plot(lower, "Lower")
plot(mid, "Mid")
if close > upper then long(1)
if position == 1 and close < mid then flat()
if close < lower then short(1)
if position == -1 and close > mid then flat()
"""),
        new ScriptTemplate("momentum_trend", "Momentum (ROC) + trend filter", "Long when the past N-bar return is positive and price is above its EMA.", Trend, """
// Time-series momentum with a trend filter and a trailing stop.
lookback = input(90, "Momentum lookback", 10, 400, 1)
trendLen = input(100, "Trend EMA", 20, 400, 1)
trailPct = input(15, "Trailing stop %", 0, 50, 1)
m = roc(close, lookback)
trend = ema(close, trendLen)
plot(trend, "Trend EMA")
plot_sub(m, "ROC %")
trail_stop(trailPct)
if m > 0 and close > trend then long(1)
if m < 0 and close < trend then short(1)
if (m <= 0 and close > trend) or (m >= 0 and close < trend) then flat()
"""),
        new ScriptTemplate("vol_target_trend", "Volatility-targeted trend", "EMA trend with position size scaled so risk stays about constant (smaller when the market is wild).", Trend, """
// Position size = target volatility / current volatility (capped at 1).
fast = input(20, "Fast EMA", 2, 200, 1)
slow = input(100, "Slow EMA", 5, 400, 1)
targetVol = input(0.4, "Target annual vol", 0.1, 1, 0.05)
volLen = input(60, "Vol lookback", 10, 200, 1)
emaF = ema(close, fast)
emaS = ema(close, slow)
w = min(1, targetVol / vol(volLen))
plot(emaF, "Fast EMA")
plot(emaS, "Slow EMA")
if emaF > emaS and close > emaS then long(w)
if emaF < emaS and close < emaS then short(w)
if (emaF > emaS and close <= emaS) or (emaF < emaS and close >= emaS) then flat()
"""),
        new ScriptTemplate("rsi_pullback", "RSI pullback in an uptrend", "Buy dips (low RSI) only while price is above its long-term EMA; sell on RSI recovery.", MeanRev, """
// Buy weakness inside an uptrend, exit on strength.
rsiLen = input(14, "RSI length", 2, 50, 1)
buyBelow = input(30, "Buy below RSI", 5, 50, 1)
sellAbove = input(55, "Sell above RSI", 40, 90, 1)
trendLen = input(200, "Trend EMA", 50, 400, 1)
r = rsi(close, rsiLen)
trend = ema(close, trendLen)
plot(trend, "Trend EMA")
plot_sub(r, "RSI")
stop_loss(10)
if r < buyBelow and close > trend and position == 0 and not (bars_since_exit < 5) then long(1)
if r > sellAbove then flat()
"""),
        new ScriptTemplate("connors_rsi2", "Connors RSI(2) pullback", "Very short-term RSI(2) dips above the 200-bar average; exit when price recovers above a 5-bar average.", MeanRev, """
// Short-term mean reversion popularised by Larry Connors (originally for stocks).
trendLen = input(200, "Trend SMA", 50, 400, 1)
exitLen = input(5, "Exit SMA", 2, 30, 1)
buyBelow = input(10, "Buy below RSI(2)", 2, 30, 1)
trend = sma(close, trendLen)
exitMa = sma(close, exitLen)
r = rsi(close, 2)
plot(trend, "Trend SMA")
plot_sub(r, "RSI(2)")
stop_loss(10)
if close > trend and r < buyBelow and position == 0 and not (bars_since_exit < 5) then long(1)
if close > exitMa then flat()
"""),
        new ScriptTemplate("bollinger_revert", "Bollinger Bands mean reversion", "Buy below the lower band, exit at the middle band (short above the upper band on futures).", MeanRev, """
// Fade stretched moves back to the average.
len = input(20, "Length", 5, 100, 1)
mult = input(2, "Std devs", 1, 4, 0.1)
up = bb_upper(close, len, mult)
lo = bb_lower(close, len, mult)
mid = sma(close, len)
plot(up, "Upper")
plot(lo, "Lower")
plot(mid, "Middle")
stop_loss(8)
if close < lo and position == 0 and not (bars_since_exit < 5) then long(1)
if position == 1 and close > mid then flat()
if close > up and position == 0 and not (bars_since_exit < 5) then short(1)
if position == -1 and close < mid then flat()
"""),
        new ScriptTemplate("bollinger_breakout", "Bollinger Bands breakout", "Go with moves that close outside the band; exit when price falls back to the middle band.", Trend, """
// Treat a close outside the band as the start of a trend.
len = input(20, "Length", 5, 100, 1)
mult = input(2, "Std devs", 1, 4, 0.1)
up = bb_upper(close, len, mult)
lo = bb_lower(close, len, mult)
mid = sma(close, len)
plot(up, "Upper")
plot(lo, "Lower")
plot(mid, "Middle")
if close > up then long(1)
if position == 1 and close < mid then flat()
if close < lo then short(1)
if position == -1 and close > mid then flat()
"""),
        new ScriptTemplate("zscore_revert", "Z-score mean reversion + trend filter", "Buy when price is unusually far below its average (in an uptrend); exit when it returns to the average.", MeanRev, """
// Statistical mean reversion: how many standard deviations from normal?
len = input(30, "Window", 10, 200, 1)
entryZ = input(2, "Entry z-score", 1, 4, 0.1)
trendLen = input(200, "Trend EMA", 50, 400, 1)
z = (close - sma(close, len)) / stdev(close, len)
trend = ema(close, trendLen)
plot(trend, "Trend EMA")
plot_sub(z, "Z-score")
stop_loss(8)
if z < -entryZ and close > trend and position == 0 and not (bars_since_exit < 5) then long(1)
if position == 1 and z > 0 then flat()
if z > entryZ and close < trend and position == 0 and not (bars_since_exit < 5) then short(1)
if position == -1 and z < 0 then flat()
"""),
        new ScriptTemplate("stoch_pullback", "Stochastic pullback in an uptrend", "Buy when the smoothed stochastic is oversold while price is above its EMA; exit when overbought.", MeanRev, """
// Oversold dips inside an uptrend, using %K smoothed by a 3-bar average.
kLen = input(14, "Stochastic length", 5, 50, 1)
buyBelow = input(20, "Buy below", 5, 40, 1)
sellAbove = input(75, "Sell above", 60, 95, 1)
trendLen = input(100, "Trend EMA", 20, 400, 1)
k = stoch_k(kLen)
ks = sma(k, 3)
trend = ema(close, trendLen)
plot(trend, "Trend EMA")
plot_sub(ks, "Stoch %K")
stop_loss(10)
if close > trend and ks < buyBelow and position == 0 and not (bars_since_exit < 5) then long(1)
if ks > sellAbove then flat()
"""),
        new ScriptTemplate("orb", "Opening Range Breakout (ORB)", "Mark the high/low of the first minutes of a chosen session; enter when a candle CLOSES outside it (optionally on a retest), stop inside the range, target 1.5R-2R. One trade per session, flat at session end. Use 5m or 15m candles.",
            "Intraday breakout idea from equity/futures markets. Crypto has no real open, so you choose a session (default 13:30 UTC = US open = 19:00 IST). Evidence in crypto is thin; false breakouts in choppy markets are the main risk, and frequent trading makes fees, slippage and spot TDS decisive. Futures is the more sensible market for it.", """
// Opening Range Breakout (ORB). Best on 5m or 15m candles.
// Common sessions in minutes after 00:00 UTC:  Asia 0 | Europe 420 | US open 810 (13:30 UTC = 19:00 IST)
sessStart = input(810, "Session start (min after 00:00 UTC)", 0, 1439, 15)
rangeMin = input(15, "Opening range length (min)", 5, 120, 5)
windowMin = input(240, "Entry window after the range (min)", 15, 1440, 15)
sessLen = input(480, "Session length (min) - flat at the end", 60, 1440, 15)
volMult = input(1.2, "Breakout volume vs 20-bar avg (0 = ignore)", 0, 5, 0.1)
rr = input(1.5, "Target = R x", 0.5, 4, 0.25)
stopMode = input(0, "Stop: 0 = far side of range, 1 = range midpoint", 0, 1, 1)
useRetest = input(0, "Entry: 0 = breakout close, 1 = wait for retest", 0, 1, 1)
riskPct = input(1, "Risk per trade, % of capital (0 = full size)", 0, 10, 0.25)

mins = session_min(sessStart)
newSess = new_session(sessStart)
avgVol = sma(volume, 20)
volOk = volMult == 0 or volume > volMult * avgVol

var rh = na
var rl = na
var done = 0
var brk = 0
if newSess
  rh := high
  rl := low
  done := 0
  brk := 0
elif mins < rangeMin
  rh := max(rh, high)
  rl := min(rl, low)
end

armed = mins >= rangeMin and mins < sessLen and not na(rh)
inWindow = mins < rangeMin + windowMin
canTrade = armed and inWindow and done == 0 and position == 0

breakUp = close > rh and volOk
breakDn = close < rl and volOk
retestLong = brk == 1 and low <= rh and close > rh
retestShort = brk == -1 and high >= rl and close < rl
failedUp = brk == 1 and close < rh
failedDn = brk == -1 and close > rl

// stop inside the range (or beyond the retest candle), size so a stop-out loses riskPct of capital
stopLong = useRetest == 1 ? low : (stopMode == 0 ? rl : (rh + rl) / 2)
stopShort = useRetest == 1 ? high : (stopMode == 0 ? rh : (rh + rl) / 2)
riskLong = close - stopLong
riskShort = stopShort - close
sizeLong = riskPct == 0 ? 1 : min(1, riskPct / 100 / (riskLong / close))
sizeShort = riskPct == 0 ? 1 : min(1, riskPct / 100 / (riskShort / close))
goLong = useRetest == 1 ? retestLong : breakUp
goShort = useRetest == 1 ? retestShort : breakDn

if canTrade and goLong and riskLong > 0
  long(sizeLong)
  stop_at(stopLong)
  target_at(close + rr * riskLong)
end
if canTrade and goShort and riskShort > 0
  short(sizeShort)
  stop_at(stopShort)
  target_at(close - rr * riskShort)
end
if position != 0 then done := 1

// retest mode: remember a breakout close, forget it if price closes back inside (false breakout)
if useRetest == 1 and armed and done == 0
  if breakUp and brk == 0 then brk := 1
  if breakDn and brk == 0 then brk := -1
  if failedUp or failedDn then brk := 0
end

if mins >= sessLen and position != 0 then flat()
plot(armed ? rh : na, "Range high")
plot(armed ? rl : na, "Range low")
"""),
        new ScriptTemplate("trend_pullback_vwap", "Trend pullback: EMA + VWAP (intraday)", "Long bias: price above VWAP and 9 EMA above 21 EMA. Wait for a pullback toward VWAP or the 21 EMA, then enter on a bullish confirmation candle; stop below the pullback low. Short bias is the mirror image (futures). Use 5m, 15m or 1h candles.",
            "Mainstream intraday idea: trade with the trend and do not chase spikes. Evidence for it in crypto is anecdotal, not tested. It trades often, so fees/slippage (and spot TDS) matter, and it struggles in sideways chop. Your spec did not give a target, so the target is an optional setting (2R by default, 0 = off).", """
// Trend pullback: EMA + VWAP (intraday). Best on 5m / 15m / 1h candles.
fast = input(9, "Fast EMA", 3, 50, 1)
slow = input(21, "Slow EMA", 5, 100, 1)
vwapStart = input(0, "VWAP restarts at (min after 00:00 UTC)", 0, 1439, 15)
atrLen = input(14, "ATR length", 5, 50, 1)
pbLen = input(5, "Pullback look-back (bars)", 2, 30, 1)
nearAtr = input(0.25, "How close to VWAP / slow EMA counts as a pullback (ATR)", 0, 3, 0.05)
confirmMode = input(1, "Confirmation: 0 = bullish candle, 1 = bullish candle closing above the previous high", 0, 1, 1)
extMult = input(3, "Skip if price is more than this many ATR from VWAP (no chasing)", 0.5, 10, 0.5)
buffer = input(0.1, "Stop buffer beyond the pullback extreme (ATR)", 0, 1, 0.05)
rr = input(2, "Target = R x (0 = no fixed target)", 0, 5, 0.25)
riskPct = input(1, "Risk per trade, % of capital (0 = full size)", 0, 10, 0.25)

vw = vwap(vwapStart)
emaF = ema(close, fast)
emaS = ema(close, slow)
a = atr(atrLen)
plot(vw, "VWAP")
plot(emaF, "Fast EMA")
plot(emaS, "Slow EMA")

// bias: price vs VWAP and the EMA stack
upTrend = close > vw and emaF > emaS
downTrend = close < vw and emaF < emaS

// pullback: within the last pbLen bars price came to (or through) the support/resistance zone formed by VWAP and the slow EMA
supUp = max(vw, emaS)
supDn = min(vw, emaS)
touchedUp = lowest(low - supUp, pbLen) <= nearAtr * a
touchedDn = lowest(supDn - high, pbLen) <= nearAtr * a
pbLow = lowest(low, pbLen)
pbHigh = highest(high, pbLen)

// confirmation candle
bullConfirm = close > open and (confirmMode == 0 or close > high[1])
bearConfirm = close < open and (confirmMode == 0 or close < low[1])

// stop beyond the pullback extreme; size so a stop-out loses riskPct of capital
stopL = pbLow - buffer * a
stopS = pbHigh + buffer * a
riskL = close - stopL
riskS = stopS - close
sizeL = riskPct == 0 ? 1 : min(1, riskPct / 100 / (riskL / close))
sizeS = riskPct == 0 ? 1 : min(1, riskPct / 100 / (riskS / close))
cool = not (bars_since_exit < 3)

if position == 0 and cool and upTrend and touchedUp and bullConfirm and close - vw < extMult * a and riskL > 0
  long(sizeL)
  stop_at(stopL)
  target_at(rr > 0 ? close + rr * riskL : 0)
end
if position == 0 and cool and downTrend and touchedDn and bearConfirm and vw - close < extMult * a and riskS > 0
  short(sizeS)
  stop_at(stopS)
  target_at(rr > 0 ? close - rr * riskS : 0)
end
// bias fully flipped: get out
if position == 1 and close < vw and emaF < emaS then flat()
if position == -1 and close > vw and emaF > emaS then flat()
"""),
        new ScriptTemplate("blank", "Blank starter", "Minimal skeleton: a plotted EMA and a simple rule. Replace with your own idea.", "Your own idea: test it with the same costs, walk-forward check and live gate.", """
// Your strategy. Lines starting with // are comments.
len = input(50, "EMA length", 5, 300, 1)
e = ema(close, len)
plot(e, "EMA")
if close > e then long(1)
if close < e then flat()
"""),
    };
}
