namespace CsBot;

/// <summary>
/// Smallest futures order CoinSwitch accepts, per symbol, in units of the coin (as reported by the app owner; verify in the CoinSwitch app and edit here if it changes).
/// Decimals = quantity decimals that fit the minimum (0.002 needs 3, 0.02 and 0.23 need 2). Unknown symbols and spot return null (no check).
/// </summary>
public static class MinOrders
{
    private static readonly Dictionary<string, (double MinQty, int Decimals)> Futures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTCUSDT"] = (0.002, 3),
        ["ETHUSDT"] = (0.02, 2),
        ["SOLUSDT"] = (0.23, 2),
    };

    public static (double MinQty, int Decimals)? For(Market market, string symbol) =>
        market == Market.Futures && Futures.TryGetValue(symbol.Replace("/", ""), out var v) ? v : null;
}