using System.Text.Json;

namespace CsBot;

public sealed record ResumeReq(string? Confirm);

public static class TraderFactory
{
    public const string Phrase = "I UNDERSTAND THE RISKS";

    /// <summary>The request as saved to disk: everything except the typed confirmation phrase.</summary>
    public static string ToJson(TraderReq r) => JsonSerializer.Serialize(r with { Confirm = null });

    public static Trader Build(TraderReq r, string requestJson, ExecutorState? restore = null, long? posOpenedAt = null)
    {
        var m = Api.ParseMarket(r.Data.Market);
        var cost = Api.BuildCost(m, r.Costs);
        double bpy = 365.25 * 1440 / r.Data.IntervalMin;
        var (factory, band) = StrategyRegistry.Create(r.Strategy.Id, r.Strategy.Params, r.Strategy.Rules, m, bpy, cost.MaxLeverage, r.Strategy.Code);
        var risk = r.Risk ?? new RiskIn(0, 0, 1, 0);
        var cfg = new TraderConfig
        {
            Mode = r.Mode == "live" ? "live" : "paper",
            Market = m,
            Exchange = r.Data.Exchange,
            Symbol = r.Data.Symbol,
            IntervalMin = r.Data.IntervalMin,
            StrategyId = r.Strategy.Id,
            Params = r.Strategy.Params,
            Rules = r.Strategy.Rules,
            Code = r.Strategy.Code,
            Cost = cost,
            Capital = r.Capital,
            Risk = new RiskLimits(risk.MaxOrderNotional, risk.MaxPositionNotional, risk.MaxLeverage, risk.DailyLossPct),
            PricePrecision = r.PricePrecision ?? 0,
            QtyPrecision = r.QtyPrecision ?? 5
        };
        var cs = CoinSwitchClient.FromEnv();
        if (!cs.KeyMatchesSecret) { cs.Dispose(); throw new InvalidOperationException("Your secret key does not match your API key."); }
        return new Trader(cfg,
            (n, ct) => cs.GetCandlesAsync(m, cfg.Exchange, cfg.Symbol, cfg.IntervalMin, DateTime.UtcNow.AddMinutes(-(n + 2.0) * cfg.IntervalMin), DateTime.UtcNow, null, ct),
            cs, factory, band, null, requestJson, restore, posOpenedAt)
        { Owned = cs };
    }
}

/// <summary>Finds a trader that was running (or holding a position) when the app last closed, and brings it back.</summary>
public static class SessionResume
{
    public static object Describe(TraderManager mgr)
    {
        if (mgr.Current is { IsRunning: true }) return new { exists = false };
        var s = SessionStore.LoadSession();
        if (s == null) return new { exists = false };
        TraderReq? req = null;
        try { req = JsonSerializer.Deserialize<TraderReq>(s.RequestJson); } catch { }
        if (req == null) return new { exists = false };
        var pos = SessionStore.LoadPosition();
        return new
        {
            exists = true,
            mode = s.Mode,
            market = req.Data.Market,
            symbol = req.Data.Symbol,
            strategy = req.Strategy.Id,
            intervalMin = req.Data.IntervalMin,
            wasRunning = s.WasRunning,
            savedAt = s.SavedAt,
            hasPosition = pos != null,
            side = pos?.Side,
            units = pos?.Units,
            entryPrice = pos?.EntryPrice,
            openedAt = pos?.OpenedAt,
            requiresConfirm = s.Mode == "live"
        };
    }

    public static Trader Resume(TraderManager mgr, string? confirm)
    {
        var s = SessionStore.LoadSession() ?? throw new InvalidOperationException("There is no saved session to resume.");
        var req = JsonSerializer.Deserialize<TraderReq>(s.RequestJson) ?? throw new InvalidOperationException("The saved session file is damaged.");
        if (!CoinSwitchClient.HasEnvKeys) throw new InvalidOperationException("Set your CoinSwitch API keys first.");
        if (s.Mode == "live")
        {
            if (Environment.GetEnvironmentVariable("CS_LIVE_ENABLED") != "1") throw new InvalidOperationException("Live trading is disabled on this server (CS_LIVE_ENABLED is not 1).");
            if (confirm != TraderFactory.Phrase) throw new ArgumentException($"Type the confirmation phrase exactly: {TraderFactory.Phrase}");
        }
        var t = TraderFactory.Build(req, s.RequestJson, s.Exec, s.PosOpenedAt);
        mgr.Start(t);
        return t;
    }

    /// <summary>At app start: a PAPER trader that was running is resumed automatically. Live traders always wait for you to confirm.</summary>
    public static async Task AutoResumeAsync(TraderManager mgr)
    {
        try
        {
            await Task.Delay(1500);
            var s = SessionStore.LoadSession();
            if (s == null || !s.WasRunning || s.Mode != "paper" || !CoinSwitchClient.HasEnvKeys) return;
            Resume(mgr, null);
            Console.WriteLine("Resumed the paper trader from the last session.");
        }
        catch (Exception e) { Console.WriteLine("Could not resume the saved paper trader: " + e.Message); }
    }
}