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

    [Theory]
    [Trait("Rule", "R-LIMIT-01")]
    [Trait("Rule", "R-LIMIT-02")]
    // RULES §2: floor base * 3 / 10 to the base tick; then floor base ± band to each result's tick.
    // 1,990: 597 → 597 (tick 1); 2,587 → 2,585 (5); 1,393 → 1,393 (1).
    [InlineData(1_990, 2_585, 1_393, 597)]
    // 4,990: 1,497 → 1,495 (5); 6,485 → 6,480 (10); 3,495 → 3,495 (5).
    [InlineData(4_990, 6_480, 3_495, 1_495)]
    // 19,990: 5,997 → 5,990 (10); 25,980 → 25,950 (50); 14,000 → 14,000 (10).
    [InlineData(19_990, 25_950, 14_000, 5_990)]
    // 49,950: 14,985 → 14,950 (50); 64,900 → 64,900 (100); 35,000 → 35,000 (50).
    [InlineData(49_950, 64_900, 35_000, 14_950)]
    // 199,900: 59,970 → 59,900 (100); 259,800 → 259,500 (500); 140,000 → 140,000 (100).
    [InlineData(199_900, 259_500, 140_000, 59_900)]
    // 499,500: 149,850 → 149,500 (500); 649,000 → 649,000 (1,000); 350,000 → 350,000 (500).
    [InlineData(499_500, 649_000, 350_000, 149_500)]
    public void Limits_use_each_results_tick_near_price_boundaries(long price, long upper, long lower, long band) =>
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
