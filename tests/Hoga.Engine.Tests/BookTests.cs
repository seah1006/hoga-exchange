namespace Hoga.Engine.Tests;

public class BookTests
{
    [Fact]
    [Trait("Rule", "R-MATCH-01")]
    [Trait("Rule", "R-MATCH-02")]
    [Trait("Rule", "R-MATCH-04")]
    public void Book_and_queue_reflect_partial_fill_and_cancellation()
    {
        var engine = new MatchingEngine(9_940, new TickSizeTable());
        engine.Process(new PlaceLimitOrder("low", Side.Buy, 9_980, 4, "a", 1, 0));
        engine.Process(new PlaceLimitOrder("head", Side.Buy, 9_990, 6, "a", 2, 0));
        engine.Process(new PlaceLimitOrder("tail", Side.Buy, 9_990, 9, "a", 3, 0));
        engine.Process(new PlaceLimitOrder("far", Side.Sell, 10_010, 2, "b", 4, 0));
        engine.Process(new PlaceLimitOrder("near", Side.Sell, 10_000, 7, "b", 5, 0));
        engine.Process(new PlaceLimitOrder("taker", Side.Sell, 9_990, 2, "b", 6, 0));

        var book = engine.GetBook(2);
        Assert.Equal<BookLevel>([new(9_990, 13, 2), new(9_980, 4, 1)], book.Bids);
        Assert.Equal<BookLevel>([new(10_000, 7, 1), new(10_010, 2, 1)], book.Asks);
        Assert.Equal(new BookLevel(9_990, 13, 2), Assert.Single(engine.GetBook(1).Bids));
        Assert.Equal(new BookLevel(10_000, 7, 1), Assert.Single(engine.GetBook(1).Asks));
        Assert.Empty(engine.GetBook(0).Bids);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.GetBook(-1));
        Assert.Equal(new QueuePosition(1, 4), engine.GetQueuePosition("tail"));
        Assert.Null(engine.GetQueuePosition("taker"));

        engine.Process(new CancelOrder("head", 7, 0));
        Assert.Equal(new QueuePosition(0, 0), engine.GetQueuePosition("tail"));
        Assert.Null(engine.GetQueuePosition("head"));
        Assert.Equal(13, book.Bids[0].Quantity);
        Assert.Equal(9, engine.GetBook(1).Bids[0].Quantity);
    }

    [Fact]
    public void Hash_uses_canonical_empty_bytes_and_distinguishes_sides()
    {
        var buy = new MatchingEngine(9_940, new TickSizeTable());
        var sell = new MatchingEngine(9_940, new TickSizeTable());
        Assert.Equal("7387705c8f778ab7c9f0661d985c937cab2d09b7e288397966df05221db44ca4", buy.ComputeStateHash());
        buy.Process(new PlaceLimitOrder("same", Side.Buy, 9_990, 6, "a", 1, 0));
        sell.Process(new PlaceLimitOrder("same", Side.Sell, 9_990, 6, "a", 1, 0));
        Assert.NotEqual(buy.ComputeStateHash(), sell.ComputeStateHash());
    }

    [Fact]
    public void Empty_books_at_the_same_sequence_distinguish_accepted_id_history()
    {
        var used = new MatchingEngine(9_940, new TickSizeTable());
        var fresh = new MatchingEngine(9_940, new TickSizeTable());
        var order = new PlaceLimitOrder("reused", Side.Buy, 9_990, 1, "a", 1, 0);
        used.Process(order);
        used.Process(new CancelOrder(order.OrderId, 2, 0));
        fresh.Process(order with { Quantity = 0 });
        fresh.Process(new CancelOrder(order.OrderId, 2, 0));
        foreach (var engine in new[] { used, fresh })
        {
            Assert.Empty(engine.GetBook(1).Bids);
            Assert.Empty(engine.GetBook(1).Asks);
        }
        Assert.NotEqual(used.ComputeStateHash(), fresh.ComputeStateHash());
        var next = order with { Sequence = 3 };
        Assert.Equal(new OrderRejected(order.OrderId, RejectReason.DuplicateOrderId, null),
            Assert.Single(used.Process(next)));
        Assert.Equal(new OrderAccepted(order.OrderId), Assert.Single(fresh.Process(next)));
    }

    [Fact]
    public void Same_state_after_different_histories_produces_the_same_next_events()
    {
        var left = new MatchingEngine(9_940, new TickSizeTable());
        var right = new MatchingEngine(9_940, new TickSizeTable());
        // The accepted-ID set is equal, but the IDs were inserted in opposite ordinal order.
        var ids = new[] { "a", "Z" };
        for (var i = 0; i < ids.Length; i++)
        {
            var order = new PlaceLimitOrder(ids[i], Side.Buy, 9_990, 1, "a", 2 * i + 1, 0);
            var otherId = ids[^(i + 1)];
            left.Process(order);
            right.Process(order with { OrderId = otherId });
            left.Process(new CancelOrder(order.OrderId, order.Sequence + 1, 0));
            right.Process(new CancelOrder(otherId, order.Sequence + 1, 0));
        }
        var ask = new PlaceLimitOrder("maker", Side.Sell, 9_990, 2, "a", 5, 0);
        left.Process(ask);
        right.Process(ask);
        Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
        var next = ask with { OrderId = "taker", Side = Side.Buy, Sequence = 6 };
        var events = left.Process(next);
        Assert.Single(events.OfType<Trade>());
        Assert.Equal<EngineEvent>(events, right.Process(next));
        Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
    }

    [Fact]
    public void Empty_book_hash_distinguishes_price_settings_and_tick_rule()
    {
        var original = new MatchingEngine(9_940, new TickSizeTable());
        var differentBase = new MatchingEngine(9_950, new TickSizeTable());
        var differentRule = new MatchingEngine(9_940, new AlternativeTickRule());
        Assert.Equal(original.UpperLimit, differentRule.UpperLimit);
        Assert.Equal(original.LowerLimit, differentRule.LowerLimit);
        Assert.NotEqual(original.ComputeStateHash(), differentBase.ComputeStateHash());
        Assert.NotEqual(original.ComputeStateHash(), differentRule.ComputeStateHash());
    }

    private sealed class AlternativeTickRule : ITickSizeRule
    {
        private readonly TickSizeTable _standard = new();
        public string RuleId => "test/alternative-v1";
        public long GetTickSize(long price) => price == 9_991 ? 1 : _standard.GetTickSize(price);
        public bool IsOnTick(long price) => price % GetTickSize(price) == 0;
        public long FloorToTick(long price) => price - price % GetTickSize(price);
    }

    [Fact]
    [Trait("Rule", "R-LIMIT-04")]
    [Trait("Rule", "R-TICK-02")]
    public void All_six_reject_reasons_only_advance_the_sequence()
    {
        var engine = new MatchingEngine(9_940, new TickSizeTable());
        var existing = new PlaceLimitOrder("existing", Side.Buy, 9_990, 6, "a", 1, 0);
        engine.Process(existing);
        var control = new MatchingEngine(9_940, new TickSizeTable());
        control.Process(existing);
        var rejected = new (OrderCommand Command, RejectReason Reason)[]
        {
            (existing with { OrderId = "zero", Sequence = 2, Quantity = 0 }, RejectReason.InvalidQuantity),
            (existing with { OrderId = "off", Sequence = 3, Price = 9_991 }, RejectReason.OffTick),
            (existing with { OrderId = "upper", Sequence = 4, Price = 12_930 }, RejectReason.AboveUpperLimit),
            (existing with { OrderId = "lower", Sequence = 5, Price = 6_950 }, RejectReason.BelowLowerLimit),
            (existing with { Sequence = 6 }, RejectReason.DuplicateOrderId),
            (new CancelOrder("unknown", 7, 0), RejectReason.UnknownOrder),
        };
        foreach (var (command, reason) in rejected)
        {
            var before = engine.ComputeStateHash();
            Assert.Equal(reason, Assert.IsType<OrderRejected>(Assert.Single(engine.Process(command))).Reason);
            Assert.NotEqual(before, engine.ComputeStateHash());
            control.Process(new CancelOrder("never-accepted", command.Sequence, 0));
            Assert.Equal(control.ComputeStateHash(), engine.ComputeStateHash());
        }
    }
}
