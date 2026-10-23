namespace CsBot;

/// <summary>
/// EMA trend filter + ATR trailing stop + volatility-targeted sizing (inspired by the trend-following literature).
/// Fixed, non-optimised defaults on purpose; tune only via walk-forward tests.
/// </summary>
public sealed class TrendFollowingStrategy : IStrategy
{
    public sealed record Params(
        int FastEma = 20, int SlowEma = 100, int AtrPeriod = 14, double StopAtr = 3.0,
        double TargetVol = 0.40, int VolLookback = 60, double BarsPerYear = 1460,
        double MaxWeight = 1.0, bool AllowShort = false, bool FixedSize = false);

    private readonly Params _p;
    private double _fast, _slow, _atr, _prevClose, _extreme;
    private int _count, _dir, _blockedDir;
    private readonly Queue<double> _rets = new();

    public TrendFollowingStrategy(Params? p = null) => _p = p ?? new Params();
    public string Name => $"Trend EMA{_p.FastEma}/{_p.SlowEma} ATRstop{_p.StopAtr}" + (_p.AllowShort ? " L/S" : " LongOnly");
    public int WarmupBars => Math.Max(_p.SlowEma, _p.VolLookback) + 1;

    public Decision OnCandle(Candle c)
    {
        _count++;
        double af = 2.0 / (_p.FastEma + 1), asl = 2.0 / (_p.SlowEma + 1);
        if (_count == 1) { _fast = _slow = c.Close; }
        else { _fast += af * (c.Close - _fast); _slow += asl * (c.Close - _slow); }

        double tr = _count == 1 ? c.High - c.Low
            : Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - _prevClose), Math.Abs(c.Low - _prevClose)));
        _atr = _count == 1 ? tr : (_atr * (_p.AtrPeriod - 1) + tr) / _p.AtrPeriod;

        if (_prevClose > 0)
        {
            _rets.Enqueue(Math.Log(c.Close / _prevClose));
            if (_rets.Count > _p.VolLookback) _rets.Dequeue();
        }
        _prevClose = c.Close;

        if (_count < WarmupBars) return new Decision(0);

        bool up = _fast > _slow && c.Close > _slow;
        bool down = _fast < _slow && c.Close < _slow;
        if (_blockedDir == 1 && !up) _blockedDir = 0;     // re-arm only after trend condition resets
        if (_blockedDir == -1 && !down) _blockedDir = 0;

        if (_dir == 0)
        {
            if (up && _blockedDir != 1) { _dir = 1; _extreme = c.Close; }
            else if (down && _p.AllowShort && _blockedDir != -1) { _dir = -1; _extreme = c.Close; }
        }
        else if (_dir == 1)
        {
            _extreme = Math.Max(_extreme, c.Close);
            if (c.Close < _extreme - _p.StopAtr * _atr) { _dir = 0; _blockedDir = 1; }
            else if (!up) _dir = 0;
        }
        else
        {
            _extreme = Math.Min(_extreme, c.Close);
            if (c.Close > _extreme + _p.StopAtr * _atr) { _dir = 0; _blockedDir = -1; }
            else if (!down) _dir = 0;
        }

        if (_dir == 0) return new Decision(0);
        if (_p.FixedSize) return new Decision(_dir * _p.MaxWeight);   // fewer rebalances = lower fees
        double mean = _rets.Average();
        double sd = Math.Sqrt(_rets.Sum(r => (r - mean) * (r - mean)) / Math.Max(1, _rets.Count - 1));
        double vol = sd * Math.Sqrt(_p.BarsPerYear);
        double w = vol > 1e-9 ? Math.Min(_p.MaxWeight, _p.TargetVol / vol) : 0;
        return new Decision(_dir * w);
    }
}

public sealed class BuyAndHoldStrategy : IStrategy
{
    public string Name => "Buy & Hold";
    public int WarmupBars => 0;
    public Decision OnCandle(Candle c) => new(1);
}
