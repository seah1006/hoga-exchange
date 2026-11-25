using System.Globalization;
using System.Threading.Channels;
using Hoga.Engine;
using Hoga.Journal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hoga.Server;

internal sealed class MarketOptions
{
    public long BasePrice { get; set; } = 10_000;
    public int Speed { get; set; } = 1;
    public int RequestTimeoutMs { get; set; } = 5000;
    public string JournalPath { get; set; } = "data/journal/market.jsonl";

    // 서버 입력 한도(임의 지정, KRX 규칙 아님). 엔진·저널에 닿기 전에 막아 호가 합계 오버플로를 차단한다(REVIEW-5 P0-1).
    public long MaxOrderQuantity { get; set; } = 1_000_000;
}

internal sealed class MarketUnavailableException(Exception? inner = null)
    : Exception("The market loop is unavailable.", inner);

internal sealed class MarketLoop(
    IOptions<MarketOptions> configuration, IWebHostEnvironment environment, TimeProvider time,
    IHubContext<MarketHub> hub, ILogger<MarketLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(100);
    private readonly Channel<IRequest> _requests = Channel.CreateBounded<IRequest>(new BoundedChannelOptions(1024)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });

    // Only ExecuteAsync and work run by it access the engine, journal, and projections.
    private MatchingEngine _engine = null!;
    private MarketProjection _projection = null!;
    private JournalWriter? _writer;
    private JournalHeader _header = null!;
    private string _path = null!;
    private long _seq, _timeAtStart, _timeOrigin, _lastBroadcast;
    private bool _restored, _snapshotPending, _broadcastStarted;
    private CancellationToken _stopping;

    internal Task<object> Place(Side side, long price, long quantity, CancellationToken ct) => Invoke(async () =>
    {
        var next = checked(_seq + 1);
        var id = "o-" + next.ToString(CultureInfo.InvariantCulture);
        var events = await Process(new PlaceLimitOrder(id, side, price, quantity, "rest", next, LogicalNow()));
        var failure = events.OfType<OrderRejected>().SingleOrDefault();
        return (object)new { orderId = failure is null ? id : null, accepted = failure is null,
            reason = failure?.Reason.ToString(), ruleId = failure?.RuleId };
    }, ct);

    internal Task<object?> Cancel(string id, CancellationToken ct) => Invoke(async () =>
    {
        if (!_projection.KnowsOrder(id)) return (object?)null;
        var events = await Process(new CancelOrder(id, checked(_seq + 1), LogicalNow()));
        var failure = events.OfType<OrderRejected>().SingleOrDefault();
        return (object?)new { orderId = id, cancelled = failure is null, reason = failure?.Reason.ToString() };
    }, ct);

    internal Task<object?> Explain(string id, CancellationToken ct) =>
        Invoke(() => ValueTask.FromResult(_projection.Explain(_engine, id)), ct);

    internal Task<object> Hash(CancellationToken ct) => Invoke(() => ValueTask.FromResult<object>(new
    {
        seq = _seq, stateHash = _engine.ComputeStateHash(), restoredFromJournal = _restored
    }), ct);

    internal Task<bool> Subscribe(string id, CancellationToken ct) => Invoke(async () =>
    {
        using var send = CancellationTokenSource.CreateLinkedTokenSource(_stopping, ct);
        send.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            // Subscribe in the same loop so the initial state cannot be overtaken by a change broadcast.
            await hub.Clients.Client(id).SendAsync("bookSnapshot", _projection.Snapshot(_engine, _seq), send.Token);
            await hub.Groups.AddToGroupAsync(id, MarketHub.Group, send.Token);
            return true;
        }
        catch (OperationCanceledException) when (send.IsCancellationRequested) { return false; }
    }, ct);

    internal Task Crash(CancellationToken ct) => Invoke(() =>
    {
        var before = _engine.ComputeStateHash();
        _writer!.Dispose();
        _writer = null;
        _engine = null!;
        _projection = null!;
        Restore();
        var after = _engine.ComputeStateHash();
        logger.LogInformation("Crash simulation: seq={Sequence}, before={BeforeHash}, after={AfterHash}", _seq, before, after);
        if (!StringComparer.Ordinal.Equals(before, after)) throw new InvalidDataException("Crash recovery hash mismatch.");
        return ValueTask.FromResult(true);
    }, ct);

    private async Task<T> Invoke<T>(Func<ValueTask<T>> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new Request<T>(work);
        if (!_requests.Writer.TryWrite(request)) throw new MarketUnavailableException();
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(configuration.Value.RequestTimeoutMs), ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            request.SkipIfQueued();
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            _path = Path.GetFullPath(configuration.Value.JournalPath, environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _header = JournalHeader.Create(configuration.Value.BasePrice, new TickSizeTable());
            if (File.Exists(_path)) Restore();
            else
            {
                _writer = new JournalWriter(_path, _header);
                _engine = _header.CreateEngine();
                _projection = new();
                logger.LogInformation("Market started: restoredFromJournal=false, seq=0");
            }
            _timeAtStart = _projection.LogicalTimeMs;
            _timeOrigin = time.GetTimestamp();
            using var timer = new PeriodicTimer(Cadence, time);
            var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var ready = _requests.Reader.WaitToReadAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.WhenAny(tick, ready);
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    await OnTick();
                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                if (ready.IsCompleted)
                {
                    if (!await ready) break;
                    if (_requests.Reader.TryRead(out var request)) await request.Run();
                    ready = _requests.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _requests.Writer.TryComplete();
            while (_requests.Reader.TryRead(out var request)) request.Fail(new MarketUnavailableException());
            _writer?.Dispose();
        }
    }

    private void Restore()
    {
        _writer = JournalWriter.OpenExisting(_path, _header);
        var projection = new MarketProjection();
        var restored = Recovery.Restore(_path, expectedHeader: _header,
            observe: (command, events) => projection.Apply(command, events, publish: false));
        _projection = projection;
        _engine = restored.Engine;
        _seq = restored.LastSequence;
        _restored = true;
        logger.LogInformation("Market restored: restoredFromJournal=true, seq={Sequence}, hash={Hash}", _seq, restored.StateHash);
    }

    private long LogicalNow() => checked(_timeAtStart + (long)time.GetElapsedTime(_timeOrigin).TotalMilliseconds * configuration.Value.Speed);

    private async Task OnTick()
    {
        // Continuous matching does not require periodic input. Avoid idle writes and sequence/hash churn.
        if (_projection.Vi is not null)
            await Process(new AdvanceTime(checked(_seq + 1), LogicalNow()));
        await PublishSnapshotIfDue();
    }

    private async Task<IReadOnlyList<EngineEvent>> Process(OrderCommand command)
    {
        _writer!.Append(command);
        var events = _engine.Process(command);
        _seq = command.Sequence;
        var messages = _projection.Apply(command, events, publish: true);
        if (events.Any(e => e is not OrderRejected) || command is AdvanceTime) _snapshotPending = true;
        foreach (var (name, payload) in messages) await Broadcast(name, payload);
        await PublishSnapshotIfDue();
        return events;
    }

    private async Task PublishSnapshotIfDue()
    {
        if (!_snapshotPending || (_broadcastStarted && time.GetElapsedTime(_lastBroadcast) < Cadence)) return;
        _snapshotPending = false;
        _lastBroadcast = time.GetTimestamp();
        _broadcastStarted = true;
        await Broadcast("bookSnapshot", _projection.Snapshot(_engine, _seq));
    }

    private async Task Broadcast(string name, object payload)
    {
        using var send = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        send.CancelAfter(TimeSpan.FromSeconds(1));
        try { await hub.Clients.Group(MarketHub.Group).SendAsync(name, payload, send.Token); }
        catch (OperationCanceledException) when (send.IsCancellationRequested)
        { logger.LogWarning("Market signal {Event} delivery was cancelled", name); }
    }

    private interface IRequest
    {
        Task Run();
        void Fail(Exception ex);
    }

    private sealed class Request<T>(Func<ValueTask<T>> work) : IRequest
    {
        private int _claimed;
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SkipIfQueued() => Interlocked.CompareExchange(ref _claimed, 1, 0);
        public void Fail(Exception ex)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0) Completion.TrySetException(ex);
        }
        public async Task Run()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            try { Completion.TrySetResult(await work()); }
            catch (Exception ex)
            {
                Completion.TrySetException(new MarketUnavailableException(ex));
                // An input may have reached disk; never continue against partially applied state.
                throw;
            }
        }
    }
}
