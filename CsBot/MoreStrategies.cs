namespace CsBot;

/// <summary>Annualised realised volatility -> position weight so risk stays roughly constant.</summary>
public sealed class VolTarget
{
    private readonly Queue<double> _rets = new(); private readonly int _n; private double _prev;
    public VolTarget(int lookback) => _n = lookback;
    public void Update(double close)
    {
        if (_prev > 0) { _rets.Enqueue(Math.Log(close / _prev)); if (_rets.Count > _n) _rets.Dequeue(); }
        _prev = close;
    }
    public double Weight(double targetVol, double barsPerYear, double maxW)
    {
        if (_rets.Count < 5) return 0;
        double m = _rets.Average(), sd = Math.Sqrt(_rets.Sum(r => (r - m) * (r - m)) / Math.Max(1, _rets.Count - 1));
        double vol = sd * Math.Sqrt(barsPerYear);
        return vol > 1e-9 ? Math.Min(maxW, targetVol / vol) : 0;
    }
}

/// <summary>Donchian channel breakout (Turtle-style): enter on N-bar high, exit on M-bar low. Mirror for shorts.</summary>
public sealed class DonchianStrategy : IStrategy
{
    private readonly int _entry, _exit; private readonly double _targetVol, _bpy, _maxW; private readonly bool _short, _fixed;
    private readonly List<double> _h = new(), _l = new(); private readonly VolTarget _vt; private int _dir;
    public DonchianStrategy(int entry, int exit, double targetVol, double barsPerYear, double maxW, bool allowShort, bool fixedSize)
    { _entry = entry; _exit = exit; _targetVol = targetVol; _bpy = barsPerYear; _maxW = maxW; _short = allowShort; _fixed = fixedSize; _vt = new VolTarget(60); }
    public string Name => $"Donchian {_entry}/{_exit}" + (_short ? " L/S" : " LongOnly");
    public int WarmupBars => Math.Max(_entry, _exit) + 2;

    public Decision OnCandle(Candle c)
    {
        _vt.Update(c.Close);
        double hiE = double.NaN, loE = double.NaN, hiX = double.NaN, loX = double.NaN;
        if (_h.Count >= Math.Max(_entry, _exit))
        {
            hiE = Max(_h, _entry); loE = Min(_l, _entry); hiX = Max(_h, _exit); loX = Min(_l, _exit);
        }
        _h.Add(c.High); _l.Add(c.Low);
        if (double.IsNaN(hiE)) return new Decision(0);
        if (_dir == 0) { if (c.Close > hiE) _dir = 1; else if (_short && c.Close < loE) _dir = -1; }
        else if (_dir == 1 && c.Close < loX) _dir = 0;
        else if (_dir == -1 && c.Close > hiX) _dir = 0;
        if (_dir == 0) return new Decision(0);
        double w = _fixed ? _maxW : _vt.Weight(_targetVol, _bpy, _maxW);
        return new Decision(_dir * w);
    }
    private static double Max(List<double> x, int n) { double m = double.MinValue; for (int i = x.Count - n; i < x.Count; i++) m = Math.Max(m, x[i]); return m; }
    private static double Min(List<double> x, int n) { double m = double.MaxValue; for (int i = x.Count - n; i < x.Count; i++) m = Math.Min(m, x[i]); return m; }
}

/// <summary>Time-series momentum: long when the past N-bar return is positive (short when negative, futures only).</summary>
public sealed class TsMomentumStrategy : IStrategy
{
    private readonly int _look; private readonly double _targetVol, _bpy, _maxW, _band; private readonly bool _short;
    private readonly List<double> _c = new(); private readonly VolTarget _vt; private int _dir;
    public TsMomentumStrategy(int lookback, double bandPct, double targetVol, double barsPerYear, double maxW, bool allowShort)
    { _look = lookback; _band = bandPct / 100; _targetVol = targetVol; _bpy = barsPerYear; _maxW = maxW; _short = allowShort; _vt = new VolTarget(60); }
    public string Name => $"TS-Momentum {_look}" + (_short ? " L/S" : " LongOnly");
    public int WarmupBars => _look + 2;

    public Decision OnCandle(Candle c)
    {
        _c.Add(c.Close); _vt.Update(c.Close);
        if (_c.Count <= _look) return new Decision(0);
        double ret = c.Close / _c[_c.Count - 1 - _look] - 1;
        // Hysteresis band stops flip-flopping around zero (which costs fees + TDS lock-up).
        if (ret > _band) _dir = 1; else if (ret < -_band) _dir = _short ? -1 : 0;
        else if (_dir == 1 && ret < 0) _dir = 0; else if (_dir == -1 && ret > 0) _dir = 0;
        return _dir == 0 ? new Decision(0) : new Decision(_dir * _vt.Weight(_targetVol, _bpy, _maxW));
    }
}
