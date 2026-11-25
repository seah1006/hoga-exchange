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
        Assert.Equal("af5570f5a1810b7af78caf4bc70a660f0df51e42baf91d4de5b2328de0e83dfc", buy.ComputeStateHash());
        buy.Process(new PlaceLimitOrder("same", Side.Buy, 9_990, 6, "a", 1, 0));
        sell.Process(new PlaceLimitOrder("same", Side.Sell, 9_990, 6, "a", 1, 0));
        Assert.NotEqual(buy.ComputeStateHash(), sell.ComputeStateHash());
    }

    [Fact]
    [Trait("Rule", "R-LIMIT-04")]
    [Trait("Rule", "R-TICK-02")]
    public void All_six_reject_reasons_leave_the_book_hash_unchanged()
    {
        var engine = new MatchingEngine(9_940, new TickSizeTable());
        var existing = new PlaceLimitOrder("existing", Side.Buy, 9_990, 6, "a", 1, 0);
        engine.Process(existing);
        var hash = engine.ComputeStateHash();
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
            Assert.Equal(reason, Assert.IsType<OrderRejected>(Assert.Single(engine.Process(command))).Reason);
            Assert.Equal(hash, engine.ComputeStateHash());
        }
    }
}
