using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Channels;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hoga.Engine;
using Hoga.Journal;
using Hoga.Server.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hoga.Server.Tests;

public sealed partial class MarketTests
{
    [Fact]
    public async Task Review8LoginFailureBodiesMatchAndIndependentQuotasApply()
    {
        using var app = new MarketFactory(isolateJournal: true);
        using var owner = app.CreateClient();
        var email = TestAddress();
        await Register(owner, "ReviewLogin", email);
        using var anonymous = app.CreateClient();
        await CsrfHeader(anonymous);
        async Task<string> Fail(string address)
        {
            using var result = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = address, password = "" });
            Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
            return await result.Content.ReadAsStringAsync();
        }
        var missing = await Fail(TestAddress());
        for (var i = 0; i < 8; i++) Assert.Equal(missing, await Fail(email));
        await Code(anonymous.PostAsJsonAsync("/api/auth/login", new { email = email.ToUpperInvariant(), password = "" }), 429, "TooManyRequests");
        for (var i = 0; i < 20; i++) Assert.Equal(missing, await Fail(TestAddress()));
        await Code(anonymous.PostAsJsonAsync("/api/auth/login", new { email = TestAddress(), password = "" }), 429, "TooManyRequests");
    }

    [Fact]
    public async Task Review8SignupAndWithdrawalHaveRequestLimits()
    {
        using var app = new MarketFactory(isolateJournal: true);
        using var client = app.CreateClient();
        await Register(client, "ReviewWithdraw");
        await CsrfHeader(client);
        for (var i = 0; i < 9; i++)
            await Code(client.PostAsJsonAsync("/api/auth/register", new { }), 400, "InvalidEmail");
        await Code(client.PostAsJsonAsync("/api/auth/register", new { }), 429, "TooManyRequests");
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(new { password = "" }) };
            await Code(client.SendAsync(request), i < 5 ? 401 : 429, i < 5 ? "InvalidCredentials" : "TooManyRequests");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review8RevokedCookieCannotBeReused(bool withdraw)
    {
        using var app = new MarketFactory(isolateJournal: true);
        using var anonymous = app.CreateClient();
        using var replay = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await CsrfHeader(anonymous);
        using var registered = await anonymous.PostAsJsonAsync("/api/auth/register", new { email = TestAddress(), password = TestPassword, nickname = "ReviewRevoke" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var session = registered.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        replay.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", session);
        await using var hub = app.Connect(session);
        var updates = Channel.CreateUnbounded<JsonElement>();
        var resets = Channel.CreateUnbounded<JsonElement>();
        using var accountUpdate = hub.On<JsonElement>("accountUpdate", value => updates.Writer.TryWrite(value));
        using var marketReset = hub.On<JsonElement>("marketReset", value => resets.Writer.TryWrite(value));
        await hub.StartAsync();
        _ = await Read(updates);
        await Status(replay.GetAsync("/api/auth/me"), 200);
        await CsrfHeader(anonymous);
        using var request = new HttpRequestMessage(withdraw ? HttpMethod.Delete : HttpMethod.Post, withdraw ? "/api/auth/me" : "/api/auth/logout");
        if (withdraw) request.Content = JsonContent.Create(new { password = TestPassword });
        await Status(anonymous.SendAsync(request), 204);
        await Code(replay.GetAsync("/api/auth/me"), 401, "Unauthorized");
        using (var reset = await Admin(anonymous, app.Key, "reset")) Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        _ = await Read(resets);
        using var settle = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updates.Reader.ReadAsync(settle.Token).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review8InvalidReplayRefusesHostStart(bool missingRound)
    {
        using var app = new MarketFactory(isolateJournal: true);
        Directory.CreateDirectory(Path.GetDirectoryName(app.JournalPath)!);
        var header = JournalHeader.Create(10_000, new TickSizeTable()) with { MarketGeneration = Guid.NewGuid() };
        using (var writer = new JournalWriter(app.JournalPath, header))
            writer.Append(new PlaceLimitOrder("order", Side.Buy, 10_000, 2, "u-replay", 1, 0));
        if (!missingRound)
            File.WriteAllText(app.JournalPath + $".{header.MarketGeneration:N}.round",
                JsonSerializer.Serialize(new { Round = "2026-11-26", InitialCash = 1, InitialShares = 1 }));
        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(missingRound ? "matching .round" : "Negative ledger balance", failure.ToString());
    }

    [Fact]
    public async Task Review8BundleAloneRestoresStateAndLedgerHashesAndSession()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var client = app.CreateClient();
        await CsrfHeader(client);
        using var registered = await client.PostAsJsonAsync("/api/auth/register", new { email = TestAddress(), password = TestPassword, nickname = "ReviewRecover" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var session = registered.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        await CsrfHeader(client);
        await Place(client, "Buy", 10_000, 4);
        using (var reset = await Admin(client, app.Key, "reset")) Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        await Place(client, "Sell", 10_000, 3);
        var hash = await Get(client, "/api/state/hash");
        await app.Services.GetRequiredService<BackupService>().RunOnce(app.Clock.GetUtcNow().AddMinutes(2));
        var path = Directory.GetFiles(app.BackupDirectory, "*.zip").Order(StringComparer.Ordinal).Last();
        var root = Path.Combine(Path.GetDirectoryName(app.JournalPath)!, "restore");
        ZipFile.ExtractToDirectory(path, root);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "journal"), "market-*.jsonl"));
        using var restored = new MarketFactory(Path.Combine(root, "journal", "market.jsonl"), frozen: true, restoreRoot: root);
        using var restoredClient = restored.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        restoredClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", session);
        await Status(restoredClient.GetAsync("/api/auth/me"), 200);
        AssertHash(hash, await Get(restoredClient, "/api/state/hash"));
    }

    [Fact]
    [Trait("Rule", "R-VI-01")]
    public async Task Review8PartialAuctionFillSettlesOnlyExecutedQuantity()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var buyer = await TradingClient(app);
        using var seller = app.CreateClient();
        await Register(seller, "AuctionSeller");
        await CsrfHeader(seller);
        await Place(seller, "Sell", 10_600, 10);
        var bid = (await Place(buyer, "Buy", 10_600, 15)).GetProperty("orderId").GetString();
        Assert.Equal("WaitingAuction", (await Get(buyer, "/api/account/orders"))[0].GetProperty("status").GetString());
        app.Clock.AddMilliseconds(151_000);
        await WaitUntil(() => Get(buyer, "/api/account/trades").GetAwaiter().GetResult().GetArrayLength() == 1);
        var view = await Get(buyer, "/api/account");
        Assert.Equal(9_841_000, view.GetProperty("cash").GetInt64());
        Assert.Equal(53_000, view.GetProperty("cashOnHold").GetInt64());
        Assert.Equal(1_010, view.GetProperty("shares").GetInt64());
        Assert.Equal(0, (await Get(seller, "/api/account")).GetProperty("sharesOnHold").GetInt64());
        await Status(buyer.DeleteAsync($"/api/orders/{bid}"), 200);
        Assert.Equal(9_894_000, (await Get(buyer, "/api/account")).GetProperty("cash").GetInt64());
    }
    [Fact]
    public async Task Review8SqliteWriteLockTimesOutWithoutCorruptingRoundAndAuditsExpire()
    {
        using var app = new MarketFactory(frozen: true, isolateJournal: true);
        using var client = app.CreateClient();
        await Register(client, "AuditRetention");
        await CsrfHeader(client);
        await WaitUntil(() => Directory.Exists(app.BackupDirectory) && Directory.GetFiles(app.BackupDirectory, "*.zip").Length > 0);
        var id = (await Get(client, "/api/auth/me")).GetProperty("id").GetString();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketDb>();
        db.LoginAudits.AddRange(new LoginAudit(0, app.Clock.GetUtcNow().UtcDateTime.AddDays(-31), false, "unknown", null),
            new LoginAudit(0, app.Clock.GetUtcNow().UtcDateTime, true, "unknown", id));
        await db.SaveChangesAsync();
        Assert.Equal(1, await Members.PruneAudits(db, app.Clock.GetUtcNow(), default));
        var before = await Get(client, "/api/state/hash");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
               { DataSource = app.JournalPath + ".db", Pooling = false }.ToString()))
        {
            connection.Open();
            using var mode = connection.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", mode.ExecuteScalar());
            using var writing = connection.BeginTransaction();
            await Code(Task.FromResult(await Admin(client, app.Key, "reset")), 503, "ServiceUnavailable");
            using var denied = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(new { password = TestPassword }) };
            await Code(client.SendAsync(denied), 503, "ServiceUnavailable");
            await Status(client.GetAsync("/healthz"), 200);
        }
        Assert.Equal(before.GetRawText(), (await Get(client, "/api/state/hash")).GetRawText());
        using var withdraw = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/me") { Content = JsonContent.Create(new { password = TestPassword }) };
        await Status(client.SendAsync(withdraw), 204);
        Assert.Empty(db.LoginAudits.AsEnumerable());
    }
}
