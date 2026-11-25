using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hoga.Server.Tests;

public sealed class MarketTests
{
    [Theory]
    [InlineData(13_050, "AboveUpperLimit", "R-LIMIT-04")]
    [InlineData(6_990, "BelowLowerLimit", "R-LIMIT-04")]
    [InlineData(10_001, "OffTick", "R-TICK-02")]
    public async Task RejectsOrdersWithContractReason(long price, string reason, string rule)
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        var body = await Place(client, "Buy", price, 100);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orderId").ValueKind);
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.Equal(rule, body.GetProperty("ruleId").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"side\":\"buy\",\"price\":10000,\"quantity\":100}")]
    [InlineData("{\"side\":\"Buy\",\"price\":\"10000\",\"quantity\":100}")]
    [InlineData("{\"side\":\"Buy\",\"price\":10000}")]
    public async Task ReportsBadRequestForMalformedOrder(string json)
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/orders", content);
        await AssertError(response, HttpStatusCode.BadRequest, "BadRequest");
    }

    [Fact]
    public async Task CrashAndRealHostRestartPreserveHashAndExplanation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"test-{Guid.NewGuid():N}.jsonl");
        try
        {
            JsonElement before;
            using (var app = new MarketFactory(path))
            using (var client = app.CreateClient())
            {
                var first = await Place(client, "Buy", 10_000, 100);
                Assert.True(first.GetProperty("accepted").GetBoolean());
                Assert.Equal(JsonValueKind.Null, first.GetProperty("ruleId").ValueKind);
                var id = first.GetProperty("orderId").GetString();
                await Place(client, "Sell", 10_000, 40);
                var tail = (await Place(client, "Buy", 10_000, 25)).GetProperty("orderId").GetString();
                var explanation = await Get(client, $"/api/orders/{id}/explanation");
                Assert.Equal("PartiallyFilled", explanation.GetProperty("status").GetString());
                Assert.Equal(60, explanation.GetProperty("remainingQuantity").GetInt64());
                var queued = await Get(client, $"/api/orders/{tail}/explanation");
                Assert.Equal(1, queued.GetProperty("queueAhead").GetProperty("orders").GetInt32());
                Assert.Equal(60, queued.GetProperty("queueAhead").GetProperty("quantity").GetInt64());
                Assert.Contains("2번째", queued.GetProperty("message").GetString());
                before = await Get(client, "/api/state/hash");
                Assert.False(before.GetProperty("restoredFromJournal").GetBoolean());
                using var crash = new HttpRequestMessage(HttpMethod.Post, "/api/admin/crash");
                crash.Headers.Add("X-Admin-Key", app.Key);
                using var response = await client.SendAsync(crash);
                Assert.True((await Json(response)).GetProperty("ok").GetBoolean());
                AssertHash(before, await Get(client, "/api/state/hash"));
                Assert.Equal(explanation.GetRawText(), (await Get(client, $"/api/orders/{id}/explanation")).GetRawText());
            }
            using var restart = new MarketFactory(path);
            using var nextClient = restart.CreateClient();
            AssertHash(before, await Get(nextClient, "/api/state/hash"));
            var next = await Place(nextClient, "Buy", 9_990, 3);
            Assert.Equal("o-4", next.GetProperty("orderId").GetString());
            using var cancel = await nextClient.DeleteAsync("/api/orders/o-4");
            Assert.True((await Json(cancel)).GetProperty("cancelled").GetBoolean());
            using var again = await nextClient.DeleteAsync("/api/orders/o-4");
            Assert.Equal("UnknownOrder", (await Json(again)).GetProperty("reason").GetString());
            Assert.Equal("Cancelled", (await Get(nextClient, "/api/orders/o-4/explanation")).GetProperty("status").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReturnsMissingResourcesAndRequiresAdminKey()
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        using var crash = await client.PostAsync("/api/admin/crash", null);
        await AssertError(crash, HttpStatusCode.Unauthorized, "Unauthorized");
        using var missing = await client.GetAsync("/api/orders/o-404/explanation");
        await AssertError(missing, HttpStatusCode.NotFound, "NotFound");
        using var cancel = await client.DeleteAsync("/api/orders/o-404");
        await AssertError(cancel, HttpStatusCode.NotFound, "NotFound");
        using var experiment = await client.GetAsync("/api/experiments/exp-1");
        await AssertError(experiment, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task WaitsForAuctionThenBroadcastsTradeCandleAndRelease()
    {
        using var app = new MarketFactory(speed: 10);
        using var client = app.CreateClient();
        var snapshots = Channel.CreateUnbounded<JsonElement>();
        var vi = Channel.CreateUnbounded<JsonElement>();
        var trades = Channel.CreateUnbounded<JsonElement>();
        var candles = Channel.CreateUnbounded<JsonElement>();
        await using var hub = app.Connect();
        using var bookSub = hub.On<JsonElement>("bookSnapshot", value => snapshots.Writer.TryWrite(value));
        using var viSub = hub.On<JsonElement>("viEvent", value => vi.Writer.TryWrite(value));
        using var tradeSub = hub.On<JsonElement>("trade", value => trades.Writer.TryWrite(value));
        using var candleSub = hub.On<JsonElement>("candle", value => candles.Writer.TryWrite(value));
        await hub.StartAsync();
        var initial = await Read(snapshots);
        Assert.Equal("HOGA", initial.GetProperty("symbol").GetString());
        Assert.Equal(0, initial.GetProperty("seq").GetInt64());
        Assert.Equal("Continuous", initial.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("vi").ValueKind);
        await Place(client, "Sell", 10_600, 10);
        var order = await Place(client, "Buy", 10_600, 15);
        var triggered = await Read(vi);
        Assert.Equal("Triggered", triggered.GetProperty("type").GetString());
        var active = await Read(snapshots);
        if (active.GetProperty("phase").GetString() != "CallAuction") active = await Read(snapshots);
        Assert.Equal("CallAuction", active.GetProperty("phase").GetString());
        Assert.Equal(10_600, active.GetProperty("expectedPrice").GetInt64());
        Assert.Equal(10, active.GetProperty("expectedQuantity").GetInt64());
        var id = order.GetProperty("orderId").GetString();
        Assert.Equal("WaitingAuction", (await Get(client, $"/api/orders/{id}/explanation")).GetProperty("status").GetString());
        var scheduled = active.GetProperty("vi").GetProperty("scheduledEndMs").GetInt64();
        app.Clock.AddMilliseconds(16_000);
        var released = await Read(vi);
        Assert.Equal("Released", released.GetProperty("type").GetString());
        Assert.Equal(10_600, released.GetProperty("auctionPrice").GetInt64());
        var trade = await Read(trades);
        Assert.Equal(JsonValueKind.Null, trade.GetProperty("aggressorSide").ValueKind);
        var at = trade.GetProperty("logicalTimeMs").GetInt64();
        Assert.InRange(at, scheduled, scheduled + 30_000);
        var candle = await Read(candles);
        Assert.Equal(at / 60_000 * 60_000, candle.GetProperty("startMs").GetInt64());
        Assert.Equal(10, candle.GetProperty("volume").GetInt64());
        await hub.StopAsync();
        await using var reconnect = app.Connect();
        var reconnected = Channel.CreateUnbounded<JsonElement>();
        using var sub = reconnect.On<JsonElement>("bookSnapshot", value => reconnected.Writer.TryWrite(value));
        await reconnect.StartAsync();
        var latest = await Read(reconnected);
        Assert.Equal("Continuous", latest.GetProperty("phase").GetString());
        Assert.Single(latest.GetProperty("bids").EnumerateArray());
        Assert.Equal(5, latest.GetProperty("bids")[0].GetProperty("quantity").GetInt64());
    }

    private static async Task<JsonElement> Read(Channel<JsonElement> channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<JsonElement> Place(HttpClient client, string side, long price, long quantity)
    {
        using var response = await client.PostAsJsonAsync("/api/orders", new { side, price, quantity });
        return await Json(response);
    }

    private static async Task<JsonElement> Get(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        return await Json(response);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.NotEmpty(body.GetProperty("message").GetString()!);
    }

    private static void AssertHash(JsonElement before, JsonElement after)
    {
        Assert.Equal(before.GetProperty("seq").GetInt64(), after.GetProperty("seq").GetInt64());
        Assert.Equal(before.GetProperty("stateHash").GetString(), after.GetProperty("stateHash").GetString());
        Assert.True(after.GetProperty("restoredFromJournal").GetBoolean());
    }

    private sealed class MarketFactory(string? existingPath = null, int speed = 1) : WebApplicationFactory<Program>
    {
        public string JournalPath { get; } = existingPath ?? Path.Combine(AppContext.BaseDirectory, $"test-{Guid.NewGuid():N}.jsonl");
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public TestClock Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Market:JournalPath"] = JournalPath, ["Market:AdminKey"] = Key,
                ["Market:Speed"] = speed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }))
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));

        public HubConnection Connect() => new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/market"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            }).Build();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && existingPath is null) File.Delete(JournalPath);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _offset;
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _offset);
        public void AddMilliseconds(long ms) => Interlocked.Add(ref _offset, ms * TimestampFrequency / 1000);
    }
}
