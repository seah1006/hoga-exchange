using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Hoga.Server.Operations;

internal static class OperationsRegistration
{
    private static readonly object DirectLoopback = new();

    public static void AddOperations(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsProduction())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                o.UseUtcTimestamp = true;
                o.IncludeScopes = false;
            });
            // Framework diagnostics can emit raw URLs or exceptions; allow only known-safe console sources.
            builder.Services.PostConfigure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.AddFilter((category, level) => level >= LogLevel.Information &&
                    (category == typeof(SafeRequestLog).FullName || category == typeof(MarketLoop).FullName ||
                     category == typeof(BackupService).FullName));
            });
        }
        builder.Services.AddSingleton<MarketMetrics>();
        builder.Services.Configure<HubOptions<MarketHub>>(o => o.AddFilter<ConnectionMetrics>());
        builder.Services.AddSingleton<ISqliteSnapshot, OfflineFileSnapshot>(); // TASK-011: replace with online implementation.
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<IRoundBackupTrigger>(sp => sp.GetRequiredService<BackupService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
    }

    public static void CaptureMetricsPeer(this WebApplication app) => app.Use(async (context, next) =>
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        if (ip is not null && IPAddress.IsLoopback(ip) && !context.Request.Headers.ContainsKey("X-Forwarded-For") &&
            !context.Request.Headers.ContainsKey("Forwarded")) context.Items[DirectLoopback] = true;
        await next(context);
    });

    public static void MapOperations(this WebApplication app) => app.MapGet("/metrics",
        (HttpContext context, IConfiguration config, MarketMetrics metrics) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return context.Items.ContainsKey(DirectLoopback) || IsAdmin(context, config)
                ? Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8")
                : Results.StatusCode(StatusCodes.Status401Unauthorized);
        });

    public static bool IsAdmin(HttpContext context, IConfiguration configuration)
    {
        var key = configuration["Market:AdminKey"];
        return !string.IsNullOrWhiteSpace(key) && context.Request.Headers.TryGetValue("X-Admin-Key", out var headers) &&
            headers.Count == 1 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(headers[0]!)));
    }
}

internal sealed class SafeRequestLog(RequestDelegate next, ILogger<SafeRequestLog> logger, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        int? failed = null;
        try { await next(context); }
        catch (Exception) when (environment.IsProduction())
        {
            failed = context.RequestAborted.IsCancellationRequested ? 499 : 500;
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = failed.Value;
            }
            else context.Abort();
        }
        finally
        {
            // Route templates are developer-controlled. Never log raw paths or any request data.
            var path = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
            var id = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var user = id is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 6)); // 임의 지정: 12 hex
            logger.LogInformation("HTTP {Path} returned {StatusCode} in {ElapsedMs} ms (user {UserHash})",
                path, failed ?? context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds, user);
        }
    }
}

internal sealed class ConnectionMetrics(MarketMetrics metrics) : IHubFilter
{
    private static readonly object Counted = new();

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        metrics.Connect();
        context.Context.Items[Counted] = true;
        try { await next(context); }
        catch { Disconnect(context); throw; }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        Disconnect(context);
        await next(context, exception);
    }

    private void Disconnect(HubLifetimeContext context)
    {
        if (context.Context.Items.Remove(Counted)) metrics.Disconnect();
    }
}
