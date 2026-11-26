using System.Text.Json;
using System.Threading.RateLimiting;
using Hoga.Bots;
using Hoga.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<MarketOptions>().BindConfiguration("Market")
    .Validate(o => o.Speed is >= 1 and <= 1000 && o.RequestTimeoutMs > 0 && o.BasePrice > 0 &&
        o.BotIntensity is >= 0 and <= 1 && (string.IsNullOrEmpty(o.DailyResetLocalTime) ||
            MarketOptions.TryParseResetTime(o.DailyResetLocalTime, out _)) &&
        o.MaxOrderQuantity is >= 1 and <= 1_000_000_000 && !string.IsNullOrWhiteSpace(o.JournalPath), "시장 설정이 올바르지 않습니다.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, config) =>
{
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // No configured network means no header is trusted (empty allowlists alone would trust everyone).
    if (config["Market:TrustedProxyNetwork"] is not { Length: > 0 } cidr) return;
    if (!System.Net.IPNetwork.TryParse(cidr, out var network) ||
        !new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Select(System.Net.IPNetwork.Parse)
            .Any(range => network.PrefixLength >= range.PrefixLength && range.Contains(network.BaseAddress)))
        throw new InvalidOperationException("Market:TrustedProxyNetwork must be a private IPv4 Docker subnet.");
    options.KnownIPNetworks.Add(network);
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1; // Only the direct Caddy hop; never walk arbitrary client-supplied chains.
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, ct) => new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
        new { error = "TooManyRequests" }, ct));
    options.AddPolicy("orders", context => PerIp(context, TimeSpan.FromSeconds(1),
        context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Market:OrdersPerSecond", 20))); // 임의 지정
    options.AddPolicy("admin", context => PerIp(context, TimeSpan.FromMinutes(1), 10)); // 임의 지정
});
builder.Services.AddSingleton(new ExperimentSummaryFile(Path.Combine(AppContext.BaseDirectory, "summary.json")));
builder.Services.AddSingleton(services => new ExperimentCache(
    TimeSpan.FromMilliseconds(services.GetRequiredService<IOptions<MarketOptions>>().Value.RequestTimeoutMs),
    (seed, scenario) => ExperimentRunner.RunExperiment(seed, scenario).Experiment));
builder.Services.AddSingleton<MarketLoop>();
builder.Services.AddHostedService(services => services.GetRequiredService<MarketLoop>());
builder.AddMembers();
var app = builder.Build();
if (await app.InitializeMembers(args)) return;
app.UseWhen(context => context.Connection.RemoteIpAddress is not null, branch => branch.UseForwardedHeaders());
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (MarketUnavailableException) when (!context.Response.HasStarted)
    {
        await Error(503, "ServiceUnavailable", "시장 처리 루프를 사용할 수 없습니다.").ExecuteAsync(context);
    }
    catch (TimeoutException) when (!context.Response.HasStarted)
    {
        await Error(504, "Timeout", "처리 결과를 기다리는 시간이 초과되었습니다. 이미 시작한 요청은 적용될 수 있습니다.")
            .ExecuteAsync(context);
    }
});

var localDist = Path.GetFullPath("../../web/dist", app.Environment.ContentRootPath);
if (!Directory.Exists(app.Environment.WebRootPath) && Directory.Exists(localDist))
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(localDist);
    app.Lifetime.ApplicationStopped.Register(files.Dispose);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapGet("/healthz", async Task<IResult> (MarketLoop market, CancellationToken abort) =>
{
    try { return Results.Ok(new { ok = true, seq = await market.Health(abort) }); }
    catch (Exception ex) when (ex is MarketUnavailableException or TimeoutException)
    { return Results.Json(new { ok = false }, statusCode: 503); }
});

app.MapMarketWrites();
app.MapMembers();
app.MapAccounts();
app.MapGet("/api/state/hash", async (MarketLoop market, CancellationToken abort) => Results.Ok(await market.Hash(abort)));
app.MapGet("/api/experiments/summary", (ExperimentSummaryFile summary) =>
{
    try
    {
        return Results.Stream(File.OpenRead(summary.Path), "application/json; charset=utf-8");
    }
    catch (IOException failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
    {
        return Error(503, "ServiceUnavailable", "실험 요약 파일을 사용할 수 없습니다.");
    }
});
app.MapGet("/api/experiments/{id}", async (string id, ExperimentCache experiments, CancellationToken abort) =>
{
    try
    {
        return await experiments.Get(id, abort) is { } result
            ? Results.Ok(result) : Error(404, "NotFound", "실험을 찾을 수 없습니다.");
    }
    catch (TimeoutException)
    {
        return Error(503, "ServiceUnavailable", "실험 결과를 기다리는 시간이 초과되었습니다.");
    }
});
app.MapHub<MarketHub>("/hubs/market");
app.Run();

static RateLimitPartition<string> PerIp(HttpContext context, TimeSpan window, int permits)
{
    var ip = context.Connection.RemoteIpAddress;
    if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
    return RateLimitPartition.GetFixedWindowLimiter(ip?.ToString() ?? "unknown", _ => new()
    {
        PermitLimit = Math.Max(1, permits), Window = window, QueueLimit = 0
    });
}

static IResult Error(int status, string error, string message) => Results.Json(new { error, message }, statusCode: status);
internal sealed record ExperimentSummaryFile(string Path);
public partial class Program;
