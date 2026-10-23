using System.Text.Json;
namespace CsBot;

public static class FeeTools
{
    /// <summary>Prints the raw /tradingFee response for your account so you can read your real maker/taker rate.</summary>
    public static async Task RunAsync(Dictionary<string, string> o)
    {
        using var cs = CoinSwitchClient.FromEnv();
        string ex = o.GetValueOrDefault("exchange", "coinswitchx");
        using var d = await cs.GetAsync("/trade/api/v2/tradingFee", new List<KeyValuePair<string, string>> { new("exchange", ex) });
        Console.WriteLine(JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("\nUse the taker rate in backtests, e.g.:  dotnet run -- cli --csv btc.csv --fee 0.04 --compare");
    }

    /// <summary>Finds taker/maker fee for a coin anywhere in the response and returns percentages (API gives fractions: 0.0004 = 0.04%).</summary>
    public static (double? TakerPct, double? MakerPct) FindCoinFee(JsonElement root, string coin)
    {
        JsonElement? hit = Find(root, coin);
        if (hit == null) return (null, null);
        double? Get(string n) => hit.Value.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDouble() * 100 : null;
        return (Get("taker_fee_after_discount") ?? Get("taker_fee"), Get("maker_fee_after_discount") ?? Get("maker_fee"));
    }
    private static JsonElement? Find(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object) return p.Value;
                var r = Find(p.Value, name); if (r != null) return r;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) { var r = Find(i, name); if (r != null) return r; }
        return null;
    }
}
