using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hoga.Engine;
using Hoga.Journal;
using Microsoft.Extensions.DependencyInjection;

namespace Hoga.Server.Tests;

public sealed partial class MarketTests
{
    [Fact]
    public async Task MembershipErrorsCsrfLogoutAndLockoutMatchContract()
    {
        using var app = new MarketFactory(isolateJournal: true);
        using var client = app.CreateClient();
        var email = TestAddress();
        await Code(client.PostAsJsonAsync("/api/orders", new { }), 401, "Unauthorized");
        await Code(client.PostAsJsonAsync("/api/auth/register", new { }), 400, "BadCsrfToken");
        await CsrfHeader(client);
        await Code(client.PostAsJsonAsync("/api/auth/register", new { email, password = "", nickname = "first" }), 400, "WeakPassword");
        await Code(client.PostAsJsonAsync("/api/auth/register", new { password = TestPassword, nickname = "first" }), 400, "InvalidEmail");
        await Code(client.PostAsJsonAsync("/api/auth/register", new { email, password = TestPassword, nickname = "x" }), 400, "InvalidNickname");
        var registered = await Register(client, "first", email);
        Assert.False(registered.TryGetProperty("email", out _));
        var me = await Get(client, "/api/auth/me");
        Assert.Equal(registered.GetProperty("id").GetString(), me.GetProperty("id").GetString());
        Assert.True(me.GetProperty("email").GetString() == email);
        await Code(client.PostAsJsonAsync("/api/orders", new { }), 400, "BadCsrfToken"); // still the anonymous token
        await CsrfHeader(client);
        await Status(client.PostAsync("/api/auth/logout", null), 204);
        await Code(client.GetAsync("/api/auth/me"), 401, "Unauthorized");
        await CsrfHeader(client);
        await Code(client.PostAsJsonAsync("/api/auth/register", new { email, password = TestPassword, nickname = "second" }), 409, "EmailTaken");
        await Code(client.PostAsJsonAsync("/api/auth/register", new { email = TestAddress(), password = TestPassword, nickname = "FIRST" }), 409, "NicknameTaken");
        await Status(client.PostAsJsonAsync("/api/auth/login", new { email, password = TestPassword }), 200);
        await CsrfHeader(client);
        await Status(client.PostAsync("/api/auth/logout", null), 204);
        await CsrfHeader(client);
        for (var i = 0; i < 5; i++) await Code(client.PostAsJsonAsync("/api/auth/login", new { email, password = "" }), 401, "InvalidCredentials");
        await Code(client.PostAsJsonAsync("/api/auth/login", new { email, password = "" }), 401, "InvalidCredentials");
        await Code(client.PostAsJsonAsync("/api/auth/login", new { email, password = TestPassword }), 401, "InvalidCredentials");
        await Code(client.PostAsJsonAsync("/api/auth/login", new { email, password = TestPassword }), 429, "TooManyRequests");
    }

    [Fact]
    public async Task MemberOwnershipHoldsPriceImprovementReplayRankingsAndWithdrawal()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var buyer = app.CreateClient();
        using var seller = app.CreateClient();
        await Register(buyer, "Buyer");
        await Register(seller, "Seller");
        await CsrfHeader(buyer);
        await CsrfHeader(seller);
        await Code(buyer.PostAsync("/api/admin/reset", null), 403, "Unauthorized");
        var rejected = await Place(seller, "Sell", 10_000, 1_001);
        Assert.Equal("InsufficientShares", rejected.GetProperty("reason").GetString());
        Assert.Equal(0, (await Get(buyer, "/api/state/hash")).GetProperty("seq").GetInt64());
        Assert.Equal("InsufficientCash", (await Place(buyer, "Buy", 10_000, 1_001)).GetProperty("reason").GetString());
        Assert.Empty(JournalReader.Read(app.JournalPath).Commands);
        var ask = (await Place(seller, "Sell", 10_000, 2)).GetProperty("orderId").GetString()!;
        await Code(buyer.DeleteAsync($"/api/orders/{ask}"), 404, "NotFound");
        await Code(buyer.GetAsync($"/api/orders/{ask}/explanation"), 404, "NotFound");
        var bid = (await Place(buyer, "Buy", 10_100, 5)).GetProperty("orderId").GetString()!;
        var view = await Get(buyer, "/api/account");
        Assert.Equal(9_949_700, view.GetProperty("cash").GetInt64());
        Assert.Equal(30_300, view.GetProperty("cashOnHold").GetInt64());
        Assert.Equal(1_002, view.GetProperty("shares").GetInt64());
        Assert.Equal("PartiallyFilled", (await Get(buyer, "/api/account/orders"))[0].GetProperty("status").GetString());
        Assert.Equal(10_000, (await Get(buyer, "/api/account/trades"))[0].GetProperty("price").GetInt64());
        await Status(buyer.DeleteAsync($"/api/orders/{bid}"), 200);
        Assert.Equal(9_980_000, (await Get(buyer, "/api/account")).GetProperty("cash").GetInt64());
        app.Clock.AddMilliseconds(10_000);
        await WaitUntil(() => app.Services.GetRequiredService<MarketLoop>().Ranking(default).GetAwaiter().GetResult().Entries.Length == 2);
        var ranking = await Get(buyer, "/api/ranking");
        Assert.Equal("Seller", ranking.GetProperty("entries")[0].GetProperty("nickname").GetString());
        Assert.Equal(2, ranking.GetProperty("me").GetProperty("rank").GetInt32());
        var before = await Get(buyer, "/api/state/hash");
        using (var crash = await Admin(buyer, app.Key, "crash")) Assert.Equal(HttpStatusCode.OK, crash.StatusCode);
        AssertHash(before, await Get(buyer, "/api/state/hash"));
        using (var reset = await Admin(buyer, app.Key, "reset")) Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var restored = await Get(buyer, "/api/account");
        Assert.Equal(10_000_000, restored.GetProperty("cash").GetInt64());
        Assert.Equal(1_000, restored.GetProperty("shares").GetInt64());
        var round = (await Get(buyer, "/api/ranking/rounds"))[0].GetString();
        var history = await Get(buyer, "/api/ranking/" + round);
        Assert.Equal(2, history.GetProperty("me").GetProperty("rank").GetInt32());
        await Place(buyer, "Sell", 10_000, 1);
        using var withdraw = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(new { password = TestPassword }) };
        await Status(buyer.SendAsync(withdraw), 204);
        await Code(buyer.GetAsync("/api/auth/me"), 401, "Unauthorized");
        Assert.IsType<CancelOrder>(JournalReader.Read(app.JournalPath).Commands.Last());
        Assert.Equal(Members.DeletedNickname, (await Get(seller, "/api/ranking/" + round)).GetProperty("entries")[1].GetProperty("nickname").GetString());
    }

    [Fact]
    public async Task MemberAndBotSettlementSurvivesActualHostRestartWithOriginalRoundAssets()
    {
        using var original = new MarketFactory(frozen: true, isolateJournal: true, allowMissingRound: true);
        Directory.CreateDirectory(Path.GetDirectoryName(original.JournalPath)!);
        using (var writer = new JournalWriter(original.JournalPath, JournalHeader.Create(10_000, new TickSizeTable())))
            writer.Append(new PlaceLimitOrder("o-1", Side.Sell, 10_000, 2, "bot:test", 1, 0));
        using var beforeClient = await TradingClient(original);
        await Place(beforeClient, "Buy", 10_000, 2);
        Assert.Equal(1_002, (await Get(beforeClient, "/api/account")).GetProperty("shares").GetInt64());
        var before = await Get(beforeClient, "/api/state/hash");
        using (var crash = await Admin(beforeClient, original.Key, "crash")) Assert.Equal(HttpStatusCode.OK, crash.StatusCode);
        AssertHash(before, await Get(beforeClient, "/api/state/hash"));
        await original.Services.GetRequiredService<MarketLoop>().StopAsync(default);
        using var restarted = new MarketFactory(original.JournalPath, frozen: true, initialCash: 1);
        using var afterClient = await TradingClient(restarted);
        AssertHash(before, await Get(afterClient, "/api/state/hash"));
        Assert.Equal(9_980_000, (await Get(afterClient, "/api/account")).GetProperty("cash").GetInt64());
        Assert.Single((await Get(afterClient, "/api/account/trades")).EnumerateArray());
    }

    private static string TestAddress() => Guid.NewGuid().ToString("N") + "@example.invalid";
    private static async Task CsrfHeader(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await Get(client, "/api/auth/csrf")).GetProperty("token").GetString());
    }
    private static async Task<JsonElement> Register(HttpClient client, string nickname, string? email = null)
    {
        await CsrfHeader(client);
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { email = email ?? TestAddress(), password = TestPassword, nickname });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task Status(Task<HttpResponseMessage> response, int expected)
    {
        using var result = await response;
        Assert.Equal(expected, (int)result.StatusCode);
    }
    private static async Task Code(Task<HttpResponseMessage> response, int expected, string code)
    {
        using var result = await response;
        Assert.Equal(expected, (int)result.StatusCode);
        Assert.Equal(code, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
