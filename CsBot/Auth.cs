using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CsBot;

/// <summary>
/// Optional single-password login for when the app is reachable from the internet.
/// Enabled by setting CS_APP_PASSWORD. Session = HttpOnly, SameSite=Strict cookie holding "expiry.HMAC".
/// </summary>
public static class Auth
{
    public const string Cookie = "csbot_session";
    private static byte[]? _pw, _key;
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> _throttle = new();
    public static bool Required => _pw != null;

    public static void Init()
    {
        string? pw = Environment.GetEnvironmentVariable("CS_APP_PASSWORD");
        if (string.IsNullOrEmpty(pw)) { _pw = _key = null; return; }
        _pw = SHA256.HashData(Encoding.UTF8.GetBytes(pw));
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("csbot-session-v1"), Encoding.UTF8.GetBytes(pw));
    }

    public static bool PasswordOk(string? attempt) =>
        _pw != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(attempt ?? "")), _pw);

    private static string Sign(long exp) => Convert.ToBase64String(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(exp.ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewToken(TimeSpan life)
    {
        long exp = DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds();
        return exp + "." + Sign(exp);
    }

    public static bool IsAuthed(HttpContext c)
    {
        if (_key == null) return true;
        if (!c.Request.Cookies.TryGetValue(Cookie, out var tok) || tok is null) return false;
        int dot = tok.IndexOf('.');
        if (dot < 1 || !long.TryParse(tok[..dot], out long exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tok[(dot + 1)..]), Encoding.UTF8.GetBytes(Sign(exp)));
    }

    /// <summary>Returns seconds to wait if this client is locked out, else 0.</summary>
    public static int LockedFor(string ip) =>
        _throttle.TryGetValue(ip, out var t) && t.Until > DateTime.UtcNow ? (int)(t.Until - DateTime.UtcNow).TotalSeconds + 1 : 0;
    public static void Failed(string ip) =>
        _throttle.AddOrUpdate(ip, _ => (1, DateTime.MinValue), (_, t) => t.Fails + 1 >= 5 ? (0, DateTime.UtcNow.AddMinutes(10)) : (t.Fails + 1, t.Until));
    public static void Succeeded(string ip) => _throttle.TryRemove(ip, out _);

    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/auth/status", (HttpContext c) => new { required = Required, authed = IsAuthed(c) });
        app.MapPost("/api/auth/login", (LoginReq r, HttpContext c) =>
        {
            if (!Required) return Results.Ok(new { ok = true });
            string ip = c.Connection.RemoteIpAddress?.ToString() ?? "?";
            int wait = LockedFor(ip);
            if (wait > 0) return Results.Json(new { error = $"Too many wrong passwords. Try again in {(wait + 59) / 60} minute(s)." }, statusCode: 429);
            if (!PasswordOk(r.Password)) { Failed(ip); return Results.Json(new { error = "Wrong password." }, statusCode: 401); }
            Succeeded(ip);
            var life = TimeSpan.FromHours(12);
            c.Response.Cookies.Append(Cookie, NewToken(life), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, Expires = DateTimeOffset.UtcNow.Add(life) });
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/api/auth/logout", (HttpContext c) => { c.Response.Cookies.Delete(Cookie); return Results.Ok(new { ok = true }); });
    }
}

public sealed record LoginReq(string Password);
