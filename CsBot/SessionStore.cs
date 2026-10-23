using System.Text.Json;

namespace CsBot;

/// <summary>The money/position bookkeeping of an executor, so a trader can carry on after a restart.</summary>
public sealed record ExecutorState(Dictionary<string, double> Num, List<Fill> Fills);

/// <summary>Everything needed to resume a trader: how it was started (without the confirmation phrase) plus its bookkeeping.</summary>
public sealed record SessionFile(int Version, string Mode, string RequestJson, ExecutorState Exec, bool WasRunning, DateTime SavedAt, long? PosOpenedAt, long? LastBar);

/// <summary>Human-readable record of the currently open position. Exists ONLY while a position is open.</summary>
public sealed record PositionFile(string Mode, string Market, string Symbol, string Strategy, int IntervalMin, string Side, double Units,
    double EntryPrice, double Notional, long OpenedAt, DateTime Updated);

/// <summary>
/// JSON files under ./data/state:
///   open-position.json   the open position (deleted as soon as it is closed - by the strategy, the kill switch or "Close position")
///   trader-session.json  the resumable trader (deleted when you stop it with no position open, or discard it)
/// Writes are atomic (temp file then replace) so a crash cannot leave half a file.
/// </summary>
public static class SessionStore
{
    private static readonly object L = new();
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    private static string Dir { get { var d = Path.Combine("data", "state"); Directory.CreateDirectory(d); return d; } }
    public static string SessionPath => Path.Combine(Dir, "trader-session.json");
    public static string PositionPath => Path.Combine(Dir, "open-position.json");

    private static void WriteAtomic(string path, string json)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
    }

    public static void SaveSession(SessionFile s) { lock (L) WriteAtomic(SessionPath, JsonSerializer.Serialize(s, Opt)); }
    public static void SavePosition(PositionFile p) { lock (L) WriteAtomic(PositionPath, JsonSerializer.Serialize(p, Opt)); }
    public static void DeleteSession() { lock (L) if (File.Exists(SessionPath)) File.Delete(SessionPath); }
    public static void DeletePosition() { lock (L) if (File.Exists(PositionPath)) File.Delete(PositionPath); }

    public static SessionFile? LoadSession()
    {
        lock (L)
        {
            try { return File.Exists(SessionPath) ? JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(SessionPath)) : null; }
            catch { return null; }
        }
    }
    public static PositionFile? LoadPosition()
    {
        lock (L)
        {
            try { return File.Exists(PositionPath) ? JsonSerializer.Deserialize<PositionFile>(File.ReadAllText(PositionPath)) : null; }
            catch { return null; }
        }
    }
}