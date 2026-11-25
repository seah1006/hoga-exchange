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
        Assert.Equal(new IndicativeAuction(7830, 600), MatchingEngine.FindAuction(bids, asks, 7850, new TickSizeTable()));
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
    }

    [Theory]
    [InlineData(9990, 10010, 10000, 10000)]
    [InlineData(11000, 11010, 10000, 11000)]
    [InlineData(9000, 9010, 10000, 9010)]
    [Trait("Rule", "R-AUCT-05")]
    public void Actual_crossing_orders_select_reference_or_nearest_boundary(long ask, long bid, long previous, long expected)
    {
        Assert.Equal(new IndicativeAuction(expected, 5), MatchingEngine.FindAuction(
            [new(bid, 5, 1)], [new(ask, 5, 1)], previous, new TickSizeTable()));
    }

    [Theory]
    [InlineData(9390, 9400)]
    [InlineData(9400, 9400)]
    [InlineData(9401, 9410)] // Not an on-tick prior trade; useful for the price selector.
    [InlineData(9410, 9410)]
    [InlineData(9420, 9410)]
    [Trait("Rule", "R-AUCT-02a")]
    public void Adjacent_candidates_without_at_price_fill_can_clear(long previous, long expected)
    {
        Assert.Equal(new IndicativeAuction(expected, 1), MatchingEngine.FindAuction(
            [new(9420, 1, 1), new(9400, 1, 1)], [new(9400, 1, 1), new(9410, 19, 1)],
            previous, new TickSizeTable()));
    }

    [Theory]
    [InlineData(9300, 9410)]
    [InlineData(10000, 10000)]
    [InlineData(10001, 10010)]
    [InlineData(11800, 11750)]
    [Trait("Rule", "R-AUCT-08")]
    public void Empty_tick_prices_prevent_crossed_release_in_regression_book(long previous, long expected)
    {
        Assert.Equal(new IndicativeAuction(expected, 1), MatchingEngine.FindAuction(
            [new(12380, 1, 1), new(9400, 1, 1)], [new(9400, 1, 1), new(11760, 19, 1)],
            previous, new TickSizeTable()));
    }

    [Theory]
    [InlineData(1999, 1999)]
    [InlineData(2000, 2000)]
    [InlineData(2001, 2005)]
    [Trait("Rule", "R-AUCT-08")]
    public void Grid_uses_the_tick_of_each_price_band(long previous, long expected)
    {
        Assert.Equal(new IndicativeAuction(expected, 1), MatchingEngine.FindAuction(
            [new(2010, 1, 1)], [new(1998, 1, 1)], previous, new TickSizeTable()));
    }
}
