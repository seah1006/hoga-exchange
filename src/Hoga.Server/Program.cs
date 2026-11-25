using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoga.Engine;
using Hoga.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<MarketOptions>().BindConfiguration("Market")
    .Validate(o => o.Speed is >= 1 and <= 1000 && o.RequestTimeoutMs > 0 && o.BasePrice > 0 &&
        o.MaxOrderQuantity is >= 1 and <= 1_000_000_000 && !string.IsNullOrWhiteSpace(o.JournalPath), "시장 설정이 올바르지 않습니다.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();
builder.Services.AddSingleton<MarketLoop>();
builder.Services.AddHostedService(services => services.GetRequiredService<MarketLoop>());
var app = builder.Build();

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

app.MapPost("/api/orders", async (HttpContext context, MarketLoop market, IOptions<MarketOptions> options) =>
{
    var abort = context.RequestAborted;
    PlaceRequest? order;
    if (!context.Request.HasJsonContentType() ||
        context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        return Error(400, "BadRequest", "JSON 주문 본문이 필요합니다.");
    try { order = await context.Request.ReadFromJsonAsync<PlaceRequest>(abort); }
    catch (JsonException) { return Error(400, "BadRequest", "side, price, quantity 형식을 확인해 주세요."); }
    catch (BadHttpRequestException) { return Error(400, "BadRequest", "주문 본문을 읽을 수 없습니다."); }
    if (order is not { Price: long price, Quantity: long quantity } || order.Side is not ("Buy" or "Sell"))
        return Error(400, "BadRequest", "side는 Buy 또는 Sell이며 price와 quantity는 정수여야 합니다.");
    // 저널 기록 전에 막는다. 0 이하 수량은 엔진의 InvalidQuantity 거부(무해)로 그대로 넘긴다.
    var max = options.Value.MaxOrderQuantity;
    if (quantity > max)
        return Error(400, "BadRequest", $"주문 수량은 {max:N0}주 이하여야 합니다.");
    return Results.Ok(await market.Place(order.Side == "Buy" ? Side.Buy : Side.Sell, price, quantity, abort));
});
app.MapDelete("/api/orders/{id}", async (string id, MarketLoop market, CancellationToken abort) =>
    await market.Cancel(id, abort) is { } result ? Results.Ok(result) : MissingOrder());
app.MapGet("/api/orders/{id}/explanation", async (string id, MarketLoop market, CancellationToken abort) =>
    await market.Explain(id, abort) is { } result ? Results.Ok(result) : MissingOrder());
app.MapGet("/api/state/hash", async (MarketLoop market, CancellationToken abort) => Results.Ok(await market.Hash(abort)));
app.MapGet("/api/experiments/{id}", () => Error(404, "NotFound", "실험을 찾을 수 없습니다."));
app.MapPost("/api/admin/crash", async (HttpContext context, MarketLoop market, IConfiguration configuration) =>
{
    var key = configuration["Market:AdminKey"];
    if (string.IsNullOrWhiteSpace(key) || !context.Request.Headers.TryGetValue("X-Admin-Key", out var headers) ||
        headers.Count != 1 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(headers[0]!))))
        return Error(401, "Unauthorized", "유효한 관리자 키가 필요합니다.");
    await market.Crash(context.RequestAborted);
    return Results.Ok(new { ok = true });
});
app.MapHub<MarketHub>("/hubs/market");
app.Run();

static IResult Error(int status, string error, string message) => Results.Json(new { error, message }, statusCode: status);
static IResult MissingOrder() => Error(404, "NotFound", "주문을 찾을 수 없습니다.");

internal sealed record PlaceRequest(string? Side,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? Price,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? Quantity);
public partial class Program;
