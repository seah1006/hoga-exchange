namespace Hoga.Engine.Tests;

public sealed class AuctionTimingTests
{
    [Fact]
    [Trait("Rule", "R-AUCT-04")]
    public void SplitMix64_matches_reference_vector()
    {
        var generator = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, generator.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, generator.Next());
        Assert.Equal(0x06C45D188009454FUL, generator.Next());
    }

    [Fact]
    public void Advance_time_without_auction_consumes_sequence_without_events()
    {
        var engine = new MatchingEngine(10_000, new TickSizeTable());
        Assert.Equal(TradingPhase.Continuous, engine.Phase);
        Assert.Empty(engine.Process(new AdvanceTime(1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Process(new AdvanceTime(1, 3)));
    }
}
