using System.Globalization;
using System.Threading.Channels;
using Hoga.Engine;
using Hoga.Bots;
using Hoga.Journal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hoga.Server;

internal sealed class MarketOptions
{
    public bool Bots { get; set; } = true;
    public long BotSeed { get; set; } = 42; // 임의 지정
    public double BotIntensity { get; set; } = 1.0;
    public string? DailyResetLocalTime { get; set; } = "05:00"; // Asia/Seoul, 임의 지정

    internal static bool TryParseResetTime(string? value, out TimeOnly localTime) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);
    public long InitialCash { get; set; } = 10_000_000; // 임의 지정
    public long InitialShares { get; set; } = 1_000; // 임의 지정
    public long BasePrice { get; set; } = 10_000;
    public int Speed { get; set; } = 1;
    public int RequestTimeoutMs { get; set; } = 5000;
    public string JournalPath { get; set; } = "data/journal/market.jsonl";

    // 서버 입력 한도(임의 지정, KRX 규칙 아님). 엔진·저널에 닿기 전에 막아 호가 합계 오버플로를 차단한다(REVIEW-5 P0-1).
    public long MaxOrderQuantity { get; set; } = 1_000_000;
}

internal sealed class MarketUnavailableException(Exception? inner = null)
    : Exception("The market loop is unavailable.", inner);

internal sealed partial class MarketLoop(
    IOptions<MarketOptions> configuration, IWebHostEnvironment environment, TimeProvider time,
    IHubContext<MarketHub> hub, ILogger<MarketLoop> logger, IServiceScopeFactory scopes) : BackgroundService
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
    private readonly SemaphoreSlim _expiredRead = new(1); // 임의 지정: cap concurrent journal scans.
    private LiveBots? _bots;
    private long _nextBotTick;
    private bool _botBatchQueued;
    private long _journalAttempts, _generation;
    private TimeOnly? _resetTime;
    private DateTimeOffset? _nextDailyReset;
    private TimeZoneInfo? _seoul;

    internal Task<long> Health(CancellationToken ct) => Read(() => _seq, ct);

    internal Task<bool> Reset(CancellationToken ct) => Invoke(() => ResetMarket("Admin", time.GetUtcNow()), ct);

    internal Task SelectScenario(Scenario scenario, CancellationToken ct) => Invoke(async () =>
    {
        if (_bots is not null)
            await _bots.Select(scenario, LogicalNow(), id => CancelBot(id, LogicalNow()));
        return true;
    }, ct);

    internal Task<T> Read<T>(Func<T> work, CancellationToken ct) =>
        Invoke(() => ValueTask.FromResult(work()), ct, readOnly: true);

    internal Task<object> Place(string owner, Side side, long price, long quantity, CancellationToken ct) => Invoke(async () =>
    {
        var next = checked(_seq + 1);
        var id = OrderId(next);
        if (_withdrawn.Contains(owner)) return (object)new { orderId = (string?)null, accepted = false, reason = "UnknownOrder", ruleId = (string?)null };
        var command = new PlaceLimitOrder(id, side, price, quantity, owner, next, LogicalNow());
        if (_ledger.Check(command) is { } reason)
            return (object)new { orderId = (string?)null, accepted = false, reason, ruleId = (string?)null };
        var events = await Process(command);
        var failure = events.OfType<OrderRejected>().SingleOrDefault();
        return (object)new { orderId = failure is null ? id : null, accepted = failure is null,
            reason = failure?.Reason.ToString(), ruleId = failure?.RuleId };
    }, ct);

    internal Task<object?> Cancel(string owner, string id, CancellationToken ct) => Invoke(async () =>
    {
        if (!_projection.OwnsOrder(id, owner)) return (object?)null;
        var events = await Process(new CancelOrder(id, checked(_seq + 1), LogicalNow()));
        var failure = events.OfType<OrderRejected>().SingleOrDefault();
        return (object?)new { orderId = id, cancelled = failure is null, reason = failure?.Reason.ToString() };
    }, ct);

    internal async Task<(object? Result, bool Expired)> ExplainOrder(string owner, string id, CancellationToken ct)
    {
        var (result, seq, generation) = await Read(() =>
        {
            var prefix = OrderIdPrefix;
            var candidate = id.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) &&
                _projection.MayHaveExpired(sequence) && id == OrderId(sequence) ? sequence : 0;
            return (_projection.OwnsOrder(id, owner) ? _projection.Explain(_engine, id) : null, candidate, _generation);
        }, ct);
        if (result is not null || seq == 0) return (result, false);
        // No unbounded tombstones: verify against disk outside the market loop.
        await _expiredRead.WaitAsync(ct);
        try
        {
            if (generation != _generation) return (null, false);
            var expired = await Task.Run(() => JournalReader.Read(_path).Commands
                .TakeWhile(c => { ct.ThrowIfCancellationRequested(); return c.Sequence <= seq; })
                .Any(c => c.Sequence == seq && c is PlaceLimitOrder p && p.OrderId == id && p.AccountId == owner), ct);
            return (null, expired);
        }
        finally { _expiredRead.Release(); }
    }

    internal Task<object> Hash(CancellationToken ct) => Read<object>(() => new
    {
        seq = _seq, stateHash = _engine.ComputeStateHash(), ledgerHash = _ledger.Hash(), restoredFromJournal = _restored
    }, ct);

    internal Task<bool> Subscribe(string id, string? owner, CancellationToken ct) => Invoke(async () =>
    {
        using var send = CancellationTokenSource.CreateLinkedTokenSource(_stopping, ct);
        send.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            // Subscribe in the same loop so the initial state cannot be overtaken by a change broadcast.
            var client = hub.Clients.Client(id);
            foreach (var candle in _projection.Candles) await client.SendAsync("candle", candle, send.Token);
            foreach (var trade in _projection.Trades) await client.SendAsync("trade", trade, send.Token);
            await client.SendAsync("bookSnapshot", _projection.Snapshot(_engine, _seq), send.Token);
            if (owner is not null && !_withdrawn.Contains(owner)) await client.SendAsync("accountUpdate", AccountView(owner), send.Token);
            await hub.Groups.AddToGroupAsync(id, MarketHub.Group, send.Token);
            if (owner is not null && !_withdrawn.Contains(owner)) _online[id] = owner;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Market subscription delivery failed");
            return false;
        }
    }, ct, readOnly: true);

    internal Task Crash(CancellationToken ct) => Invoke(() =>
    {
        var before = _engine.ComputeStateHash();
        var beforeLedger = _ledger.Hash();
        _writer!.Dispose();
        _writer = null;
        _engine = null!;
        _projection = null!;
        Restore();
        var after = _engine.ComputeStateHash();
        logger.LogInformation("Crash simulation: seq={Sequence}, before={BeforeHash}, after={AfterHash}", _seq, before, after);
        if (!StringComparer.Ordinal.Equals(before, after) || beforeLedger != _ledger.Hash()) throw new InvalidDataException("Crash recovery hash mismatch.");
        return ValueTask.FromResult(true);
    }, ct);

    private async Task<T> Invoke<T>(Func<ValueTask<T>> work, CancellationToken ct, bool readOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        var request = new Request<T>(work, readOnly);
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
            _bots = CreateBots();
            if (File.Exists(_path))
            {
                _header = _header with { MarketGeneration = JournalReader.Read(_path).Header.MarketGeneration };
                Restore(rebuildBots: true);
            }
            else StartEmpty(time.GetUtcNow());
            RestartClock();
            if (MarketOptions.TryParseResetTime(configuration.Value.DailyResetLocalTime, out var localTime))
            {
                _seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
                _resetTime = localTime;
                ScheduleDailyReset(time.GetUtcNow());
            }
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

    private LiveBots? CreateBots() => configuration.Value.Bots
        ? new(configuration.Value.BotSeed, configuration.Value.BasePrice, configuration.Value.BotIntensity) : null;

    private string OrderIdPrefix => _header.MarketGeneration is { } generation ? $"o-{generation:N}-" : "o-";
    private string OrderId(long sequence) => OrderIdPrefix + sequence.ToString(CultureInfo.InvariantCulture);

    private void StartEmpty(DateTimeOffset now)
    {
        _header = _header with { MarketGeneration = Guid.NewGuid() };
        LoadRound(now);
        _writer = new JournalWriter(_path, _header);
        _engine = _header.CreateEngine();
        _projection = new();
        _seq = 0;
        RefreshRanking(now);
        _restored = _snapshotPending = _broadcastStarted = false;
        logger.LogInformation("Market started: restoredFromJournal=false, seq=0");
    }

    private void RestartClock()
    {
        _timeAtStart = _projection.LogicalTimeMs;
        _timeOrigin = time.GetTimestamp();
        _nextBotTick = (_timeAtStart / LiveBots.TickMs + 1) * LiveBots.TickMs;
    }

    private void ScheduleDailyReset(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _seoul!);
        var target = local.Date + _resetTime!.Value.ToTimeSpan();
        if (target <= local.DateTime) target = target.AddDays(1);
        _nextDailyReset = TimeZoneInfo.ConvertTimeToUtc(target, _seoul!);
    }

    private async ValueTask<bool> ResetMarket(string reason, DateTimeOffset now)
    {
        // The expired-order reader opens the live journal outside this queue. Drain that single reader
        // before rotating (Windows disallows renaming a file with its read handle open).
        await _expiredRead.WaitAsync(_stopping);
        try
        {
            if (!await SaveRound(now)) return false;
            var previousRoundPath = RoundPath;
            var directory = Path.GetDirectoryName(_path)!;
            var archiveTime = TimeZoneInfo.ConvertTime(now, _seoul ?? TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
            string archive;
            do
            {
                archive = Path.Combine(directory, "market-" + archiveTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
                archiveTime = archiveTime.AddSeconds(1); // Same-second admin resets must never overwrite an archive.
            }
            while (File.Exists(archive));
            _writer!.Dispose();
            _writer = null;
            try { File.Move(_path, archive); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Market reset cancelled: could not archive the current journal");
                _writer = JournalWriter.OpenExisting(_path, _header);
                if (_writer.LastSequence != _seq)
                    throw new InvalidDataException("Journal sequence changed during failed rotation.");
                return false;
            }
            _generation++;
            StartEmpty(now);
            try { File.Delete(previousRoundPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning("Could not prune previous round metadata"); }
            _bots = CreateBots();
            RestartClock();
            foreach (var old in Directory.EnumerateFiles(directory, "market-????????-??????.jsonl")
                .Where(p => p != _path && p != archive)
                .Where(p => DateTime.TryParseExact(Path.GetFileName(p), "'market-'yyyyMMdd-HHmmss'.jsonl'",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .OrderDescending(StringComparer.Ordinal).Skip(1)) // 임의 지정: retain this archive plus the latest previous one.
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "Could not prune old market journal"); }
            }
        }
        finally { _expiredRead.Release(); }
        await Broadcast("marketReset", new { symbol = "HOGA", atUtc = now.UtcDateTime, reason });
        foreach (var id in _online.Values.Distinct(StringComparer.Ordinal)) await PublishAccount(id);
        _snapshotPending = true;
        await PublishSnapshotIfDue();
        return true;
    }

    private void Restore(bool rebuildBots = false)
    {
        _writer = JournalWriter.OpenExisting(_path, _header);
        var projection = new MarketProjection();
        LoadRound(time.GetUtcNow());
        var restored = Recovery.Restore(_path, expectedHeader: _header,
            observe: (command, events) =>
            {
                projection.Apply(command, events, publish: false);
                _ledger.Apply(command, events);
                if (rebuildBots) _bots?.Observe(command, events);
            });
        _projection = projection;
        _engine = restored.Engine;
        _seq = restored.LastSequence;
        _restored = true;
        RefreshRanking(time.GetUtcNow());
        logger.LogInformation("Market restored: restoredFromJournal=true, seq={Sequence}, hash={Hash}", _seq, restored.StateHash);
    }

    private long LogicalNow() => checked(_timeAtStart + (long)time.GetElapsedTime(_timeOrigin).TotalMilliseconds * configuration.Value.Speed);

    private async Task OnTick()
    {
        if (_nextDailyReset is { } due && time.GetUtcNow() is var now && now >= due)
        {
            if (await ResetMarket("Daily", now)) ScheduleDailyReset(now);
            else _nextDailyReset = now.AddMinutes(5); // 임의 지정: avoid hammering a failing filesystem.
        }
        if (time.GetElapsedTime(_lastRanking) >= TimeSpan.FromSeconds(10)) RefreshRanking(time.GetUtcNow());
        // Continuous matching does not require periodic input. Avoid idle writes and sequence/hash churn.
        if (_projection.Vi is not null)
            await Process(new AdvanceTime(checked(_seq + 1), LogicalNow()));
        if (_bots is not null && !_botBatchQueued && _nextBotTick <= LogicalNow())
            _botBatchQueued = _requests.Writer.TryWrite(new BotBatch(RunBots));
        await PublishSnapshotIfDue();
    }

    private async Task RunBots()
    {
        _botBatchQueued = false;
        var attempts = _journalAttempts;
        try { await RunBotTicks(); }
        catch (Exception ex) when (_journalAttempts == attempts && !_stopping.IsCancellationRequested)
        { logger.LogWarning(ex, "Discarding bot batch that did not reach the journal"); }
    }

    private async Task RunBotTicks()
    {
        var now = LogicalNow();
        // At most one bounded batch is queued; yield to REST after each batch.
        for (var i = 0; i < 20 && _nextBotTick <= now && !_stopping.IsCancellationRequested; i++)
        {
            var tick = _nextBotTick;
            _nextBotTick += LiveBots.TickMs;
            await _bots!.Step(tick, _engine, id => CancelBot(id, tick), async (account, side, price, quantity) =>
            {
                var next = checked(_seq + 1);
                await Process(new PlaceLimitOrder(OrderId(next), side,
                    price, quantity, "bot:" + account, next, Math.Max(tick, _projection.LogicalTimeMs)));
            });
        }
    }

    private async Task CancelBot(string id, long logicalTime) =>
        await Process(new CancelOrder(id, checked(_seq + 1), Math.Max(logicalTime, _projection.LogicalTimeMs)));

    private async Task<IReadOnlyList<EngineEvent>> Process(OrderCommand command)
    {
        _journalAttempts++; // An I/O error may follow a partial write, so never suppress append failures.
        _writer!.Append(command);
        var events = _engine.Process(command);
        _seq = command.Sequence;
        _bots?.Observe(command, events);
        var messages = _projection.Apply(command, events, publish: true);
        var accounts = _ledger.Apply(command, events);
        if (events.Any(e => e is not OrderRejected) || command is AdvanceTime) _snapshotPending = true;
        foreach (var (name, payload) in messages) await Broadcast(name, payload);
        foreach (var id in accounts) await PublishAccount(id);
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
        catch (Exception ex)
        { logger.LogWarning(ex, "Market signal {Event} delivery failed", name); }
    }

    private interface IRequest
    {
        Task Run();
        void Fail(Exception ex);
    }

    private sealed class BotBatch(Func<Task> run) : IRequest
    {
        public Task Run() => run();
        public void Fail(Exception ex) { }
    }

    private sealed class Request<T>(Func<ValueTask<T>> work, bool readOnly) : IRequest
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
                if (readOnly) return;
                // An input may have reached disk; never continue against partially applied state.
                throw;
            }
        }
    }
}
