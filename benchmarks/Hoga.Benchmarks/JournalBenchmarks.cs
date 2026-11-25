using BenchmarkDotNet.Attributes;
using Hoga.Engine;
using Hoga.Journal;

namespace Hoga.Benchmarks;

[MemoryDiagnoser]
public class JournalBenchmarks
{
    private const int BatchSize = 256; // 임의 지정, 두 flush 설정 모두 끝까지 flush되는 배수
    private OrderCommand[] _commands = null!;
    private JournalWriter _writer = null!;
    private string _path = null!;

    [Params(1, 32)] // 1: 기본값, 32: 임의 지정
    public int FlushEvery { get; set; }

    [GlobalSetup]
    public void PrepareCommands() => _commands = Enumerable.Range(1, BatchSize)
        .Select(i => new PlaceLimitOrder($"order-{i}", Side.Buy, 10_000, 1, "buyer", i, 0)).ToArray();

    [IterationSetup]
    public void Open()
    {
        _path = Path.GetFullPath($"bench-journal-{Guid.NewGuid():N}.jsonl");
        _writer = new JournalWriter(_path, JournalHeader.Create(10_000, new TickSizeTable()), flushEvery: FlushEvery);
    }

    [IterationCleanup]
    public void Close()
    {
        try { _writer.Dispose(); }
        finally { File.Delete(_path); }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void Append()
    {
        foreach (var command in _commands) _writer.Append(command);
    }
}
