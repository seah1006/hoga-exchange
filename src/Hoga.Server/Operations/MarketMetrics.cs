using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Hoga.Engine;

namespace Hoga.Server.Operations;

internal sealed class MarketMetrics : IDisposable
{
    private static readonly double[] Bounds = [.0001, .0005, .001, .005, .01, .05, .1, .5, 1]; // 임의 지정 (초)
    private readonly Meter _meter = new("Hoga.Server.Operations");
    private readonly MeterListener _listener = new();
    private readonly Counter<long> _accepted, _rejected, _trades;
    private readonly Histogram<double> _duration;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _rejections = Enum.GetValues<RejectReason>()
        .ToDictionary(r => r.ToString(), _ => 0L, StringComparer.Ordinal);
    private readonly Distribution[] _latency = [new(), new()];
    private long _acceptedCount, _tradeCount, _connections;
    private Func<int> _queueLength = static () => 0;

    public MarketMetrics()
    {
        _accepted = _meter.CreateCounter<long>("hoga_orders_accepted_total");
        _rejected = _meter.CreateCounter<long>("hoga_orders_rejected_total");
        _trades = _meter.CreateCounter<long>("hoga_trades_total");
        _duration = _meter.CreateHistogram<double>("hoga_market_processing_seconds", "s");
        _meter.CreateObservableGauge("hoga_market_queue_length", () => Volatile.Read(ref _queueLength)());
        _meter.CreateObservableGauge("hoga_connections", () => Interlocked.Read(ref _connections));
        _meter.CreateObservableGauge("hoga_process_working_set_bytes", WorkingSet, "By");
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument == _accepted || instrument == _rejected || instrument == _trades || instrument == _duration)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_gate)
            {
                if (instrument == _accepted) _acceptedCount += value;
                else if (instrument == _trades) _tradeCount += value;
                else _rejections[(string)tags[0].Value!] += value;
            }
        });
        _listener.SetMeasurementEventCallback<double>((_, elapsed, tags, _) =>
        {
            lock (_gate) _latency[(string)tags[0].Value! == "journal" ? 0 : 1].Add(elapsed);
        });
        _listener.Start();
    }

    public void ObserveQueue(Func<int> count) => Volatile.Write(ref _queueLength, count);
    public void Connect() => Interlocked.Increment(ref _connections);
    public void Disconnect() => Interlocked.Decrement(ref _connections);
    public void JournalElapsed(long start) => _duration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, new KeyValuePair<string, object?>("stage", "journal"));
    public void EngineElapsed(long start) => _duration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, new KeyValuePair<string, object?>("stage", "engine"));
    public void Rejected(RejectReason reason) => _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void Observe(OrderCommand command, IReadOnlyList<EngineEvent> events)
    {
        if (command is PlaceLimitOrder)
        {
            var failure = events.OfType<OrderRejected>().SingleOrDefault();
            if (failure is null) _accepted.Add(1);
            else Rejected(failure.Reason);
        }
        var trades = events.Count(e => e is Trade);
        if (trades != 0) _trades.Add(trades);
    }

    public string Render()
    {
        var output = new StringBuilder();
        void Write(string line) => output.Append(line).Append('\n');
        void Count(string key, long value) => Write(FormattableString.Invariant($"{key} {value}"));
        lock (_gate)
        {
            Write("# TYPE hoga_orders_accepted_total counter");
            Count("hoga_orders_accepted_total", _acceptedCount);
            Write("# TYPE hoga_orders_rejected_total counter");
            foreach (var pair in _rejections) Count($"hoga_orders_rejected_total{{reason=\"{pair.Key}\"}}", pair.Value);
            Write("# TYPE hoga_trades_total counter");
            Count("hoga_trades_total", _tradeCount);
            Write("# TYPE hoga_market_processing_seconds histogram");
            for (var stage = 0; stage < _latency.Length; stage++)
            {
                const string metric = "hoga_market_processing_seconds";
                var label = $"stage=\"{(stage == 0 ? "journal" : "engine")}\"";
                var histogram = _latency[stage];
                for (var i = 0; i < Bounds.Length; i++)
                    Count($"{metric}_bucket{{{label},le=\"{Bounds[i].ToString(CultureInfo.InvariantCulture)}\"}}", histogram.Buckets[i]);
                Count($"{metric}_bucket{{{label},le=\"+Inf\"}}", histogram.Count);
                Count($"{metric}_count{{{label}}}", histogram.Count);
                Write(FormattableString.Invariant($"{metric}_sum{{{label}}} {histogram.Sum:G17}"));
            }
        }
        Write("# TYPE hoga_market_queue_length gauge");
        Count("hoga_market_queue_length", Volatile.Read(ref _queueLength)());
        Write("# TYPE hoga_connections gauge");
        Count("hoga_connections", Interlocked.Read(ref _connections));
        Write("# TYPE hoga_process_working_set_bytes gauge");
        Count("hoga_process_working_set_bytes", WorkingSet());
        return output.ToString();
    }

    private static long WorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    public void Dispose() { _listener.Dispose(); _meter.Dispose(); }

    private sealed class Distribution
    {
        public readonly long[] Buckets = new long[Bounds.Length];
        public long Count;
        public double Sum;
        public void Add(double elapsed)
        {
            Count++;
            Sum += elapsed;
            for (var i = 0; i < Bounds.Length; i++) if (elapsed <= Bounds[i]) Buckets[i]++;
        }
    }
}
