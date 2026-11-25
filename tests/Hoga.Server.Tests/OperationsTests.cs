using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hoga.Server.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoga.Server.Tests;

public sealed class OperationsTests
{
    [Fact]
    public async Task MetricsTrustOnlyAdminOrDirectLoopback()
    {
        using var app = new OperationsFactory();
        async Task<int> Scrape(string peer, string? forwarded = null, string? key = null)
        {
            var result = await app.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Path = "/metrics";
                if (forwarded is not null) context.Request.Headers["X-Forwarded-For"] = forwarded;
                if (key is not null) context.Request.Headers["X-Admin-Key"] = key;
            });
            return result.Response.StatusCode;
        }
        Assert.Equal(401, await Scrape("203.0.113.9"));
        Assert.Equal(401, await Scrape("203.0.113.9", key: Guid.NewGuid().ToString("N")));
        Assert.Equal(401, await Scrape("172.18.0.9", "127.0.0.1"));
        Assert.Equal(401, await Scrape("::ffff:127.0.0.1", "203.0.113.9"));
        Assert.Equal(200, await Scrape("::1"));
        Assert.Equal(200, await Scrape("203.0.113.9", key: app.Key));
    }

    [Fact]
    public async Task MetricsAreParseableAndCountAcceptedRejectedTradesAndBothStages()
    {
        using var app = new OperationsFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", app.Key);
        var baseline = await client.GetStringAsync("/metrics");
        Assert.Contains("hoga_orders_accepted_total 0\n", baseline);
        foreach (var (side, price) in new[] { ("Buy", 10000), ("Sell", 10000), ("Buy", 10001) })
        {
            using var order = await client.PostAsJsonAsync("/api/orders", new { side, price, quantity = 1 });
            order.EnsureSuccessStatusCode();
        }
        using var scrape = await client.GetAsync("/metrics");
        Assert.Equal("text/plain", scrape.Content.Headers.ContentType?.MediaType);
        Assert.Contains(scrape.Content.Headers.ContentType!.Parameters, p => p.Name == "version" && p.Value == "0.0.4");
        var text = await scrape.Content.ReadAsStringAsync();
        var samples = new Dictionary<string, double>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) { Assert.Matches("^# TYPE [a-z_]+ (counter|gauge|histogram)$", line); continue; }
            var boundary = line.LastIndexOf(' ');
            Assert.True(boundary > 0);
            Assert.Matches("^[a-z_]+(?:\\{[a-z]+=\"[^\"]+\"(?:,[a-z]+=\"[^\"]+\")?\\})?$", line[..boundary]);
            Assert.True(samples.TryAdd(line[..boundary], double.Parse(line[(boundary + 1)..], CultureInfo.InvariantCulture)));
        }
        Assert.Equal(2, samples["hoga_orders_accepted_total"]);
        Assert.Equal(1, samples["hoga_orders_rejected_total{reason=\"OffTick\"}"]);
        Assert.Equal(1, samples["hoga_trades_total"]);
        Assert.InRange(samples["hoga_market_queue_length"], 0, 1024);
        Assert.Equal(0, samples["hoga_connections"]);
        Assert.True(samples["hoga_process_working_set_bytes"] > 0);
        foreach (var stage in new[] { "journal", "engine" })
        {
            Assert.Equal(3, samples[$"hoga_market_processing_seconds_count{{stage=\"{stage}\"}}"]);
            var buckets = samples.Where(pair => pair.Key.StartsWith($"hoga_market_processing_seconds_bucket{{stage=\"{stage}\"", StringComparison.Ordinal))
                .Select(pair => pair.Value).ToArray();
            Assert.Equal(buckets.Order(), buckets);
            Assert.Equal(3, buckets[^1]);
        }
    }

    [Fact]
    public async Task ProductionRequestLogsExcludeSecretsEmailsCookiesAndRawPaths()
    {
        using var logs = new LogCapture();
        using var app = new OperationsFactory(logs);
        using var client = app.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var cookie = Guid.NewGuid().ToString("N");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/scenario?address={email}");
        req.Headers.Add("X-Admin-Key", app.Key);
        req.Headers.Add("Cookie", $"sample={cookie}");
        req.Content = JsonContent.Create(new { name = email });
        using var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var unknown = await client.GetAsync($"/lookup/{email}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var requestLines = logs.Entries.Where(e => e.Category == typeof(SafeRequestLog).FullName).Select(e => e.Text).ToArray();
        Assert.Contains(requestLines, line => line.Contains("/api/admin/scenario returned 400", StringComparison.Ordinal));
        Assert.Contains(requestLines, line => line.Contains("(unmatched) returned 404", StringComparison.Ordinal));
        var output = string.Join('\n', requestLines);
        Assert.DoesNotContain(app.Key, output);
        Assert.DoesNotContain(email, output);
        Assert.DoesNotContain(cookie, output);

        var logger = app.Services.GetRequiredService<ILogger<SafeRequestLog>>();
        var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        var middleware = new SafeRequestLog(_ => throw new InvalidOperationException(email), logger, environment);
        var hit = new DefaultHttpContext();
        hit.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, email)], "test"));
        await middleware.InvokeAsync(hit);
        Assert.Equal(500, hit.Response.StatusCode);
        var hashed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)).AsSpan(0, 6));
        Assert.Contains(logs.Entries, e => e.Text.Contains(hashed, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries.Where(e => e.Category == typeof(SafeRequestLog).FullName), e => e.Text.Contains(email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackupRetainsSevenSeoulDatesAndSkipsAbsentDatabaseOrSidecars()
    {
        using var logs = new LogCapture();
        using var app = new OperationsFactory(logs);
        var services = app.Services;
        var service = services.GetRequiredService<BackupService>();
        var earliest = DateTimeOffset.Parse("2026-11-01T15:30:00Z", CultureInfo.InvariantCulture);
        await service.RunOnce(earliest);
        Assert.False(Directory.Exists(app.Backups));
        Assert.Contains(logs.Entries, entry => entry.Text.Contains("database missing", StringComparison.Ordinal));
        // gzip of a SQLite database created with CREATE TABLE checks and INSERT of one fixture row.
        const string fixture = "H4sIAAAAAAAACu3XMQrCUAwG4LwqDoLUTcQlo4K4eAGrPKRYResb7FjrKxYrQq3i4jG8hhfxOh5ApLq46CA4+H+QQEIyZMx04kSp5nCTrP2U21QmIajDTETGI54EEeVf6ncMap0upfuweSbz/MEGAAAAAAAAwN8bi0KlWhXH1J/HOljqYLXNstFzpaUkK6vrSM56XI8WbI+U7EuXx649tFyPB9Jr8t6Pd5qVnKlG9ptfybz++jYAAAAAAAAA+KaiyFEtjA7pLtE3aQ/rUgAgAAA=";
        using (var source = new GZipStream(new MemoryStream(Convert.FromBase64String(fixture)), CompressionMode.Decompress))
        await using (var file = File.Create(app.Database)) await source.CopyToAsync(file);
        await File.WriteAllTextAsync(app.Database + "-wal", "");
        await service.RunOnce(earliest);
        Assert.Empty(Directory.EnumerateFiles(app.Backups));
        File.Delete(app.Database + "-wal");
        for (var i = 0; i < 9; i++) await service.RunOnce(earliest.AddDays(i));
        var actual = Directory.GetFiles(app.Backups).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(7, actual.Length);
        Assert.Equal("hoga-20261104.db", Path.GetFileName(actual[0]));
        Assert.Equal("hoga-20261110.db", Path.GetFileName(actual[^1]));
        Assert.Equal(await File.ReadAllBytesAsync(app.Database), await File.ReadAllBytesAsync(actual[^1]));
        await service.RunOnce(earliest.AddDays(8));
        Assert.Equal(actual, Directory.GetFiles(app.Backups).Order(StringComparer.Ordinal));
    }

    private sealed class OperationsFactory(LogCapture? logs = null) : WebApplicationFactory<Program>
    {
        private readonly string _directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"))).FullName;
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Backups => Path.Combine(_directory, "backups");
        public string Database => Path.Combine(_directory, "hoga.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Market:AdminKey"] = Key, ["Market:JournalPath"] = Path.Combine(_directory, "journal.jsonl"),
                ["Market:DatabasePath"] = Database, ["Market:TrustedProxyNetwork"] = "172.18.0.0/24", ["Market:Bots"] = "false",
                ["Operations:BackupDirectory"] = Backups, ["Operations:AllowOfflineDatabaseCopy"] = "true"
            })).ConfigureLogging(logging => { if (logs is not null) logging.AddProvider(logs); });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, string Text)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, formatter(state, exception)));
        }
    }
}
