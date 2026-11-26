using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Hoga.Bots;
using Hoga.Journal;
using Hoga.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hoga.Server.Tests;

public sealed partial class MarketTests
{
    [Theory]
    [InlineData(13_050, "AboveUpperLimit", "R-LIMIT-04")]
    [InlineData(6_990, "BelowLowerLimit", "R-LIMIT-04")]
    [InlineData(10_001, "OffTick", "R-TICK-02")]
    public async Task RejectsOrdersWithContractReason(long price, string reason, string rule)
    {
        using var app = new MarketFactory();
        using var client = await TradingClient(app);
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
        using var client = await TradingClient(app);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/orders", content);
        await AssertError(response, HttpStatusCode.BadRequest, "BadRequest");
    }

    [Fact]
    public async Task RejectsOversizedQuantityBeforeJournalAndKeepsServing()
    {
        using var app = new MarketFactory(initialCash: 20_000_000_000);
        using var client = await TradingClient(app);
        var before = await Get(client, "/api/state/hash");
        const string huge = "{\"side\":\"Buy\",\"price\":10000,\"quantity\":4611686018427387913}";
        for (var i = 0; i < 2; i++)
        {
            using var content = new StringContent(huge, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/orders", content);
            await AssertError(response, HttpStatusCode.BadRequest, "BadRequest");
        }
        var after = await Get(client, "/api/state/hash");
        Assert.Equal(0, after.GetProperty("seq").GetInt64());
        Assert.Equal(before.GetProperty("stateHash").GetString(), after.GetProperty("stateHash").GetString());
        Assert.True((await Place(client, "Buy", 10_000, 1_000_000)).GetProperty("accepted").GetBoolean());
        using var over = new StringContent("{\"side\":\"Sell\",\"price\":10000,\"quantity\":1000001}",
            System.Text.Encoding.UTF8, "application/json");
        await AssertError(await client.PostAsync("/api/orders", over), HttpStatusCode.BadRequest, "BadRequest");
        Assert.Equal(1, (await Get(client, "/api/state/hash")).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task CrashAndRealHostRestartPreserveHashAndExplanation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"test-{Guid.NewGuid():N}.jsonl");
        try
        {
            JsonElement before;
            string firstId;
            using (var app = new MarketFactory(path))
            using (var client = await TradingClient(app))
            {
                var first = await Place(client, "Buy", 10_000, 100);
                Assert.True(first.GetProperty("accepted").GetBoolean());
                Assert.Equal(JsonValueKind.Null, first.GetProperty("ruleId").ValueKind);
                var id = firstId = first.GetProperty("orderId").GetString()!;
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
            using var nextClient = await TradingClient(restart);
            AssertHash(before, await Get(nextClient, "/api/state/hash"));
            var next = await Place(nextClient, "Buy", 9_990, 3);
            var nextId = next.GetProperty("orderId").GetString();
            Assert.EndsWith("-1", firstId);
            Assert.Equal(firstId[..^1] + "4", nextId);
            Assert.Equal("PartiallyFilled", (await Get(nextClient, $"/api/orders/{firstId}/explanation")).GetProperty("status").GetString());
            using var cancel = await nextClient.DeleteAsync($"/api/orders/{nextId}");
            Assert.True((await Json(cancel)).GetProperty("cancelled").GetBoolean());
            using var again = await nextClient.DeleteAsync($"/api/orders/{nextId}");
            Assert.Equal("UnknownOrder", (await Json(again)).GetProperty("reason").GetString());
            Assert.Equal("Cancelled", (await Get(nextClient, $"/api/orders/{nextId}/explanation")).GetProperty("status").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReturnsMissingResourcesAndRequiresAdminKey()
    {
        using var app = new MarketFactory();
        using var client = await TradingClient(app);
        using var crash = await client.PostAsync("/api/admin/crash", null);
        await AssertError(crash, HttpStatusCode.Forbidden, "Unauthorized");
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
        using var client = await TradingClient(app);
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

    [Fact]
    public async Task ScenarioRequiresKeyAndRejectsUnknownName()
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        using var denied = await client.PostAsJsonAsync("/api/admin/scenario", new { name = "FatFinger" });
        await AssertError(denied, HttpStatusCode.Unauthorized, "Unauthorized");
        using var unknown = await Admin(client, app.Key, "scenario", "panic");
        await AssertError(unknown, HttpStatusCode.BadRequest, "UnknownScenario");
        foreach (var name in new[] { "FatFinger", "PanicSell", "Calm" })
        {
            using var ok = await Admin(client, app.Key, "scenario", name);
            Assert.True((await Json(ok)).GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task ExperimentDemoMatchesCanonicalIdAndRejectsMalformedIds()
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        var demo = await Get(client, "/api/experiments/demo");
        Assert.Equal("exp-FatFinger-16", demo.GetProperty("id").GetString());
        Assert.Equal(16, demo.GetProperty("seed").GetInt32());
        var runs = demo.GetProperty("runs");
        Assert.Equal(9490, runs[0].GetProperty("minPrice").GetInt64());
        Assert.Equal(7020, runs[1].GetProperty("minPrice").GetInt64());
        Assert.Equal(demo.GetRawText(), (await Get(client, "/api/experiments/exp-FatFinger-16")).GetRawText());
        foreach (var invalid in new[] { "exp-calm-1", "exp-Calm-0", "exp-Calm-1001", "exp-Calm-+1", "exp-X-1" })
        {
            using var response = await client.GetAsync($"/api/experiments/{invalid}");
            await AssertError(response, HttpStatusCode.NotFound, "NotFound");
        }
    }

    [Fact]
    public async Task ExperimentCalculationDoesNotBlockOrders()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ExperimentCache(TimeSpan.FromSeconds(10), (seed, scenario) =>
        {
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Experiment was not released.");
            return FakeExperiment(seed, scenario);
        });
        using var factory = new MarketFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(cache)));
        using var client = await TradingClient(app);
        var experiment = Get(client, "/api/experiments/demo");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var order = await Place(client, "Buy", 10_000, 100).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(order.GetProperty("accepted").GetBoolean());
            Assert.False(experiment.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal("exp-FatFinger-16", (await experiment).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ConcurrentExperimentRequestsShareCalculationEvenIfOneCancels()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ExperimentCache(TimeSpan.FromSeconds(10), (seed, scenario) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Experiment was not released.");
            return FakeExperiment(seed, scenario);
        });
        var abandoned = cache.Get("demo", cancellation.Token);
        Task<Experiment?> canonical, alias;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canonical = cache.Get("exp-FatFinger-16", CancellationToken.None);
            alias = cache.Get("demo", CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
            Assert.False(canonical.IsCompleted);
            Assert.False(alias.IsCompleted);
        }
        finally { release.Set(); }
        var results = await Task.WhenAll(canonical, alias);
        Assert.NotNull(results[0]);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ExperimentReturns503WhenCalculationSlotIsBusy()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ExperimentCache(TimeSpan.FromSeconds(1), (seed, scenario) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Experiment was not released.");
                return FakeExperiment(seed, scenario);
            }
            finally { finished.TrySetResult(); }
        });
        using var factory = new MarketFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(cache)));
        using var client = app.CreateClient();
        var running = cache.Get("demo", CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync("/api/experiments/exp-Calm-1").WaitAsync(TimeSpan.FromSeconds(5));
            await AssertError(response, HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
            Assert.Equal(1, Volatile.Read(ref calls));
            await Assert.ThrowsAsync<TimeoutException>(async () => await running);
        }
        finally { release.Set(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExperimentCacheKeepsEightResultsInInsertionOrder()
    {
        var calls = 0;
        var cache = new ExperimentCache(TimeSpan.FromSeconds(5), (seed, scenario) =>
        {
            Interlocked.Increment(ref calls);
            return FakeExperiment(seed, scenario);
        });
        for (var seed = 1; seed <= ExperimentCache.Capacity; seed++)
            await cache.Get($"exp-Calm-{seed}", CancellationToken.None);
        await cache.Get("exp-Calm-1", CancellationToken.None);
        Assert.Equal(ExperimentCache.Capacity, Volatile.Read(ref calls));
        await cache.Get($"exp-Calm-{ExperimentCache.Capacity + 1}", CancellationToken.None);
        await cache.Get("exp-Calm-2", CancellationToken.None);
        Assert.Equal(ExperimentCache.Capacity + 1, Volatile.Read(ref calls));
        await cache.Get("exp-Calm-1", CancellationToken.None);
        Assert.Equal(ExperimentCache.Capacity + 2, Volatile.Read(ref calls));
    }

    private static Experiment FakeExperiment(long seed, Scenario scenario) => new($"exp-{scenario}-{seed}", seed, scenario.ToString(), []);

    [Fact]
    public async Task ReadExceptionFailsOnlyThatRequest()
    {
        using var app = new MarketFactory();
        using var client = await TradingClient(app);
        var loop = app.Services.GetRequiredService<MarketLoop>();
        var failure = await Assert.ThrowsAsync<MarketUnavailableException>(() =>
            loop.Read<int>(() => throw new InvalidOperationException("test read failure"), CancellationToken.None));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.True((await Place(client, "Buy", 10_000, 1)).GetProperty("accepted").GetBoolean());
        Assert.Equal(1, (await Get(client, "/api/state/hash")).GetProperty("seq").GetInt64());
    }

    [Fact]
    public void EvictsOldestTerminalOrderButNeverAnActiveOrder()
    {
        var engine = new MatchingEngine(10_000, new TickSizeTable());
        var projection = new MarketProjection();
        Apply(new PlaceLimitOrder("o-1", Side.Buy, 10_000, 2, "rest", 1, 0));
        for (var i = 2; i <= MarketProjection.TerminalCapacity + 2; i++)
            Apply(new PlaceLimitOrder($"o-{i}", Side.Buy, 10_000, 0, "rest", i, 0));
        Assert.NotNull(projection.Explain(engine, "o-1"));
        Assert.Null(projection.Explain(engine, "o-2"));
        Assert.True(projection.MayHaveExpired(2));
        Assert.NotNull(projection.Explain(engine, "o-3"));
        Assert.NotNull(projection.Explain(engine, $"o-{MarketProjection.TerminalCapacity + 2}"));

        void Apply(OrderCommand command) => projection.Apply(command, engine.Process(command), publish: false);
    }

    [Fact]
    public async Task DistinguishesExpiredOrderFromAnOldNonOrderSequenceAfterReplay()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var header = JournalHeader.Create(10_000, new TickSizeTable());
            using (var writer = new JournalWriter(path, header, flushEvery: 8192))
            {
                writer.Append(new AdvanceTime(1, 0));
                for (var i = 2; i <= MarketProjection.TerminalCapacity + 3; ++i)
                    writer.Append(new PlaceLimitOrder($"o-{i}", Side.Buy, 10_000, 0, "rest", i, 0));
            }
            using var app = new MarketFactory(path, allowMissingRound: true);
            using var client = app.CreateClient();
            var market = app.Services.GetRequiredService<MarketLoop>();
            Assert.True((await market.ExplainOrder("rest", "o-2", default)).Expired);
            Assert.False((await market.ExplainOrder("other", "o-2", default)).Expired);
            Assert.False((await market.ExplainOrder("rest", "o-1", default)).Expired);
            var recent = JsonSerializer.SerializeToElement((await market.ExplainOrder("rest",
                $"o-{MarketProjection.TerminalCapacity + 3}", default)).Result);
            Assert.Equal("Rejected", recent.GetProperty("status").GetString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(Hoga.Bots.Scenario.FatFinger, "FatFinger")]
    [InlineData(Hoga.Bots.Scenario.PanicSell, "PanicSeller")]
    public async Task LiveScenarioFiresOnceAndThenReturnsToCalm(Hoga.Bots.Scenario scenario, string name)
    {
        var bots = new LiveBots(42, 10_000);
        var engine = new MatchingEngine(10_000, new TickSizeTable());
        var fired = 0;
        await bots.Select(scenario, 0, _ => Task.CompletedTask);
        await bots.Step(1_000, engine, _ => Task.CompletedTask, Count);
        await bots.Step(2_000, engine, _ => Task.CompletedTask, Count);
        Assert.Equal(1, fired);
        await bots.Step(LiveBots.CooldownMs, engine, _ => Task.CompletedTask, Count);
        Assert.Equal(Hoga.Bots.Scenario.Calm, bots.CurrentScenario);
        await bots.Step(LiveBots.CooldownMs + 1_000, engine, _ => Task.CompletedTask, Count);
        Assert.Equal(1, fired);

        Task Count(string account, Side side, long price, long quantity)
        {
            if (account == name) fired++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HistoryIsSentOnlyToNewClientInCandleTradeSnapshotOrder()
    {
        using var app = new MarketFactory(frozen: true, ordersPerSecond: 200);
        using var client = await TradingClient(app);
        await using var oldHub = app.Connect();
        var old = Observe(oldHub);
        await oldHub.StartAsync();
        Assert.Equal("bookSnapshot", (await old.Reader.ReadAsync()).Name);
        for (var i = 0; i < 51; i++)
        {
            if (i == 50) app.Clock.AddMilliseconds(60_000);
            await Place(client, "Buy", 10_000, 1);
            await Place(client, "Sell", 10_000, 1);
        }
        await using var newHub = app.Connect();
        var history = Observe(newHub);
        await newHub.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = history.Reader;
        for (var i = 0; i < 2; i++)
        {
            var candle = await reader.ReadAsync(timeout.Token);
            Assert.Equal("candle", candle.Name);
            Assert.Equal(i * 60_000L, candle.Body.GetProperty("startMs").GetInt64());
        }
        for (var i = 0; i < 50; i++)
        {
            var trade = await reader.ReadAsync(timeout.Token);
            Assert.Equal("trade", trade.Name);
            Assert.Equal(i * 2L + 4, trade.Body.GetProperty("seq").GetInt64());
        }
        Assert.Equal("bookSnapshot", (await reader.ReadAsync(timeout.Token)).Name);
        // The old client has exactly the live trades after all orders, no replay.
        await WaitUntil(() => old.Reader.Count >= 102);
        var observed = 0;
        while (old.Reader.TryRead(out var entry)) if (entry.Name == "trade") observed++;
        Assert.Equal(51, observed);

        static Channel<(string Name, JsonElement Body)> Observe(HubConnection hub)
        {
            var stream = Channel.CreateUnbounded<(string, JsonElement)>();
            foreach (var name in new[] { "candle", "trade", "bookSnapshot" })
                hub.On<JsonElement>(name, value => stream.Writer.TryWrite((name, value)));
            return stream;
        }
    }

    [Fact]
    public async Task JournaledBotsPreserveHashAcrossCrash()
    {
        using var app = new MarketFactory(bots: true, frozen: true);
        using var client = app.CreateClient();
        // The request is processed only after ExecuteAsync captures the clock origin.
        await Get(client, "/api/state/hash");
        app.Clock.AddMilliseconds(1_000);
        await WaitUntil(() => JournalReader.Read(app.JournalPath).Commands.OfType<PlaceLimitOrder>()
            .Any(o => o.AccountId == "bot:MarketMaker"));
        var before = await Get(client, "/api/state/hash");
        using var crash = await Admin(client, app.Key, "crash");
        Assert.True((await Json(crash)).GetProperty("ok").GetBoolean());
        AssertHash(before, await Get(client, "/api/state/hash"));
        app.Clock.AddMilliseconds(1000);
        await WaitUntil(() => JournalReader.Read(app.JournalPath).Commands.Any(c =>
            c.Sequence > before.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task Summary_serves_the_packaged_json_without_changes()
    {
        using var app = new MarketFactory();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/experiments/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var expected = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "summary.json"));
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Summary_returns_503_when_the_packaged_file_is_absent()
    {
        using var app = new MarketFactory(summaryPath: Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}", "summary.json"));
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/experiments/summary");

        await AssertError(response, HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
    }

    [Fact]
    public async Task AdminResetArchivesAndPublishesEmptyMarketInOrder()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var client = await TradingClient(app);
        await using var hub = app.Connect();
        var events = Channel.CreateUnbounded<(string Name, JsonElement Body)>();
        hub.On<JsonElement>("marketReset", body => events.Writer.TryWrite(("marketReset", body)));
        hub.On<JsonElement>("bookSnapshot", body => events.Writer.TryWrite(("bookSnapshot", body)));
        await hub.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await events.Reader.ReadAsync(timeout.Token);
        var order = await Place(client, "Buy", 10_000, 2);
        var previousId = order.GetProperty("orderId").GetString()!;
        Assert.Equal(1, (await Get(client, "/healthz")).GetProperty("seq").GetInt64());
        using var unauthorized = await client.PostAsync("/api/admin/reset", null);
        await AssertError(unauthorized, HttpStatusCode.Forbidden, "Unauthorized");
        Assert.Equal("bookSnapshot", (await events.Reader.ReadAsync(timeout.Token)).Name);
        using var reset = await Admin(client, app.Key, "reset");
        Assert.True((await Json(reset)).GetProperty("ok").GetBoolean());
        var signal = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal("marketReset", signal.Name);
        Assert.Equal("Admin", signal.Body.GetProperty("reason").GetString());
        Assert.Equal(app.Clock.GetUtcNow(), signal.Body.GetProperty("atUtc").GetDateTimeOffset());
        var snapshot = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal("bookSnapshot", snapshot.Name);
        Assert.Equal(0, snapshot.Body.GetProperty("seq").GetInt64());
        Assert.Equal(0, snapshot.Body.GetProperty("bids").GetArrayLength());
        Assert.Equal(0, snapshot.Body.GetProperty("logicalTimeMs").GetInt64());
        Assert.Empty(JournalReader.Read(app.JournalPath).Commands);
        Assert.Single(JournalReader.Read(Assert.Single(app.Archives())).Commands);
        for (var i = 0; i < 2; i++)
        {
            using var again = await Admin(client, app.Key, "reset");
            Assert.True((await Json(again)).GetProperty("ok").GetBoolean());
        }
        Assert.Equal(2, app.Archives().Length);
        Assert.All(app.Archives(), path => Assert.Empty(JournalReader.Read(path).Commands));
        var newId = (await Place(client, "Buy", 10_000, 2)).GetProperty("orderId").GetString();
        Assert.EndsWith("-1", newId);
        Assert.NotEqual(previousId, newId);
        await AssertError(await client.GetAsync($"/api/orders/{previousId}/explanation"), HttpStatusCode.NotFound, "NotFound");
        await AssertError(await client.DeleteAsync($"/api/orders/{previousId}"), HttpStatusCode.NotFound, "NotFound");
        Assert.Equal("Resting", (await Get(client, $"/api/orders/{newId}/explanation")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FailedJournalMoveCancelsResetAndPreservesWritableMarket()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var client = await TradingClient(app);
        var id = (await Place(client, "Buy", 10_000, 2)).GetProperty("orderId").GetString()!;
        var before = await Get(client, "/api/state/hash");
        // Directory collision: File.Exists is false, but File.Move cannot create a file here.
        var blocked = Path.Combine(Path.GetDirectoryName(app.JournalPath)!, "market-20261126-045959.jsonl");
        Directory.CreateDirectory(blocked);
        await AssertError(await Admin(client, app.Key, "reset"), HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
        Assert.Equal(before.GetRawText(), (await Get(client, "/api/state/hash")).GetRawText());
        Assert.Equal("Resting", (await Get(client, $"/api/orders/{id}/explanation")).GetProperty("status").GetString());
        var next = await Place(client, "Buy", 10_000, 2);
        Assert.Equal(id[..^1] + "2", next.GetProperty("orderId").GetString());
        Assert.Equal(2, JournalReader.Read(app.JournalPath).Commands.Count());
        Directory.Delete(blocked);
        using var reset = await Admin(client, app.Key, "reset");
        Assert.True((await Json(reset)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DailyResetUsesSeoulWallClockAndRunsOnlyOnce()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true, dailyReset: "05:00");
        using var client = await TradingClient(app);
        await using var hub = app.Connect();
        var resets = Channel.CreateUnbounded<JsonElement>();
        hub.On<JsonElement>("marketReset", body => resets.Writer.TryWrite(body));
        await hub.StartAsync();
        await Place(client, "Buy", 10_000, 2);
        app.Clock.AddMilliseconds(999); // 04:59:59.999 Asia/Seoul
        Assert.Equal(1, (await Get(client, "/healthz")).GetProperty("seq").GetInt64());
        Assert.Empty(app.Archives());
        await WaitUntil(() => Directory.Exists(app.BackupDirectory) && Directory.GetFiles(app.BackupDirectory, "*.zip").Length > 0);
        var backup = Assert.Single(Directory.GetFiles(app.BackupDirectory, "*.zip"));
        await WaitUntil(() => File.Exists(backup)); // discard the startup snapshot, not the daily one
        File.Delete(backup);
        app.Clock.AddMilliseconds(1);
        var reset = await Read(resets);
        Assert.Equal("Daily", reset.GetProperty("reason").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 20, 0, 0, TimeSpan.Zero), reset.GetProperty("atUtc").GetDateTimeOffset());
        Assert.Equal("market-20261126-050000.jsonl", Path.GetFileName(Assert.Single(app.Archives())));
        await WaitUntil(() => Directory.GetFiles(app.BackupDirectory, "*.zip").Length > 0);
        var database = Path.Combine(app.BackupDirectory, "restore.db");
        using (var bundle = System.IO.Compression.ZipFile.OpenRead(Assert.Single(Directory.GetFiles(app.BackupDirectory, "*.zip"))))
        using (var entry = bundle.GetEntry("hoga.db")!.Open())
        using (var file = File.Create(database)) entry.CopyTo(file);
        using (var restored = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            restored.Open();
            using var query = restored.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM RoundEntry WHERE Rank = 1;";
            Assert.Equal(1L, query.ExecuteScalar());
        }
        app.Clock.AddMilliseconds(60_000);
        Assert.Equal(0, (await Get(client, "/healthz")).GetProperty("seq").GetInt64());
        Assert.False(resets.Reader.TryRead(out _));
    }

    [Fact]
    public async Task FatFingerTriggersViBeforeFirstRegularQuoteAtPublicIntensity()
    {
        using var app = new MarketFactory(bots: true, frozen: true, intensity: "0.10");
        using var client = app.CreateClient();
        await using var hub = app.Connect();
        var events = Channel.CreateUnbounded<JsonElement>();
        hub.On<JsonElement>("viEvent", body => events.Writer.TryWrite(body));
        await hub.StartAsync();
        Assert.Equal(0, (await Get(client, "/healthz")).GetProperty("seq").GetInt64());
        using var scenario = await Admin(client, app.Key, "scenario", "FatFinger");
        Assert.True((await Json(scenario)).GetProperty("ok").GetBoolean());
        app.Clock.AddMilliseconds(100);
        var vi = await Read(events);
        Assert.Equal("Triggered", vi.GetProperty("type").GetString());
        Assert.Equal("R-VI-01", vi.GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task NonCancellationBroadcastFailureDoesNotStopMarket()
    {
        using var app = new MarketFactory(frozen: true, throwBroadcast: true);
        using var client = await TradingClient(app);
        await Place(client, "Buy", 10_000, 1);
        await Place(client, "Sell", 10_000, 1);
        Assert.Equal(2, (await Get(client, "/healthz")).GetProperty("seq").GetInt64());
        Assert.Equal(2, JournalReader.Read(app.JournalPath).Commands.Count());
    }

    private sealed class ThrowingHub(ILogger<DefaultHubLifetimeManager<MarketHub>> logger)
        : DefaultHubLifetimeManager<MarketHub>(logger)
    {
        public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Simulated send failure.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static async Task<HttpResponseMessage> Admin(HttpClient client, string key, string route, string? scenario = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/{route}");
        req.Headers.Add("X-Admin-Key", key);
        if (scenario is not null) req.Content = JsonContent.Create(new { name = scenario });
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> Read(Channel<JsonElement> channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static readonly string TestPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private static async Task<HttpClient> TradingClient(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var suffix = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Market:JournalPath"]!)));
        var email = suffix[..24] + "@example.invalid";
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await Get(client, "/api/auth/csrf")).GetProperty("token").GetString());
        using var registered = await client.PostAsJsonAsync("/api/auth/register", new { email, password = TestPassword, nickname = "trader" });
        var registrationError = registered.StatusCode == HttpStatusCode.Created ? null : (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        if (registered.StatusCode == HttpStatusCode.Conflict && registrationError == "EmailTaken")
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        else if (registered.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException(registrationError);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await Get(client, "/api/auth/csrf")).GetProperty("token").GetString());
        return client;
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
        if (code != "Unauthorized") Assert.NotEmpty(body.GetProperty("message").GetString()!);
    }

    private static void AssertHash(JsonElement before, JsonElement after)
    {
        Assert.Equal(before.GetProperty("seq").GetInt64(), after.GetProperty("seq").GetInt64());
        Assert.Equal(before.GetProperty("stateHash").GetString(), after.GetProperty("stateHash").GetString());
        Assert.Equal(before.GetProperty("ledgerHash").GetString(), after.GetProperty("ledgerHash").GetString());
        Assert.True(after.GetProperty("restoredFromJournal").GetBoolean());
    }

    private sealed class MarketFactory(string? existingPath = null, int speed = 1, bool bots = false,
        bool frozen = false, bool isolateJournal = false, string? dailyReset = null, string intensity = "1.0",
        bool throwBroadcast = false, string? summaryPath = null, int ordersPerSecond = 20, string? proxyNetwork = null, long initialCash = 10_000_000, bool allowMissingRound = false, string? restoreRoot = null) : WebApplicationFactory<Program>
    {
        public string JournalPath { get; } = existingPath ?? (isolateJournal
            ? Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"), "market.jsonl")
            : Path.Combine(AppContext.BaseDirectory, $"test-{Guid.NewGuid():N}.jsonl"));
        public string BackupDirectory => JournalPath + ".backups";
        public string[] Archives() => Directory.GetFiles(Path.GetDirectoryName(JournalPath)!, "market-*.jsonl");
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public TestClock Clock { get; } = new(frozen);

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Market:JournalPath"] = JournalPath, ["Market:AdminKey"] = Key,
                ["Market:DatabasePath"] = (restoreRoot is null ? JournalPath + ".db" : Path.Combine(restoreRoot, "hoga.db")), ["Market:KeysPath"] = (restoreRoot is null ? JournalPath + ".keys" : Path.Combine(restoreRoot, "keys")),
                ["Operations:BackupDirectory"] = BackupDirectory,
                ["Market:AllowMissingRoundMetadata"] = allowMissingRound.ToString(),
                ["Market:InitialCash"] = initialCash.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Market:OrdersPerSecond"] = ordersPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Market:TrustedProxyNetwork"] = proxyNetwork,
                ["Market:Bots"] = bots.ToString(), ["Market:BotIntensity"] = intensity,
                ["Market:DailyResetLocalTime"] = dailyReset,
                ["Market:Speed"] = speed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }))
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                if (summaryPath is not null) services.AddSingleton(new ExperimentSummaryFile(summaryPath));
                if (throwBroadcast) services.AddSingleton<HubLifetimeManager<MarketHub>, ThrowingHub>();
            });

        public HubConnection Connect(string? session = null) => new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/market"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                if (session is not null) options.Headers["Cookie"] = session;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            }).Build();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || existingPath is not null) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (isolateJournal)
            {
                var directory = Path.GetDirectoryName(JournalPath)!;
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            else
            {
                File.Delete(JournalPath);
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(JournalPath)!, Path.GetFileName(JournalPath) + ".*")) File.Delete(file);
                if (Directory.Exists(JournalPath + ".keys")) Directory.Delete(JournalPath + ".keys", recursive: true);
                if (Directory.Exists(BackupDirectory)) Directory.Delete(BackupDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OrderAndCancelShareIpLimitButAdminAndReadsDoNot()
    {
        using var app = new MarketFactory(ordersPerSecond: 1);
        using var client = await TradingClient(app);
        using var order = await client.PostAsJsonAsync("/api/orders", new { side = "Buy", price = 10_000, quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using var limited = await client.DeleteAsync("/api/orders/missing");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("{\"error\":\"TooManyRequests\"}", await limited.Content.ReadAsStringAsync());
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var unauthorized = await client.PostAsync("/api/admin/reset", null);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
    }

    [Fact]
    public async Task AdminLimitUsesOnlyTheNearestForwardFromConfiguredPrivateSubnet()
    {
        using var app = new MarketFactory(proxyNetwork: "172.20.0.0/16");
        using var client = app.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, await Status("203.0.113.10", $"198.51.100.{i + 1}"));
            Assert.Equal(401, await Status("172.20.0.2", $"198.51.100.{i + 1}, 203.0.113.11"));
        }
        Assert.Equal(429, await Status("203.0.113.10", "198.51.100.50"));
        Assert.Equal(429, await Status("172.20.0.2", "198.51.100.50, 203.0.113.11"));
        Assert.Equal(401, await Status("172.20.0.2", "203.0.113.12"));

        async Task<int> Status(string ip, string forwarded)
        {
            var response = await app.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                context.Request.Method = "POST";
                context.Request.Path = "/api/admin/reset";
                context.Request.Headers["X-Forwarded-For"] = forwarded;
            });
            return response.Response.StatusCode;
        }
    }

    private sealed class TestClock(bool frozen) : TimeProvider
    {
        private long _offset;
        public override DateTimeOffset GetUtcNow() => frozen
            ? new DateTimeOffset(2026, 11, 25, 19, 59, 59, TimeSpan.Zero)
                .AddMilliseconds(Interlocked.Read(ref _offset) * 1000 / TimestampFrequency)
            : base.GetUtcNow();
        public override long GetTimestamp() => (frozen ? 0 : base.GetTimestamp()) + Interlocked.Read(ref _offset);
        public void AddMilliseconds(long ms) => Interlocked.Add(ref _offset, ms * TimestampFrequency / 1000);
    }
}
