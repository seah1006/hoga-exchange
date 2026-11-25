namespace Hoga.Engine.Tests;

public sealed class AuctionQueryTests
{
    [Fact]
    [Trait("Rule", "R-AUCT-01")]
    [Trait("Rule", "R-VI-04")]
    public void Indicative_auction_does_not_mutate_and_orders_report_waiting_until_release()
    {
        var s = new VolatilityTests.Session();
        s.Place("bid", Side.Buy, 9400, 2);
        Assert.Equal(OrderStatus.Resting, s.Engine.GetOrderStatus("bid"));
        Assert.Null(s.Engine.GetIndicativeAuction());
        s.Place("ask", Side.Sell, 9400);
        Assert.Equal(OrderStatus.WaitingAuction, s.Engine.GetOrderStatus("bid"));
        Assert.Equal(OrderStatus.WaitingAuction, s.Engine.GetOrderStatus("ask"));
        Assert.Equal(new QueuePosition(0, 0), s.Engine.GetQueuePosition("ask"));
        var before = s.Engine.ComputeStateHash();
        Assert.Equal(new IndicativeAuction(9400, 1), s.Engine.GetIndicativeAuction());
        Assert.Equal(before, s.Engine.ComputeStateHash());
        Assert.IsType<OrderRejected>(Assert.Single(s.Place("bad", Side.Buy, 9401)));
        s.Advance(150000);
        Assert.Null(s.Engine.GetOrderStatus("ask"));
        Assert.Equal(OrderStatus.Resting, s.Engine.GetOrderStatus("bid"));
        Assert.Null(s.Engine.GetIndicativeAuction());
    }

    [Fact]
    public void Hash_captures_settings_and_ongoing_vi_schedule()
    {
        static VolatilityTests.Session Start(long at, EngineOptions? options = null)
        {
            var s = new VolatilityTests.Session(options: options);
            s.Place("bid", Side.Buy, 9400);
            s.Place("ask", Side.Sell, 9400, at: at);
            return s;
        }
        var plain = new VolatilityTests.Session().Engine.ComputeStateHash();
        Assert.NotEqual(plain, new VolatilityTests.Session(options: new(ViEnabled: false)).Engine.ComputeStateHash());
        Assert.NotEqual(plain, new VolatilityTests.Session(options: new(IsKospi200: true)).Engine.ComputeStateHash());
        Assert.NotEqual(plain, new VolatilityTests.Session(options: new(RandomSeed: -1)).Engine.ComputeStateHash());
        var first = Start(4);
        var replay = Start(4);
        var later = Start(5);
        Assert.Equal(first.Engine.ComputeStateHash(), replay.Engine.ComputeStateHash());
        Assert.NotEqual(first.Engine.ComputeStateHash(), later.Engine.ComputeStateHash());
        Assert.Equal<EngineEvent>(first.Advance(150004), replay.Advance(150004));
        Assert.Equal(first.Engine.ComputeStateHash(), replay.Engine.ComputeStateHash());
    }
}
