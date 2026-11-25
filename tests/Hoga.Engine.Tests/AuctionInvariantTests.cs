using FsCheck.Xunit;

namespace Hoga.Engine.Tests;

public sealed class AuctionInvariantTests
{
    [Property(MaxTest = 200)]
    [Trait("Rule", "R-VI-01")]
    [Trait("Rule", "R-VI-03")]
    [Trait("Rule", "R-VI-06")]
    public void I7_continuous_trades_do_not_touch_either_trigger(int[] input, bool kospi200)
    {
        var s = new VolatilityTests.Session(options: new(IsKospi200: kospi200));
        s.Place("safe", Side.Buy, 9500 + (kospi200 ? 300 : 0), 2);
        s.Place("edge", Side.Buy, kospi200 ? 9700 : 9400, 2);
        CheckSweep("starter", Side.Sell, 9400, 4, 0);
        Assert.Equal(TradingPhase.CallAuction, s.Engine.Phase);
        for (var i = 0; i < input.Length; i++)
        {
            var time = (i + 1L) * 150_000;
            s.Advance(time);
            var (side, price, quantity) = OrderFields(input[i]);
            CheckSweep($"order{i}", side, price, quantity, time);
        }

        void CheckSweep(string id, Side side, long price, long quantity, long time)
        {
            var dynamicRef = s.Engine.DynamicReferencePrice;
            var staticRef = s.Engine.StaticReferencePrice;
            foreach (var trade in s.Place(id, side, price, quantity, time).OfType<Trade>())
            {
                Assert.True(trade.HasAggressor);
                Assert.True(Math.Abs(trade.Price - dynamicRef) * 100 < dynamicRef * (kospi200 ? 3 : 6));
                Assert.True(Math.Abs(trade.Price - staticRef) * 100 < staticRef * 10);
            }
        }
    }

    [Property(MaxTest = 200)]
    [Trait("Rule", "R-AUCT-01")]
    public void I8_every_auction_has_only_one_trade_price(int[] inputs, int seed)
    {
        var s = PopulateAuction(inputs, seed);
        var expected = Assert.IsType<IndicativeAuction>(s.Engine.GetIndicativeAuction());
        var events = s.Advance(150000);
        var tradePrices = events.OfType<Trade>().Select(t => t.Price).Distinct();
        Assert.Equal(expected.Price, Assert.Single(tradePrices));
        Assert.Equal(expected.Quantity, events.OfType<Trade>().Sum(t => t.Quantity));
        Assert.Equal(expected.Price, Assert.Single(events.OfType<AuctionCompleted>()).Price);
    }

    [Property(MaxTest = 200)]
    [Trait("Rule", "R-AUCT-02")]
    [Trait("Rule", "R-AUCT-07")]
    public void I9_book_is_not_crossed_after_auction(int[] inputs, int seed)
    {
        var s = PopulateAuction(inputs, seed);
        s.Advance(150000);
        Assert.Equal(TradingPhase.Continuous, s.Engine.Phase);
        var book = s.Engine.GetBook(1);
        if (book.Bids.Count != 0 && book.Asks.Count != 0)
            Assert.True(book.Bids[0].Price < book.Asks[0].Price);
    }

    private static VolatilityTests.Session PopulateAuction(int[] inputs, int seed)
    {
        var s = new VolatilityTests.Session(options: new(RandomSeed: seed));
        s.Place("trigger-bid", Side.Buy, 9400);
        Assert.Single(s.Place("trigger-ask", Side.Sell, 9400).OfType<ViTriggered>());
        for (var i = 0; i < inputs.Length; i++)
        {
            var (side, price, quantity) = OrderFields(inputs[i]);
            Assert.IsType<OrderAccepted>(Assert.Single(s.Place($"ind{i}", side, price, quantity)));
        }
        return s;
    }

    private static (Side Side, long Price, long Quantity) OrderFields(int input)
    {
        var bits = unchecked((uint)input * 0x9e3779b9U);
        return ((bits & 1) == 0 ? Side.Buy : Side.Sell,
            7500 + ((bits >> 9) % 501) * 10, 1 + ((bits >> 20) % 20));
    }
}
