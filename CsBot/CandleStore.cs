using System.Globalization;
using System.Text;
namespace CsBot;

/// <summary>Disk cache of CLOSED candles under ./data so charts/backtests don't re-download history every time.</summary>
public static class CandleStore
{
    public static string PathFor(Market m, string symbol, int intervalMin)
    {
        Directory.CreateDirectory("data");
        string safe = new string(symbol.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Path.Combine("data", $"{m.ToString().ToLowerInvariant()}_{safe}_{intervalMin}.csv");
    }

    public static async Task<List<Candle>> GetAsync(Market m, string exchange, string symbol, int intervalMin, int days, bool refresh,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        string file = PathFor(m, symbol, intervalMin);
        var cached = File.Exists(file) ? Load(file) : new List<Candle>();
        var now = DateTime.UtcNow; var wantFrom = now.AddDays(-days);
        var step = TimeSpan.FromMinutes(intervalMin);

        bool needFull = cached.Count == 0 || cached[0].Time > wantFrom + step * 3;
        bool stale = cached.Count == 0 || cached[^1].Time + step * 2 <= now;
        if ((needFull || stale || refresh) && CoinSwitchClient.HasEnvKeys)
        {
            var cs = CoinSwitchClient.Shared;
            var from = needFull || refresh ? wantFrom : cached[^1].Time;
            var fresh = await cs.GetCandlesAsync(m, exchange, symbol, intervalMin, from, now, progress, ct);
            var map = cached.ToDictionary(c => c.Time);
            foreach (var c in fresh) map[c.Time] = c;
            cached = map.Values.OrderBy(c => c.Time).ToList();
            cached.RemoveAll(c => c.Time + step > now);       // only closed bars
            Save(file, cached);
        }
        else if (cached.Count == 0) throw new InvalidOperationException("No cached candles and CS_API_KEY / CS_SECRET_KEY are not set.");
        return cached.Where(c => c.Time >= wantFrom).ToList();
    }

    public static List<Candle> Load(string file)
    {
        var list = new List<Candle>();
        foreach (var line in File.ReadLines(file))
        {
            var f = line.Split(',');
            if (f.Length < 6 || !long.TryParse(f[0], out var ms)) continue;
            double P(int k) => double.Parse(f[k], CultureInfo.InvariantCulture);
            list.Add(new Candle(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, P(1), P(2), P(3), P(4), P(5)));
        }
        return list;
    }

    public static void Save(string file, IEnumerable<Candle> candles)
    {
        var sb = new StringBuilder("time,open,high,low,close,volume\n");
        foreach (var c in candles)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{new DateTimeOffset(c.Time, TimeSpan.Zero).ToUnixTimeMilliseconds()},{c.Open},{c.High},{c.Low},{c.Close},{c.Volume}"));
        File.WriteAllText(file, sb.ToString());
    }
}
