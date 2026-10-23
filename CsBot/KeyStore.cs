namespace CsBot;

/// <summary>
/// Remembers your CoinSwitch API keys between runs. Lookup order:
///   1. environment variables CS_API_KEY / CS_SECRET_KEY (if you set them yourself)
///   2. ./keys.env in the folder you run the app from
///   3. keys.env in your user folder:  %USERPROFILE%\.csbot\keys.env   (where the app saves keys you enter in the UI)
/// File format: one KEY=VALUE per line. Only the two key names are read from a file; CS_LIVE_ENABLED is deliberately
/// NOT loadable from a file, so real-money trading always needs a conscious environment variable.
/// </summary>
public static class KeyStore
{
    private static readonly string[] Allowed = { "CS_API_KEY", "CS_SECRET_KEY", "CS_APP_PASSWORD" };

    public static string DefaultPath
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = AppContext.BaseDirectory;
            return Path.Combine(home, ".csbot", "keys.env");
        }
    }
    public static IEnumerable<string> SearchPaths => new[] { Path.Combine(Directory.GetCurrentDirectory(), "keys.env"), DefaultPath };

    /// <summary>"environment", a file path, or null when no keys are configured.</summary>
    public static string? Source { get; private set; }

    public static void LoadIntoEnvironment()
    {
        bool envHasBoth = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS_API_KEY")) &&
                          !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS_SECRET_KEY"));
        Source = envHasBoth ? "environment" : null;
        foreach (var path in SearchPaths)
        {
            if (!File.Exists(path)) continue;
            var found = Parse(File.ReadAllLines(path));
            foreach (var k in Allowed)   // real environment variables always win over the file
                if (found.TryGetValue(k, out var v) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(k))) Environment.SetEnvironmentVariable(k, v);
            if (Source == null && found.ContainsKey("CS_API_KEY") && found.ContainsKey("CS_SECRET_KEY")) Source = path;
        }
    }

    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string k = line[..i].Trim(), v = line[(i + 1)..].Trim().Trim('"', '\'');
            if (Allowed.Contains(k) && v.Length > 0) d[k] = v;
        }
        return d;
    }

    public static void Save(string apiKey, string secret)
    {
        string path = DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var keep = File.Exists(path) ? File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("CS_API_KEY=") && !l.TrimStart().StartsWith("CS_SECRET_KEY=") && !l.StartsWith("# CoinSwitch API keys")).ToList() : new List<string>();
        var lines = new List<string> { "# CoinSwitch API keys for CsBot. Keep this file private; never share or upload it.", $"CS_API_KEY={apiKey}", $"CS_SECRET_KEY={secret}" };
        lines.AddRange(keep);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable("CS_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("CS_SECRET_KEY", secret);
        Source = path;
        CoinSwitchClient.ResetShared();
    }

    /// <summary>Deletes the saved file (only keys that came from a file; environment variables are left alone).</summary>
    public static bool Remove()
    {
        bool removed = false;
        foreach (var p in SearchPaths) if (File.Exists(p)) { File.Delete(p); removed = true; }
        if (Source != "environment")
        {
            Environment.SetEnvironmentVariable("CS_API_KEY", null); Environment.SetEnvironmentVariable("CS_SECRET_KEY", null);
            Source = null; CoinSwitchClient.ResetShared();
        }
        return removed;
    }

    public static string Mask(string? key) => string.IsNullOrEmpty(key) || key.Length < 12 ? "" : key[..6] + "..." + key[^4..];
}
