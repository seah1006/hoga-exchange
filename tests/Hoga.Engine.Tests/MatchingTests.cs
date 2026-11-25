namespace Hoga.Engine.Tests;

public class MatchingTests
{
    private static MatchingEngine NewEngine() => new(9_940, new TickSizeTable());

    private static PlaceLimitOrder Order(long sequence, string id, Side side, long price, long quantity) =>
        new(id, side, price, quantity, "a-1", sequence, sequence * 10);

    [Theory]
    [Trait("Rule", "R-MATCH-01")]
    [Trait("Rule", "R-MATCH-03")]
    [InlineData(Side.Buy, 10_020, 10_010, 10_030)]
    [InlineData(Side.Sell, 9_970, 9_980, 9_960)]
    public void Better_price_trades_first_at_resting_price(Side taker, long worse, long better, long limit)
    {
        var engine = NewEngine();
        var maker = taker == Side.Buy ? Side.Sell : Side.Buy;
        engine.Process(Order(1, "old", maker, worse, 1));
        engine.Process(Order(2, "best", maker, better, 1));
        var events = engine.Process(Order(3, "incoming", taker, limit, 1));
        var trade = taker == Side.Buy
            ? new Trade("incoming", "best", better, 1, 3)
            : new Trade("best", "incoming", better, 1, 3);
        Assert.Equal<EngineEvent>([new OrderAccepted("incoming"), trade, new OrderFilled("best"), new OrderFilled("incoming")], events);
    }

    [Fact]
    [Trait("Rule", "R-MATCH-02")]
    [Trait("Rule", "R-MATCH-04")]
    public void Partial_fill_keeps_priority_and_large_order_sweeps_multiple_levels()
    {
        var engine = NewEngine();
        engine.Process(Order(1, "first", Side.Sell, 10_000, 3));
        engine.Process(Order(2, "second", Side.Sell, 10_000, 4) with { LogicalTimeMs = 0 });
        engine.Process(Order(3, "third", Side.Sell, 10_010, 5));
        Assert.Equal<EngineEvent>([new OrderAccepted("small"), new Trade("small", "first", 10_000, 2, 4), new OrderFilled("small")],
            engine.Process(Order(4, "small", Side.Buy, 10_020, 2)));
        Assert.Equal<EngineEvent>([
            new OrderAccepted("large"), new Trade("large", "first", 10_000, 1, 5), new OrderFilled("first"),
            new Trade("large", "second", 10_000, 4, 5), new OrderFilled("second"),
            new Trade("large", "third", 10_010, 5, 5), new OrderFilled("third")],
            engine.Process(Order(5, "large", Side.Buy, 10_020, 11)));
        Assert.Equal(new OrderCancelled("large", 1), Assert.Single(engine.Process(new CancelOrder("large", 6, 60))));
    }

    [Fact]
    [Trait("Rule", "R-MATCH-02")]
    public void Cancel_removes_the_target_from_the_queue()
    {
        var engine = NewEngine();
        engine.Process(Order(1, "first", Side.Buy, 10_000, 3));
        engine.Process(Order(2, "next", Side.Buy, 10_000, 2));
        Assert.Equal(new OrderCancelled("first", 3), Assert.Single(engine.Process(new CancelOrder("first", 3, 30))));
        Assert.Equal(new Trade("next", "sell", 10_000, 2, 4), Assert.Single(
            engine.Process(Order(4, "sell", Side.Sell, 9_990, 2)).OfType<Trade>()));
    }

    [Theory]
    [Trait("Rule", "R-TICK-02")]
    [Trait("Rule", "R-LIMIT-04")]
    [InlineData(10_000, 0, RejectReason.InvalidQuantity, null)]
    [InlineData(10_000, -1, RejectReason.InvalidQuantity, null)]
    [InlineData(10_001, 1, RejectReason.OffTick, "R-TICK-02")]
    [InlineData(12_921, 1, RejectReason.AboveUpperLimit, "R-LIMIT-04")]
    [InlineData(6_959, 1, RejectReason.BelowLowerLimit, "R-LIMIT-04")]
    public void Rejects_invalid_placement(long price, long quantity, RejectReason reason, string? ruleId)
    {
        var engine = NewEngine();
        Assert.Equal(new OrderRejected("bad", reason, ruleId), Assert.Single(engine.Process(Order(1, "bad", Side.Buy, price, quantity))));
        Assert.Equal(new OrderAccepted("bad"), Assert.Single(engine.Process(Order(2, "bad", Side.Buy, 10_000, 1))));
    }

    [Theory]
    [Trait("Rule", "R-LIMIT-05")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_price_below_one_even_at_zero_lower_limit(long price)
    {
        var engine = new MatchingEngine(1, new TickSizeTable());
        Assert.Equal(0, engine.LowerLimit);
        var order = Order(1, "minimum", Side.Buy, price, 1);
        Assert.Equal(new OrderRejected(order.OrderId, RejectReason.InvalidPrice, "R-LIMIT-05"),
            Assert.Single(engine.Process(order)));
        Assert.Empty(engine.GetBook(1).Bids);
        Assert.Equal(new OrderAccepted(order.OrderId),
            Assert.Single(engine.Process(order with { Sequence = 2, Price = 1 })));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Rejects_blank_ids_for_placement_and_cancellation(string id)
    {
        var engine = NewEngine();
        var rejected = new OrderRejected(id, RejectReason.InvalidOrderId, null);
        Assert.Equal(rejected, Assert.Single(engine.Process(Order(1, id, Side.Sell, 10_000, 1))));
        Assert.Equal(rejected, Assert.Single(engine.Process(Order(2, id, Side.Sell, 0, 0))));
        Assert.Equal(rejected, Assert.Single(engine.Process(new CancelOrder(id, 3, 0))));
        Assert.Empty(engine.GetBook(1).Asks);
    }

    [Fact]
    [Trait("Rule", "R-LIMIT-04")]
    public void Accepts_orders_at_both_limits()
    {
        var engine = NewEngine();
        Assert.IsType<OrderAccepted>(Assert.Single(engine.Process(Order(1, "bid", Side.Buy, engine.LowerLimit, 1))));
        Assert.IsType<OrderAccepted>(Assert.Single(engine.Process(Order(2, "ask", Side.Sell, engine.UpperLimit, 1))));
    }

    [Fact]
    public void Rejects_duplicate_ids_and_cancelling_unknown_or_filled_orders()
    {
        var engine = NewEngine();
        var original = Order(1, "original", Side.Buy, 10_000, 1);
        engine.Process(original);
        var duplicate = new OrderRejected("original", RejectReason.DuplicateOrderId, null);
        Assert.Equal(duplicate, Assert.Single(engine.Process(original with { Sequence = 2 })));
        Assert.Equal(new OrderRejected("unknown", RejectReason.UnknownOrder, null),
            Assert.Single(engine.Process(new CancelOrder("unknown", 3, 30))));
        engine.Process(Order(4, "fill", Side.Sell, 10_000, 1));
        Assert.Equal(new OrderRejected("original", RejectReason.UnknownOrder, null),
            Assert.Single(engine.Process(new CancelOrder("original", 5, 50))));
        Assert.Equal(duplicate, Assert.Single(engine.Process(original with { Sequence = 6 })));
    }

    [Fact]
    public void Sequence_starts_at_one_and_rejects_gaps_and_repetitions_without_advancing()
    {
        var engine = NewEngine();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Process(new CancelOrder("unknown", 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Process(new CancelOrder("unknown", 2, 0)));
        Assert.IsType<OrderRejected>(Assert.Single(engine.Process(new CancelOrder("unknown", 1, 0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Process(new CancelOrder("unknown", 1, 0)));
        Assert.IsType<OrderRejected>(Assert.Single(engine.Process(new CancelOrder("unknown", 2, 0))));
    }
}
