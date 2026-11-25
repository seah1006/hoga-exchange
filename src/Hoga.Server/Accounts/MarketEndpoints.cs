using System.Text.Json;
using System.Text.Json.Serialization;
using Hoga.Engine;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
namespace Hoga.Server;
internal static class MarketEndpoints
{
    internal static void MapMarketWrites(this WebApplication app)
    {
        app.MapPost("/api/orders", async (HttpContext context, MarketLoop market, IOptions<MarketOptions> options) =>
        {
            if (Members.Id(context) is not { } owner) return Members.Error(401, "Unauthorized");
            if (await Members.Csrf(context) is { } failure) return failure;
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
            return Results.Ok(await market.Place(owner, order.Side == "Buy" ? Side.Buy : Side.Sell, price, quantity, abort));
        }).RequireRateLimiting("orders");
        app.MapDelete("/api/orders/{id}", async (string id, MarketLoop market, CancellationToken abort) =>
            await market.Cancel(id, abort) is { } result ? Results.Ok(result) : MissingOrder()).RequireRateLimiting("orders");
        app.MapGet("/api/orders/{id}/explanation", async (string id, MarketLoop market, CancellationToken abort) =>
        {
            var (result, expired) = await market.ExplainOrder(id, abort);
            return result is not null ? Results.Ok(result) : expired
                ? Error(404, "NotFound", "기록 보관 기간이 지났습니다") : MissingOrder();
        });
        var admin = app.MapGroup("/api/admin").RequireRateLimiting("admin").AddEndpointFilter(Members.AdminFilter);
        admin.MapPost("/scenario", async (HttpContext context, MarketLoop market) =>
        {
            if (!IsAdmin(context, configuration)) return Unauthorized();
            if (!context.Request.HasJsonContentType() ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
                return Error(400, "BadRequest", "JSON 시나리오 본문이 필요합니다.");
            ScenarioRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<ScenarioRequest>(context.RequestAborted); }
            catch (JsonException) { return Error(400, "BadRequest", "name 형식을 확인해 주세요."); }
            catch (BadHttpRequestException) { return Error(400, "BadRequest", "시나리오 본문을 읽을 수 없습니다."); }
            if (request?.Name is null) return Error(400, "BadRequest", "시나리오 이름이 필요합니다.");
            if (ExperimentCache.ScenarioName(request.Name) is not { } scenario)
                return Error(400, "UnknownScenario", "알 수 없는 시나리오입니다.");
            await market.SelectScenario(scenario, context.RequestAborted);
            return Results.Ok(new { ok = true });
        });
        admin.MapPost("/crash", async (HttpContext context, MarketLoop market) =>
        {
            if (!IsAdmin(context, configuration)) return Unauthorized();
            await market.Crash(context.RequestAborted);
            return Results.Ok(new { ok = true });
        });
        admin.MapPost("/reset", async (HttpContext context, MarketLoop market) =>
        {
            if (!IsAdmin(context, configuration)) return Unauthorized();
            if (!await market.Reset(context.RequestAborted)) return Error(503, "ServiceUnavailable", "현재 장을 보관할 수 없어 초기화를 취소했습니다.");
            return Results.Ok(new { ok = true });
        });
    }
    private static IResult Error(int status, string error, string message) => Results.Json(new { error, message }, statusCode: status);
    private static IResult MissingOrder() => Error(404, "NotFound", "주문을 찾을 수 없습니다.");
    private sealed record ScenarioRequest(string? Name);
    private sealed record PlaceRequest(string? Side,
        [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? Price,
        [property: JsonNumberHandling(JsonNumberHandling.Strict)] long? Quantity);
}
