namespace Hoga.Engine.Tests;

public class PriceRuleTests
{
    private static readonly TickSizeTable Rule = new();

    [Theory]
    [Trait("Rule", "R-TICK-01")]
    [Trait("Rule", "R-TICK-02")]
    [InlineData(1_999, 1, true, 1_999)]
    [InlineData(2_000, 5, true, 2_000)]
    [InlineData(4_999, 5, false, 4_995)]
    [InlineData(5_000, 10, true, 5_000)]
    [InlineData(19_999, 10, false, 19_990)]
    [InlineData(20_000, 50, true, 20_000)]
    [InlineData(49_999, 50, false, 49_950)]
    [InlineData(50_000, 100, true, 50_000)]
    [InlineData(199_999, 100, false, 199_900)]
    [InlineData(200_000, 500, true, 200_000)]
    [InlineData(499_999, 500, false, 499_500)]
    [InlineData(500_000, 1_000, true, 500_000)]
    public void Tick_boundaries(long price, long tick, bool isOnTick, long floor)
    {
        Assert.Equal(tick, Rule.GetTickSize(price));
        Assert.Equal(isOnTick, Rule.IsOnTick(price));
        Assert.Equal(floor, Rule.FloorToTick(price));
    }

    [Theory]
    [Trait("Rule", "R-LIMIT-01")]
    [Trait("Rule", "R-LIMIT-02")]
    [Trait("Rule", "R-LIMIT-03")]
    [InlineData(9_940, 12_920, 6_960, 2_980)]
    [InlineData(9_980, 12_970, 6_990, 2_990)]
    [InlineData(19_990, 25_950, 14_000, 5_990)]
    [InlineData(3, 4, 2, 1)]
    public void Specified_limit_examples(long price, long upper, long lower, long band) =>
        Assert.Equal((upper, lower, band), PriceLimits.Compute(price, Rule));

    [Fact]
    [Trait("Rule", "R-LIMIT-01")]
    public void Max_base_price_computes_without_overflow() =>
        Assert.Equal((13_000_000L, 7_000_000L, 3_000_000L), PriceLimits.Compute(PriceLimits.MaxBasePrice, Rule));

    [Theory]
    [InlineData(0)]
    [InlineData(10_000_001)]
    [InlineData(long.MaxValue)]
    public void Rejects_unsupported_base_prices(long basePrice) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceLimits.Compute(basePrice, Rule));
}
