namespace Hoga.Engine.Tests;

public sealed class VolatilityTests
{
    internal sealed class Session(long basePrice = 10_000, EngineOptions? options = null)
    {
        private long _sequence;
        public MatchingEngine Engine { get; } = new(basePrice, new TickSizeTable(), options);
        public IReadOnlyList<EngineEvent> Place(string id, Side side, long price, long quantity = 1, long at = 0) =>
            Engine.Process(new PlaceLimitOrder(id, side, price, quantity, "account", ++_sequence, at));
        public IReadOnlyList<EngineEvent> Cancel(string id, long at = 0) =>
            Engine.Process(new CancelOrder(id, ++_sequence, at));
        public IReadOnlyList<EngineEvent> Advance(long at) => Engine.Process(new AdvanceTime(++_sequence, at));
    }

    [Fact]
    [Trait("Rule", "R-VI-01a")]
    public void Sweep_fills_1200_before_9400_and_queues_remaining_300()
    {
        var s = new Session();
        foreach (var (price, qty) in new (long, long)[]
            { (10000, 300), (9800, 300), (9600, 300), (9500, 300), (9400, 200), (9300, 1000) })
            s.Place($"bid{price}", Side.Buy, price, qty);
        var events = s.Place("sell", Side.Sell, 9400, 1500, at: 7);
        Assert.Equal(1200, events.OfType<Trade>().Sum(trade => trade.Quantity));
        Assert.Equal(new ViTriggered(ViKind.Dynamic, 10_000, 9_400, 7, 120_007, "R-VI-01"),
            Assert.Single(events.OfType<ViTriggered>()));
        Assert.Equal(TradingPhase.CallAuction, s.Engine.Phase);
        Assert.Equal(9500, s.Engine.DynamicReferencePrice);
        Assert.Equal(new BookLevel(9400, 300, 1), Assert.Single(s.Engine.GetBook(5).Asks));
        Assert.Equal(new BookLevel(9400, 200, 1), s.Engine.GetBook(5).Bids[0]);
    }

    [Theory]
    [InlineData(false, 9410, false)]
    [InlineData(false, 9400, true)]
    [InlineData(true, 9710, false)]
    [InlineData(true, 9700, true)]
    [InlineData(true, 10290, false)]
    [InlineData(true, 10300, true)]
    [Trait("Rule", "R-VI-01")]
    [Trait("Rule", "R-VI-02")]
    [Trait("Rule", "R-VI-06")]
    public void Dynamic_threshold_is_inclusive(bool kospi200, long price, bool triggers)
    {
        var s = new Session(options: new(IsKospi200: kospi200));
        s.Place("bid", Side.Buy, price);
        var events = s.Place("sell", Side.Sell, price);
        Assert.Equal(triggers, events.OfType<ViTriggered>().Any());
        Assert.Equal(!triggers, events.OfType<Trade>().Any());
    }

    [Fact]
    [Trait("Rule", "R-VI-03")]
    [Trait("Rule", "R-VI-05")]
    [Trait("Rule", "R-VI-07")]
    public void Static_uses_cumulative_move_then_resets_and_can_trigger_again()
    {
        var s = new Session();
        s.Place("b1", Side.Buy, 10500);
        Assert.Single(s.Place("s1", Side.Sell, 10500).OfType<Trade>());
        s.Place("b2", Side.Buy, 10900);
        Assert.Single(s.Place("s2", Side.Sell, 10900).OfType<Trade>());
        s.Place("b3", Side.Buy, 11000);
        var start = Assert.Single(s.Place("s3", Side.Sell, 11000).OfType<ViTriggered>());
        Assert.Equal(new ViTriggered(ViKind.Static, 10000, 11000, 0, 120000, "R-VI-03"), start);
        var close = Assert.Single(s.Advance(150000).OfType<AuctionCompleted>());
        Assert.Equal(11000, close.Price);
        Assert.Equal(11000, s.Engine.DynamicReferencePrice);
        Assert.Equal(11000, s.Engine.StaticReferencePrice);
        s.Place("b4", Side.Buy, 9900, at: 150001);
        var next = Assert.Single(s.Place("s4", Side.Sell, 9900, at: 150001).OfType<ViTriggered>());
        Assert.Equal(ViKind.Static, next.Kind); // Both dynamic and static conditions touch.
        Assert.Equal(11000, next.ReferencePrice);
    }

    [Fact]
    [Trait("Rule", "R-AUCT-07")]
    [Trait("Rule", "R-VI-04")]
    [Trait("Rule", "R-VI-07")]
    public void Cancel_during_auction_can_leave_no_match_and_release_before_next_command()
    {
        var s = new Session();
        s.Place("bid", Side.Buy, 9400);
        s.Place("sell", Side.Sell, 9400, at: 5);
        Assert.Equal(new OrderCancelled("sell", 1), Assert.Single(s.Cancel("sell", 6)));
        var events = s.Place("after", Side.Sell, 10000, at: 150005);
        var close = Assert.IsType<AuctionNoMatch>(events[0]);
        Assert.InRange(close.AtMs, 120005, 150005);
        Assert.Equal(new ViReleased(close.AtMs), events[1]);
        Assert.Equal(new OrderAccepted("after"), events[2]);
        Assert.Equal(TradingPhase.Continuous, s.Engine.Phase);
        Assert.Equal(10000, s.Engine.DynamicReferencePrice);
        Assert.Equal(10000, s.Engine.StaticReferencePrice);
    }

    [Fact]
    [Trait("Rule", "R-VI-01")]
    public void Disabled_vi_uses_continuous_matching()
    {
        var s = new Session(options: new(ViEnabled: false));
        s.Place("bid", Side.Buy, 9400);
        var events = s.Place("sell", Side.Sell, 9400);
        Assert.Empty(events.OfType<ViTriggered>());
        Assert.True(Assert.Single(events.OfType<Trade>()).HasAggressor);
        Assert.Equal(TradingPhase.Continuous, s.Engine.Phase);
    }

    [Fact]
    [Trait("Rule", "R-AUCT-04")]
    public void Auction_ends_at_seeded_time_in_inclusive_window()
    {
        static long End(long seed)
        {
            var s = new Session(options: new(RandomSeed: seed));
            s.Place("bid", Side.Buy, 9400);
            var trigger = Assert.Single(s.Place("sell", Side.Sell, 9400, at: 123).OfType<ViTriggered>());
            Assert.Equal(120123, trigger.ScheduledEndMs);
            Assert.Empty(s.Advance(trigger.ScheduledEndMs - 1));
            return Assert.Single(s.Advance(150123).OfType<AuctionCompleted>()).AtMs;
        }
        var times = Enumerable.Range(0, 100).Select(seed => End(seed)).ToArray();
        Assert.All(times, time => Assert.InRange(time, 120123, 150123));
        Assert.True(times.Distinct().Count() > 1);
        Assert.Equal(times[33], End(33));
        var s = new Session(options: new(RandomSeed: 33));
        s.Place("bid", Side.Buy, 9400);
        s.Place("sell", Side.Sell, 9400, at: 123);
        Assert.Empty(s.Advance(times[33] - 1));
        Assert.Equal(times[33], Assert.Single(s.Advance(times[33]).OfType<AuctionCompleted>()).AtMs);
    }

    [Fact]
    [Trait("Rule", "R-AUCT-03")]
    public void Krx_example_matches_600_and_allocates_at_price_in_fifo_order()
    {
        var s = new Session(7850);
        s.Place("trigger-bid", Side.Buy, 8400);
        Assert.Single(s.Place("trigger-ask", Side.Sell, 8400).OfType<ViTriggered>());
        s.Cancel("trigger-bid");
        s.Cancel("trigger-ask");
        (long Price, long Quantity)[] asks = [(7860, 450), (7850, 750), (7840, 300), (7830, 150),
            (7830, 200), (7830, 50), (7820, 200), (7810, 150)];
        (long Price, long Quantity)[] bids = [(7850, 100), (7840, 150), (7830, 200), (7830, 150),
            (7820, 400), (7810, 500), (7800, 650)];
        for (var i = 0; i < asks.Length; i++)
            Assert.IsType<OrderAccepted>(Assert.Single(s.Place($"a{i}", Side.Sell, asks[i].Price, asks[i].Quantity)));
        for (var i = 0; i < bids.Length; i++)
            Assert.IsType<OrderAccepted>(Assert.Single(s.Place($"b{i}", Side.Buy, bids[i].Price, bids[i].Quantity)));
        var events = s.Advance(150000);
        var completed = Assert.Single(events.OfType<AuctionCompleted>());
        Assert.Equal(7830, completed.Price);
        Assert.Equal(600, completed.Quantity);
        var trades = events.OfType<Trade>().ToArray();
        Assert.All(trades, trade => { Assert.Equal(7830, trade.Price); Assert.False(trade.HasAggressor); });
        Assert.Equal(150, trades.Where(trade => trade.SellOrderId == "a3").Sum(trade => trade.Quantity));
        Assert.Equal(100, trades.Where(trade => trade.SellOrderId == "a4").Sum(trade => trade.Quantity));
        Assert.DoesNotContain(trades, trade => trade.SellOrderId == "a5");
        Assert.Equal(new QueuePosition(1, 100), s.Engine.GetQueuePosition("a5"));
        Assert.Equal(new BookLevel(7830, 150, 2), Assert.Single(s.Engine.GetBook(1).Asks));
    }

    [Fact]
    [Trait("Rule", "R-AUCT-08")]
    [Trait("Rule", "R-AUCT-02a")]
    public void Occupied_price_exception_clears_strictly_better_orders_without_grid_fallback()
    {
        var s = new Session();
        s.Place("low-bid", Side.Buy, 9400);
        s.Place("low-ask", Side.Sell, 9400);
        s.Place("high-bid", Side.Buy, 12380);
        s.Place("high-ask", Side.Sell, 11760, 19);
        Assert.Equal(new IndicativeAuction(11760, 1), s.Engine.GetIndicativeAuction());
        var trade = Assert.Single(s.Advance(150000).OfType<Trade>());
        Assert.Equal("high-bid", trade.BuyOrderId);
        Assert.Equal("low-ask", trade.SellOrderId);
        Assert.Equal(11760, trade.Price);
        Assert.Equal(0, s.Engine.AuctionGridFallbackCount);
        var book = s.Engine.GetBook(1);
        Assert.True(book.Bids[0].Price < book.Asks[0].Price);
    }
    [Fact]
    [Trait("Rule", "R-AUCT-02a")]
    public void Adjacent_tick_exception_closes_without_crossed_book()
    {
        var s = new Session();
        s.Place("low-bid", Side.Buy, 9400);
        s.Place("low-ask", Side.Sell, 9400);
        s.Place("high-bid", Side.Buy, 9420);
        s.Place("high-ask", Side.Sell, 9410, 19);
        Assert.Equal(new IndicativeAuction(9410, 1), s.Engine.GetIndicativeAuction());
        var trade = Assert.Single(s.Advance(150000).OfType<Trade>());
        Assert.Equal("high-bid", trade.BuyOrderId);
        Assert.Equal("low-ask", trade.SellOrderId);
        Assert.Equal(9410, trade.Price);
        var book = s.Engine.GetBook(1);
        Assert.Equal(new BookLevel(9400, 1, 1), Assert.Single(book.Bids));
        Assert.Equal(new BookLevel(9410, 19, 1), Assert.Single(book.Asks));
    }
}
