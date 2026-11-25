using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

// 임의 지정: 루프백, 기본 포트/클라이언트/요청 수, 시드, 타임아웃과 사전 가열 횟수.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Usage(Console.Out);
    return 0;
}
var url = args.Length > 0 ? args[0] : "http://127.0.0.1:5290";
var clients = 1;
var requests = 4_096;
if (args.Length > 3 || !Uri.TryCreate(url, UriKind.Absolute, out var server) || !server.IsLoopback ||
    server.Scheme != Uri.UriSchemeHttp || server.AbsolutePath != "/" ||
    server.Query.Length != 0 || server.Fragment.Length != 0 || server.UserInfo.Length != 0 ||
    (args.Length > 1 && !int.TryParse(args[1], out clients)) || clients is < 1 or > 256 ||
    (args.Length > 2 && !int.TryParse(args[2], out requests)) || requests is < 4 or > 1_000_000 ||
    requests % (clients * 4) != 0)
{
    Usage(Console.Error);
    return 2;
}

const int seed = 42;
const int warmupPerClient = 16;
using var stageBudget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = clients,
    AllowAutoRedirect = false,
    UseProxy = false
}) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(10) };
try
{
    var warmup = await RunPhase(warmupPerClient, measured: false);
    var warmupErrors = warmup.Samples.Count(sample => sample.Result is not (Result.Accepted or Result.Rejected));
    if (warmupErrors != 0)
    {
        var counts = string.Join(", ", warmup.Samples.GroupBy(sample => sample.Result)
            .Select(group => $"{group.Key}={group.Count()}"));
        Console.Error.WriteLine($"Warmup failed: {counts}");
        return 1;
    }

    var (samples, seconds) = await RunPhase(requests / clients, measured: true);
    var accepted = samples.Count(sample => sample.Result == Result.Accepted);
    var rejected = samples.Count(sample => sample.Result == Result.Rejected);
    var httpErrors = samples.Count(sample => sample.Result == Result.HttpError);
    var ioErrors = samples.Count(sample => sample.Result == Result.IoError);
    var timeouts = samples.Count(sample => sample.Result == Result.Timeout);
    var protocolErrors = samples.Count(sample => sample.Result == Result.ProtocolError);
    var latencies = samples.Select(sample => sample.Milliseconds).Order().ToArray();
    Console.WriteLine($"clients={clients} requests={requests} seed={seed} warmup/client={warmupPerClient} elapsed={seconds:F3}s");
    Console.WriteLine($"completed={requests / seconds:F2} ops/s application={(accepted + rejected) / seconds:F2} ops/s");
    Console.WriteLine($"p50={Percentile(latencies, .50):F3} ms p95={Percentile(latencies, .95):F3} ms " +
        $"p99={Percentile(latencies, .99):F3} ms max={latencies[^1]:F3} ms");
    Console.WriteLine($"accepted={accepted} rejected={rejected} http_errors={httpErrors} io_errors={ioErrors} " +
        $"timeouts={timeouts} protocol_errors={protocolErrors}");
    return accepted == requests * 3 / 4 && rejected == requests / 4 ? 0 : 1;
}
catch (OperationCanceledException) when (stageBudget.IsCancellationRequested)
{
    Console.Error.WriteLine("Load test exceeded the time budget; no complete sample set.");
    return 1;
}

async Task<(Sample[] Samples, double Seconds)> RunPhase(int perClient, bool measured)
{
    var samples = new Sample[clients * perClient];
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var workers = Enumerable.Range(0, clients).Select(Worker).ToArray();
    var start = Stopwatch.GetTimestamp();
    gate.SetResult();
    await Task.WhenAll(workers);
    return (samples, Stopwatch.GetElapsedTime(start).TotalSeconds);

    async Task Worker(int client)
    {
        var random = new Random(seed + client * 2 + (measured ? 1 : 0));
        var pairQuantity = 0;
        await gate.Task;
        for (var i = 0; i < perClient; i++)
        {
            if (i % 4 == 0) pairQuantity = random.Next(1, 11);
            var order = (i % 4) switch
            {
                0 => new Order("Sell", 10_000, pairQuantity),
                1 => new Order("Buy", 10_000, pairQuantity),
                2 => random.Next(2) == 0 ? new Order("Buy", 9_900, 1) : new Order("Sell", 10_100, 1),
                _ => new Order("Buy", 10_000, 0)
            };
            var start = Stopwatch.GetTimestamp();
            var result = await Submit(order);
            samples[client * perClient + i] = new(Stopwatch.GetElapsedTime(start).TotalMilliseconds, result);
        }
    }
}

async Task<Result> Submit(Order order)
{
    try
    {
        using var response = await http.PostAsJsonAsync("/api/orders", order, stageBudget.Token);
        if (!response.IsSuccessStatusCode) return Result.HttpError;
        var reply = await response.Content.ReadFromJsonAsync<Reply>(cancellationToken: stageBudget.Token);
        return reply switch
        {
            { Accepted: true, OrderId.Length: > 0 } when order.Quantity > 0 => Result.Accepted,
            { Accepted: false, Reason: "InvalidQuantity" } when order.Quantity == 0 => Result.Rejected,
            _ => Result.ProtocolError
        };
    }
    catch (HttpRequestException) { return Result.IoError; }
    catch (IOException) { return Result.IoError; }
    catch (JsonException) { return Result.ProtocolError; }
    catch (OperationCanceledException) when (!stageBudget.IsCancellationRequested) { return Result.Timeout; }
}

static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
static void Usage(TextWriter output) => output.WriteLine(
    "Usage: Hoga.LoadTest [http://127.0.0.1:5290] [clients=1] [total-requests=4096]; " +
    "loopback HTTP only, total-requests must be a positive multiple of 4 * clients.");

internal sealed record Order(string Side, long Price, long Quantity);
internal sealed record Reply(bool? Accepted, string? OrderId, string? Reason);
internal readonly record struct Sample(double Milliseconds, Result Result);
internal enum Result { Accepted, Rejected, HttpError, IoError, Timeout, ProtocolError }
