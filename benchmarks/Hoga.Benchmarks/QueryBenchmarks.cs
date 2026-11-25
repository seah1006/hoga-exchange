using BenchmarkDotNet.Attributes;
using Hoga.Engine;

namespace Hoga.Benchmarks;

[MemoryDiagnoser]
public class QueryBenchmarks
{
    private const int BasePrice = 10_000; // 임의 지정
    private MatchingEngine _engine = null!;

    [Params(10, 100, 1_000)] // 호가 단계 수(방향별)
    public int Depth { get; set; }

    [GlobalSetup]
    public void Prepare()
    {
        var random = new Random(42); // 임의 지정
        _engine = new MatchingEngine(BasePrice, new SyntheticTickRule(), new EngineOptions(RandomSeed: 42));
        long seq = 0;
        // 공개 API로 정적 VI에 진입하고 트리거 주문은 제거한다. 논리 시각은 고정한다.
        _engine.Process(new PlaceLimitOrder("trigger-sell", Side.Sell, 11_000, 1, "seller", ++seq, 0));
        _engine.Process(new PlaceLimitOrder("trigger-buy", Side.Buy, 11_000, 1, "buyer", ++seq, 0));
        _engine.Process(new CancelOrder("trigger-sell", ++seq, 0));
        _engine.Process(new CancelOrder("trigger-buy", ++seq, 0));

        // 동일 가격 구간에서 양쪽이 교차하는 실제 단일가 계산 경로.
        for (var level = 0; level < Depth; level++)
        {
            var price = BasePrice - Depth / 2 + level;
            _engine.Process(new PlaceLimitOrder($"bid-{level}", Side.Buy, price, random.Next(1, 11), "buyer", ++seq, 0));
            _engine.Process(new PlaceLimitOrder($"ask-{level}", Side.Sell, price, random.Next(1, 11), "seller", ++seq, 0));
        }

        var book = _engine.GetBook(Depth + 1);
        if (book.Bids.Count != Depth || book.Asks.Count != Depth || _engine.GetIndicativeAuction() is not { Quantity: > 0 })
            throw new InvalidOperationException("Expected a crossed auction book at the requested depth.");
    }

    [Benchmark]
    public IndicativeAuction? IndicativeAuction() => _engine.GetIndicativeAuction();

    [Benchmark]
    public string StateHash() => _engine.ComputeStateHash();

    // KRX 규칙 대신 1원 호가를 사용하는 합성 데이터(임의 지정): 단일가 양쪽 1,000개의 겹치는 단계 확보.
    private sealed class SyntheticTickRule : ITickSizeRule
    {
        public string RuleId => "BENCHMARK-UNIT-TICK/v1";
        public long GetTickSize(long price) => 1;
        public bool IsOnTick(long price) => true;
        public long FloorToTick(long price) => price;
    }
}
