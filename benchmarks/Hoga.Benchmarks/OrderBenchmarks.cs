using BenchmarkDotNet.Attributes;
using Hoga.Engine;

namespace Hoga.Benchmarks;

[MemoryDiagnoser]
public class OrderBenchmarks
{
    private const int BatchSize = 256; // 임의 지정
    private const int Depth = 16; // 임의 지정: 각 방향의 호가 단계 수
    private const int SweepDepth = 8; // 임의 지정
    private MatchingEngine[] _engines = null!;
    private OrderCommand[] _commands = null!;

    [IterationSetup(Target = nameof(Rest))]
    public void PrepareRest() => Prepare(nameof(Rest));

    [IterationSetup(Target = nameof(MatchOne))]
    public void PrepareMatchOne() => Prepare(nameof(MatchOne));

    [IterationSetup(Target = nameof(SweepEight))]
    public void PrepareSweepEight() => Prepare(nameof(SweepEight));

    [IterationSetup(Target = nameof(Cancel))]
    public void PrepareCancel() => Prepare(nameof(Cancel));

    private void Prepare(string scenario)
    {
        var random = new Random(42); // 임의 지정
        _engines = new MatchingEngine[BatchSize];
        _commands = new OrderCommand[BatchSize];
        for (var batch = 0; batch < BatchSize; batch++)
        {
            var engine = new MatchingEngine(10_000, new TickSizeTable(), new EngineOptions(ViEnabled: false));
            long seq = 0, firstAskQuantity = 0, sweepQuantity = 0;
            for (var level = 0; level < Depth; level++)
            {
                var bidQuantity = random.Next(1, 11);
                var askQuantity = random.Next(1, 11);
                if (level == 0) firstAskQuantity = askQuantity;
                if (level < SweepDepth) sweepQuantity += askQuantity;
                engine.Process(new PlaceLimitOrder($"bid-{level}", Side.Buy, 9_990 - 10 * level, bidQuantity,
                    "bidder", ++seq, 0));
                engine.Process(new PlaceLimitOrder($"ask-{level}", Side.Sell, 10_010 + 10 * level, askQuantity,
                    "seller", ++seq, 0));
            }

            _engines[batch] = engine;
            ++seq;
            _commands[batch] = scenario switch
            {
                nameof(Rest) => new PlaceLimitOrder("new", Side.Buy, 9_990, 1, "buyer", seq, 0),
                nameof(MatchOne) => new PlaceLimitOrder("new", Side.Buy, 10_010, firstAskQuantity, "buyer", seq, 0),
                nameof(SweepEight) => new PlaceLimitOrder("new", Side.Buy, 10_010 + 10 * (SweepDepth - 1),
                    sweepQuantity, "buyer", seq, 0),
                nameof(Cancel) => new CancelOrder("bid-0", seq, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
        }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void Rest() => ProcessBatch();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void MatchOne() => ProcessBatch();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void SweepEight() => ProcessBatch();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void Cancel() => ProcessBatch();

    private void ProcessBatch()
    {
        for (var i = 0; i < BatchSize; i++) _engines[i].Process(_commands[i]);
    }
}
