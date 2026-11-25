namespace Hoga.Engine.Tests;

public sealed class AuctionPriceTests
{
    [Fact]
    [Trait("Rule", "R-AUCT-01")]
    [Trait("Rule", "R-AUCT-02")]
    public void Krx_book_clears_at_7830_for_600()
    {
        BookLevel[] bids = [new(7850, 100, 1), new(7840, 150, 1), new(7830, 350, 2),
            new(7820, 400, 1), new(7810, 500, 1), new(7800, 650, 1)];
        BookLevel[] asks = [new(7810, 150, 1), new(7820, 200, 1), new(7830, 400, 3),
            new(7840, 300, 1), new(7850, 750, 1), new(7860, 450, 1)];
        Assert.Equal(new IndicativeAuction(7830, 600), MatchingEngine.FindAuction(bids, asks, 7850));
    }

    [Theory]
    [InlineData(new long[] { 9000, 10000, 11000 }, 10000)]
    [InlineData(new long[] { 11000, 12000 }, 11000)]
    [InlineData(new long[] { 9000, 11000 }, 11000)]
    [InlineData(new long[] { 9000, 12000 }, 12000)]
    [InlineData(new long[] { 9000, 9500 }, 9500)]
    [Trait("Rule", "R-AUCT-05")]
    public void Rule_examples_choose_expected_price(long[] prices, long expected)
    {
        Assert.Equal(expected, MatchingEngine.ChooseAuctionPrice(prices, 10000));
        // Two quoted levels with equally sized crossing orders realize the two-candidate cases.
        if (prices.Length == 2)
            Assert.Equal(new IndicativeAuction(expected, 5), MatchingEngine.FindAuction(
                [new(prices[1], 5, 1)], [new(prices[0], 5, 1)], 10000));
    }

    [Fact]
    [Trait("Rule", "R-AUCT-02a")]
    public void Equal_cumulative_quantities_at_two_quoted_prices_can_clear()
    {
        Assert.Equal(new IndicativeAuction(10200, 5), MatchingEngine.FindAuction(
            [new(10200, 5, 1)], [new(10000, 5, 1)], 10100));
    }
}
