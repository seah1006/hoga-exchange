namespace Hoga.Engine;

public sealed partial class MatchingEngine
{
    private readonly ITickSizeRule _tickSizeRule;
    // R-MATCH-01: First() always gives the best price on each side.
    private readonly SortedDictionary<long, LinkedList<RestingOrder>> _bids =
        new(Comparer<long>.Create((left, right) => right.CompareTo(left)));
    private readonly SortedDictionary<long, LinkedList<RestingOrder>> _asks = new();
    // Lookup only; neither collection determines execution order.
    private readonly Dictionary<string, LinkedListNode<RestingOrder>> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptedIds = new(StringComparer.Ordinal);
    private long _lastSequence;

    public MatchingEngine(long basePrice, ITickSizeRule tickSizeRule)
    {
        (UpperLimit, LowerLimit, _) = PriceLimits.Compute(basePrice, tickSizeRule);
        BasePrice = basePrice;
        _tickSizeRule = tickSizeRule;
    }

    public long BasePrice { get; }
    public long UpperLimit { get; }
    public long LowerLimit { get; }

    public IReadOnlyList<EngineEvent> Process(OrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_lastSequence == long.MaxValue || command.Sequence != _lastSequence + 1)
            throw new ArgumentOutOfRangeException(nameof(command), "Sequence must start at 1 and increase by 1.");
        ArgumentNullException.ThrowIfNull(command.OrderId);

        var events = command switch
        {
            PlaceLimitOrder order => Place(order),
            CancelOrder cancel => Cancel(cancel),
            _ => throw new ArgumentException("Unsupported command type.", nameof(command)),
        };
        _lastSequence = command.Sequence;
        return events;
    }

    private IReadOnlyList<EngineEvent> Place(PlaceLimitOrder order)
    {
        if (order.Side is not (Side.Buy or Side.Sell))
            throw new ArgumentOutOfRangeException(nameof(order), "Unknown side.");
        if (order.Quantity <= 0)
            return [new OrderRejected(order.OrderId, RejectReason.InvalidQuantity, null)];
        if (_acceptedIds.Contains(order.OrderId))
            return [new OrderRejected(order.OrderId, RejectReason.DuplicateOrderId, null)];
        // R-LIMIT-04: the limits themselves are inclusive.
        if (order.Price > UpperLimit)
            return [new OrderRejected(order.OrderId, RejectReason.AboveUpperLimit, "R-LIMIT-04")];
        if (order.Price < LowerLimit)
            return [new OrderRejected(order.OrderId, RejectReason.BelowLowerLimit, "R-LIMIT-04")];
        if (!_tickSizeRule.IsOnTick(order.Price))
            return [new OrderRejected(order.OrderId, RejectReason.OffTick, "R-TICK-02")];

        _acceptedIds.Add(order.OrderId);
        var events = new List<EngineEvent> { new OrderAccepted(order.OrderId) };
        var remaining = order.Quantity;
        var opposite = order.Side == Side.Buy ? _asks : _bids;
        while (remaining > 0 && opposite.Count != 0)
        {
            var (bestPrice, queue) = opposite.First();
            if (order.Side == Side.Buy ? order.Price < bestPrice : order.Price > bestPrice)
                break;

            var head = queue.First!; // R-MATCH-02: FIFO at this price.
            var resting = head.Value;
            var quantity = Math.Min(remaining, resting.Remaining);
            // R-MATCH-03: use the resting order's price.
            events.Add(order.Side == Side.Buy
                ? new Trade(order.OrderId, resting.Id, bestPrice, quantity, order.Sequence)
                : new Trade(resting.Id, order.OrderId, bestPrice, quantity, order.Sequence));
            remaining -= quantity;
            resting.Remaining -= quantity; // R-MATCH-04: update in place without changing priority.
            if (resting.Remaining == 0)
            {
                Remove(head);
                events.Add(new OrderFilled(resting.Id));
            }
        }

        if (remaining == 0)
            events.Add(new OrderFilled(order.OrderId));
        else
        {
            var own = Levels(order.Side);
            if (!own.TryGetValue(order.Price, out var queue))
            {
                queue = new();
                own.Add(order.Price, queue);
            }
            _active.Add(order.OrderId, queue.AddLast(new RestingOrder(order, remaining)));
        }
        return events.AsReadOnly();
    }

    private IReadOnlyList<EngineEvent> Cancel(CancelOrder command)
    {
        if (!_active.TryGetValue(command.OrderId, out var node))
            return [new OrderRejected(command.OrderId, RejectReason.UnknownOrder, null)];
        Remove(node);
        return [new OrderCancelled(command.OrderId, node.Value.Remaining)];
    }

    private void Remove(LinkedListNode<RestingOrder> node)
    {
        var order = node.Value;
        var queue = node.List!;
        queue.Remove(node);
        if (queue.Count == 0)
            Levels(order.Side).Remove(order.Price);
        _active.Remove(order.Id);
    }

    private SortedDictionary<long, LinkedList<RestingOrder>> Levels(Side side) => side == Side.Buy ? _bids : _asks;

    private sealed class RestingOrder(PlaceLimitOrder order, long remaining)
    {
        public string Id { get; } = order.OrderId;
        public Side Side { get; } = order.Side;
        public long Price { get; } = order.Price;
        public long Sequence { get; } = order.Sequence;
        public long Remaining { get; set; } = remaining;
    }
}
