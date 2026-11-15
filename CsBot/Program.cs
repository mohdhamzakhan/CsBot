using CsBot;
using Microsoft.AspNetCore.HttpOverrides;

KeyStore.LoadIntoEnvironment();   // pick up saved API keys / app password (see KeyStore.cs) before anything needs them
Auth.Init();

// Web app:  dotnet run  ->  http://localhost:5080  (React UI + API)
string url = Environment.GetEnvironmentVariable("CS_URL") ?? "http://localhost:5080";
bool loopbackOnly = url.Split(';', StringSplitOptions.RemoveEmptyEntries).All(x =>
    x.Contains("localhost", StringComparison.OrdinalIgnoreCase) || x.Contains("127.0.0.1") || x.Contains("[::1]"));
if (!loopbackOnly && !Auth.Required)
{
    Console.Error.WriteLine($"REFUSING TO START: CS_URL={url} is reachable from other computers but no password is set.\nSet CS_APP_PASSWORD to a long password first (this app can place real orders with your API keys).");
    Environment.Exit(2);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TraderManager>();
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);   // trusts a reverse proxy on this same machine only
var app = builder.Build();
app.UseForwardedHeaders();
_ = Task.Run(() => SessionResume.AutoResumeAsync(app.Services.GetRequiredService<TraderManager>()));   // bring back a paper trader that was running

// Every API call needs a login when CS_APP_PASSWORD is set. If a proxy forwards traffic but no password is set, refuse (fail-safe against accidental exposure).
app.Use(async (ctx, next) =>
{
    string path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
    {
        if (Auth.Required)
        {
            if (!Auth.IsAuthed(ctx)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Please log in.", login = true }); return; }
        }
        else if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") || ctx.Request.Headers.ContainsKey("X-Original-For"))   // UseForwardedHeaders moves the header to X-Original-For
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "This app is being reached through a proxy but no password is set. Set CS_APP_PASSWORD on the server first." }); return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAuth();
app.MapApi();
app.MapResearch();
app.MapFallbackToFile("index.html");
app.Run(url);
