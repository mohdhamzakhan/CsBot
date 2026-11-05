using System.Text.Json;
namespace CsBot;

public static class FeeTools
{
    /// <summary>Finds taker/maker fee for a coin anywhere in the response and returns percentages (the API gives fractions: 0.0004 = 0.04%).</summary>
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
