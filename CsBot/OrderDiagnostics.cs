using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsBot;

/// <summary>An order (or order-related call) failed; the message already contains the full explanation and has been logged.</summary>
public sealed class OrderException : Exception
{
    public OrderException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Explains in plain words why an exchange call failed: what was attempted, what CoinSwitch answered, and the most likely cause.</summary>
public static class OrderDiagnostics
{
    private static readonly string[] Keys = { "message", "msg", "error_message", "error", "detail", "reason", "description" };

    public static string Explain(Exception e, string what, string label = "ORDER FAILED")
    {
        int? status = null; string body = e.Message;
        var m = Regex.Match(e.Message, @"^HTTP (\d{3}):\s*(.*)$", RegexOptions.Singleline);
        if (m.Success) { status = int.Parse(m.Groups[1].Value); body = m.Groups[2].Value; }

        bool timeout = e is TaskCanceledException or TimeoutException || e.InnerException is TimeoutException;
        bool network = status == null && !timeout && e is HttpRequestException;
        string api = timeout ? "no answer from CoinSwitch (timed out)" : Pull(body);
        string hint = Hint(status, timeout, network, (api + " " + body).ToLowerInvariant());
        return $"{label}: {what} -> {(status != null ? $"HTTP {status}: " : "")}{api}{(hint.Length > 0 ? " | LIKELY CAUSE: " + hint : "")}";
    }

    private static string Pull(string body)
    {
        body = body.Trim();
        try
        {
            using var d = JsonDocument.Parse(body);
            var s = Find(d.RootElement);
            if (!string.IsNullOrWhiteSpace(s)) return Trunc(s!);
        }
        catch (JsonException) { /* not JSON: show the raw text */ }
        return Trunc(body.Length == 0 ? "(empty response)" : body);
    }

    private static string? Find(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in Keys)
                foreach (var p in e.EnumerateObject())
                    if (p.Name.Equals(k, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            foreach (var p in e.EnumerateObject()) { var r = Find(p.Value); if (r != null) return r; }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) { var r = Find(i); if (r != null) return r; }
        return null;
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "...";

    private static string Hint(int? status, bool timeout, bool network, string t)
    {
        if (timeout || network)
            return "network problem or timeout. The order may or may not have reached CoinSwitch: check the CoinSwitch app (open orders and position) before restarting.";
        if (status is >= 500)
            return "CoinSwitch server error. The order may or may not have been accepted: check the CoinSwitch app before restarting.";
        if (status == 429) return "rate limit hit (too many requests). Wait a minute; the bot does not retry this order by itself.";
        if (status == 401) return "CoinSwitch rejected the API key or signature. Re-enter the keys in the menu, and check whether this machine's IP address is allowed in the key's IP restrictions.";
        if (status == 403) return "the API key lacks trading permission, or this machine's IP address is not whitelisted for the key.";

        if (t.Contains("insufficient") || t.Contains("not enough") || t.Contains("balance") || t.Contains("margin"))
            return "not enough free balance or margin for this order. Check the Wallet tab (open orders also lock funds).";
        if (t.Contains("min") && (t.Contains("quantity") || t.Contains("qty") || t.Contains("notional") || t.Contains("value") || t.Contains("size") || t.Contains("amount")))
            return "the order is below the exchange's minimum size. Raise the capital or order size.";
        if (t.Contains("precision") || t.Contains("decimal") || t.Contains("tick size") || t.Contains("step size") || t.Contains("lot size"))
            return "price or quantity has more decimals than the exchange allows. Adjust 'Price decimals' / 'Quantity decimals' in the live settings.";
        if (t.Contains("leverage")) return "the leverage value is not allowed for this symbol. Lower 'Max leverage' in the live settings.";
        if (t.Contains("reduce")) return "a reduce-only order conflicts with the real position (there may be nothing to reduce). Check the position in the CoinSwitch app.";
        if (t.Contains("timestamp") || t.Contains("epoch") || t.Contains("clock") || t.Contains("expired"))
            return "this machine's clock is out of sync with CoinSwitch. Fix the system time.";
        if (t.Contains("symbol") || t.Contains("pair") || t.Contains("instrument"))
            return "the symbol or exchange name is not recognised. Check the symbol (spot like BTC/INR, futures like BTCUSDT) and the exchange.";
        if (status is 400 or 422) return "CoinSwitch rejected the order as invalid (see its message above).";
        return "";
    }
}