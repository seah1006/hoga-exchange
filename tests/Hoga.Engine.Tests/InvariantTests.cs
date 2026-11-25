using FsCheck.Xunit;
using Xunit.Abstractions;

namespace Hoga.Engine.Tests;

public class InvariantTests(ITestOutputHelper output)
{
    private const int Runs = 200;
    private static readonly TickSizeTable Rule = new();
    private static readonly long[] BasePrices = [9_940, 1_990, 4_990, 19_990, 49_950, 199_900, 499_500];

    [Property(MaxTest = Runs)]
    public void I1_continuous_book_is_never_crossed(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var engine = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            foreach (var command in Commands(inputs, basePrice, viEnabled))
            {
                engine.Process(command);
                var book = engine.GetBook(1);
                if (engine.Phase == TradingPhase.Continuous && book.Bids.Count != 0 && book.Asks.Count != 0)
                    Assert.True(book.Bids[0].Price < book.Asks[0].Price);
            }
        }
    }

    [Property(MaxTest = Runs)]
    [Trait("Rule", "R-TICK-02")]
    [Trait("Rule", "R-LIMIT-04")]
    public void I2_trades_use_valid_prices(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var engine = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            foreach (var command in Commands(inputs, basePrice, viEnabled))
            foreach (var trade in engine.Process(command).OfType<Trade>())
            {
                Assert.InRange(trade.Price, engine.LowerLimit, engine.UpperLimit);
                Assert.True(Rule.IsOnTick(trade.Price));
            }
        }
    }

    [Property(MaxTest = Runs)]
    public void I3_each_accepted_order_conserves_quantity(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var engine = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            var ledger = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
            foreach (var command in Commands(inputs, basePrice, viEnabled))
            {
                ApplyEvents(command, engine.Process(command), ledger, checkPriority: false);
                var book = engine.GetBook(int.MaxValue);
                foreach (var side in new[] { Side.Buy, Side.Sell })
                {
                    var levels = (side == Side.Buy ? book.Bids : book.Asks).ToDictionary(x => x.Price);
                    foreach (var group in ledger.Values.Where(x => x.Order.Side == side).GroupBy(x => x.Order.Price))
                    {
                        levels.Remove(group.Key, out var level);
                        var positions = group.Select(x => (Entry: x, Position: engine.GetQueuePosition(x.Order.OrderId))).ToArray();
                        var resting = positions.Where(x => x.Position is not null).OrderBy(x => x.Position!.OrdersAhead).ToArray();
                        Assert.Equal(resting.Length, level?.OrderCount ?? 0);
                        Assert.Equal(resting.Length == 0, level is null);
                        foreach (var (entry, _) in positions.Where(x => x.Position is null))
                            Assert.Equal(entry.Order.Quantity, entry.Traded + entry.Cancelled);
                        var ahead = 0L;
                        for (var i = 0; i < resting.Length; i++)
                        {
                            var (entry, position) = resting[i];
                            Assert.Equal(new QueuePosition(i, ahead), position);
                            var next = i + 1 < resting.Length ? resting[i + 1].Position!.QuantityAhead : level!.Quantity;
                            var remaining = next - ahead;
                            Assert.True(remaining > 0);
                            Assert.Equal(entry.Order.Quantity, entry.Traded + entry.Cancelled + remaining);
                            ahead = next;
                        }
                    }
                    Assert.Empty(levels);
                }
            }
        }
    }

    [Property(MaxTest = Runs)]
    [Trait("Rule", "R-MATCH-01")]
    [Trait("Rule", "R-MATCH-02")]
    [Trait("Rule", "R-MATCH-04")]
    public void I4_trades_take_best_price_then_earliest_resting_order(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var engine = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            var ledger = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
            foreach (var command in Commands(inputs, basePrice, viEnabled))
                ApplyEvents(command, engine.Process(command), ledger, checkPriority: true);
        }
    }

    [Property(MaxTest = Runs)]
    public void I5_replay_has_identical_events_and_hashes(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var left = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            var right = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            foreach (var command in Commands(inputs, basePrice, viEnabled))
            {
                Assert.Equal<EngineEvent>(left.Process(command), right.Process(command));
                Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
            }
        }
    }

    [Property(MaxTest = Runs)]
    public void I6_rejected_commands_only_advance_sequence(int[] inputs)
    {
        foreach (var viEnabled in new[] { false, true })
        foreach (var basePrice in BasePrices)
        {
            var engine = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            var control = new MatchingEngine(basePrice, Rule, viEnabled ? null : new(ViEnabled: false));
            foreach (var command in Commands(inputs, basePrice, viEnabled))
            {
                var before = engine.ComputeStateHash();
                var actual = engine.Process(command);
                if (actual.LastOrDefault() is OrderRejected)
                {
                    // Both commands may close a due auction first; only the rejection itself is inert.
                    var noOp = new CancelOrder("never-accepted", command.Sequence, command.LogicalTimeMs);
                    var expected = control.Process(noOp);
                    Assert.Equal(new OrderRejected(noOp.OrderId, RejectReason.UnknownOrder, null), expected[^1]);
                    Assert.Equal(actual.SkipLast(1), expected.SkipLast(1));
                    Assert.NotEqual(before, engine.ComputeStateHash());
                }
                else
                {
                    Assert.Equal<EngineEvent>(actual, control.Process(command));
                }
                Assert.Equal(control.ComputeStateHash(), engine.ComputeStateHash());
            }
        }
    }

    [Fact]
    [Trait("Rule", "R-VI-04")]
    public void Vi_enabled_generator_closes_multiple_auctions_and_reports_coverage()
    {
        var random = new Random(20261124);
        var closes = 0;
        var noMatches = 0;
        var fallback = 0L;
        var closesAfterPrefix = 0;
        for (var trial = 0; trial < Runs; trial++)
        {
            var inputs = Enumerable.Range(0, random.Next(0, 81)).Select(_ => random.Next()).ToArray();
            foreach (var basePrice in BasePrices)
            {
                var engine = new MatchingEngine(basePrice, Rule);
                foreach (var command in Commands(inputs, basePrice, viEnabled: true))
                {
                    var events = engine.Process(command);
                    var closed = events.Count(e => e is ViReleased);
                    closes += closed;
                    noMatches += events.Count(e => e is AuctionNoMatch);
                    if (command.Sequence > 14)
                        closesAfterPrefix += closed;
                }
                fallback += engine.AuctionGridFallbackCount;
            }
        }
        Assert.True(closes >= Runs * BasePrices.Length);
        Assert.True(closesAfterPrefix > 0);
        output.WriteLine($"VI generator: books={Runs * BasePrices.Length}; closes={closes}; " +
            $"after-prefix={closesAfterPrefix}; no-match={noMatches}; grid-fallback={fallback}");
    }

    private sealed class LedgerEntry(PlaceLimitOrder order)
    {
        public PlaceLimitOrder Order { get; } = order;
        public long Traded { get; set; }
        public long Cancelled { get; set; }
        public long ExpectedRemaining => Order.Quantity - Traded - Cancelled;
    }

    private static void ApplyEvents(OrderCommand command, IReadOnlyList<EngineEvent> events,
        Dictionary<string, LedgerEntry> ledger, bool checkPriority)
    {
        foreach (var @event in events)
        {
            switch (@event)
            {
                case OrderAccepted accepted:
                    ledger.Add(accepted.OrderId, new(Assert.IsType<PlaceLimitOrder>(command)));
                    break;
                case Trade trade:
                    if (checkPriority && !trade.HasAggressor)
                    {
                        // Auction trades have two resting sides: independently check the head of each.
                        var live = ledger.Values.Where(x => x.ExpectedRemaining > 0);
                        var buy = live.Where(x => x.Order.Side == Side.Buy)
                            .OrderByDescending(x => x.Order.Price).ThenBy(x => x.Order.Sequence).First().Order;
                        var sell = live.Where(x => x.Order.Side == Side.Sell)
                            .OrderBy(x => x.Order.Price).ThenBy(x => x.Order.Sequence).First().Order;
                        Assert.Equal(buy.OrderId, trade.BuyOrderId);
                        Assert.Equal(sell.OrderId, trade.SellOrderId);
                        Assert.InRange(trade.Price, sell.Price, buy.Price);
                    }
                    else if (checkPriority)
                    {
                        var taker = Assert.IsType<PlaceLimitOrder>(command);
                        var makerId = taker.Side == Side.Buy ? trade.SellOrderId : trade.BuyOrderId;
                        var maker = ledger[makerId].Order;
                        // Check against all still-live opposite orders before applying this fill.
                        var opposing = ledger.Values.Where(x => x.Order.Sequence < command.Sequence &&
                            x.Order.Side != taker.Side && x.ExpectedRemaining > 0).ToArray();
                        Assert.NotEmpty(opposing);
                        var bestPrice = taker.Side == Side.Buy
                            ? opposing.Min(x => x.Order.Price)
                            : opposing.Max(x => x.Order.Price);
                        Assert.Equal(bestPrice, trade.Price);
                        var earliest = opposing.Where(x => x.Order.Price == bestPrice).MinBy(x => x.Order.Sequence);
                        Assert.Equal(earliest?.Order.OrderId, makerId);
                        Assert.Equal(trade.Price, maker.Price);
                    }
                    ledger[trade.BuyOrderId].Traded += trade.Quantity;
                    ledger[trade.SellOrderId].Traded += trade.Quantity;
                    break;
                case OrderCancelled cancelled:
                    ledger[cancelled.OrderId].Cancelled += cancelled.RemainingQuantity;
                    break;
            }
        }
    }

    private static IEnumerable<OrderCommand> Commands(int[] inputs, long basePrice, bool viEnabled)
    {
        var (upper, lower, _) = PriceLimits.Compute(basePrice, Rule);
        var center = Rule.FloorToTick(basePrice + 6 * Rule.GetTickSize(basePrice));
        var below = Rule.FloorToTick(center - 1);
        var above = center + Rule.GetTickSize(center);
        // Even the shrunk empty sequence contests price priority in both directions and FIFO at center.
        yield return new PlaceLimitOrder("worse-ask", Side.Sell, above, 3, "a", 1, 0);
        yield return new PlaceLimitOrder("seed-1", Side.Sell, center, 3, "a", 2, 0);
        yield return new PlaceLimitOrder("seed-2", Side.Sell, center, 5, "b", 3, 0);
        yield return new PlaceLimitOrder("seed-3", Side.Buy, center, 4, "a", 4, 0);
        yield return new PlaceLimitOrder("buy-sweep", Side.Buy, above, 5, "a", 5, 0);
        yield return new PlaceLimitOrder("off-tick", Side.Buy, upper - 1, 1, "b", 6, 0);
        yield return new CancelOrder("worse-ask", 7, 0);
        yield return new PlaceLimitOrder("worse-bid", Side.Buy, below, 4, "a", 8, 0);
        yield return new PlaceLimitOrder("best-bid", Side.Buy, center, 3, "a", 9, 0);
        yield return new PlaceLimitOrder("sell-sweep", Side.Sell, below, 5, "b", 10, 0);
        yield return new CancelOrder("worse-bid", 11, 0);

        var sequence = 11L;
        var time = 0L;
        if (viEnabled)
        {
            // Keep at least one auction and one closure in even the smallest shrunk input.
            yield return new PlaceLimitOrder("vi-bid", Side.Buy, lower, 2, "a", ++sequence, time);
            yield return new PlaceLimitOrder("vi-ask", Side.Sell, lower, 2, "b", ++sequence, time);
            time += 150_001;
            yield return new AdvanceTime(++sequence, time);
        }

        for (var i = 0; i < inputs.Length; i++)
        {
            // Mix small FsCheck integers so all decision fields vary independently of test size.
            var bits = Mix(inputs[i]);
            sequence++;
            var id = Id(i);
            var priorId = i == 0 ? "seed-2" : Id((int)((bits >> 9) % (uint)i));
            if (viEnabled)
            {
                time += (bits & 32) == 0 ? bits >> 19 : 150_001 + (bits >> 19);
                if ((bits & 7) == 2)
                {
                    yield return new AdvanceTime(sequence, time);
                    continue;
                }
            }
            else
                time = bits >> 19;
            if ((bits & 7) == 0)
            {
                yield return new CancelOrder(priorId, sequence, time);
                continue;
            }
            if ((bits & 7) == 1)
                id = priorId;
            var price = ((bits >> 3) & 15) switch
            {
                0 => Rule.FloorToTick(lower - 1), 1 => lower, 2 => lower + 1,
                3 => upper - 1, 4 => upper, 5 => upper + Rule.GetTickSize(upper),
                6 => below + 1, 7 => center + 1,
                8 or 9 => below, 10 or 11 => above,
                _ => center,
            };
            var side = (bits & 128) == 0 ? Side.Buy : Side.Sell;
            var quantity = (long)((bits >> 23) & 15) - 1;
            yield return new PlaceLimitOrder(id, side, price, quantity, "account", sequence, time);
        }
    }

    private static string Id(int i) => FormattableString.Invariant($"random-{i}");

    private static uint Mix(int value)
    {
        unchecked
        {
            var bits = (uint)value + 0x9e3779b9;
            bits = (bits ^ (bits >> 16)) * 0x85ebca6b;
            bits = (bits ^ (bits >> 13)) * 0xc2b2ae35;
            return bits ^ (bits >> 16);
        }
    }
}
