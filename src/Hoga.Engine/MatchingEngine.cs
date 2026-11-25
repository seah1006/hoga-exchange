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
    private readonly EngineOptions _options;

    public MatchingEngine(long basePrice, ITickSizeRule tickSizeRule, EngineOptions? options = null)
    {
        (UpperLimit, LowerLimit, _) = PriceLimits.Compute(basePrice, tickSizeRule);
        BasePrice = basePrice;
        DynamicReferencePrice = basePrice;
        _tickSizeRule = tickSizeRule;
        StaticReferencePrice = basePrice;
        _options = options ?? new();
        _rng = new(_options.RandomSeed);
    }

    public long DynamicReferencePrice { get; private set; }
    public TradingPhase Phase { get; private set; }
    public long BasePrice { get; }
    public long UpperLimit { get; }
    public long LowerLimit { get; }

    public IReadOnlyList<EngineEvent> Process(OrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_lastSequence == long.MaxValue || command.Sequence != _lastSequence + 1)
            throw new ArgumentOutOfRangeException(nameof(command), "Sequence must start at 1 and increase by 1.");
        ArgumentNullException.ThrowIfNull(command.OrderId);

        if (command is not (PlaceLimitOrder or CancelOrder or AdvanceTime))
            throw new ArgumentException("Unsupported command type.", nameof(command));
        if (command is PlaceLimitOrder { Side: not (Side.Buy or Side.Sell) })
            throw new ArgumentOutOfRangeException(nameof(command), "Unknown side.");

        List<EngineEvent>? due = null;
        if (Phase == TradingPhase.CallAuction && command.LogicalTimeMs >= _actualEndMs)
        {
            due = [];
            CompleteAuction(command.Sequence, due);
        }
        var events = command switch
        {
            PlaceLimitOrder order => Place(order),
            CancelOrder cancel => Cancel(cancel),
            AdvanceTime => Array.Empty<EngineEvent>(),
            _ => throw new ArgumentException("Unsupported command type.", nameof(command)),
        };
        _lastSequence = command.Sequence;
        if (due is null)
            return events;
        due.AddRange(events);
        return due.AsReadOnly();
    }

    private IReadOnlyList<EngineEvent> Place(PlaceLimitOrder order)
    {
        if (order.Side is not (Side.Buy or Side.Sell))
            throw new ArgumentOutOfRangeException(nameof(order), "Unknown side.");
        // After command/sequence and side checks: ID, quantity, duplicate ID, minimum price,
        // upper/lower limits, then tick alignment.
        if (string.IsNullOrWhiteSpace(order.OrderId))
            return [new OrderRejected(order.OrderId, RejectReason.InvalidOrderId, null)];
        if (order.Quantity <= 0)
            return [new OrderRejected(order.OrderId, RejectReason.InvalidQuantity, null)];
        if (_acceptedIds.Contains(order.OrderId))
            return [new OrderRejected(order.OrderId, RejectReason.DuplicateOrderId, null)];
        // R-LIMIT-05: the calculated lower limit can be 0 when the base price is 1.
        if (order.Price < 1)
            return [new OrderRejected(order.OrderId, RejectReason.InvalidPrice, "R-LIMIT-05")];
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
        var submittedReference = DynamicReferencePrice; // R-VI-01: fixed for the entire sweep.
        while (Phase == TradingPhase.Continuous && remaining > 0 && opposite.Count != 0)
        {
            var (bestPrice, queue) = opposite.First();
            if (order.Side == Side.Buy ? order.Price < bestPrice : order.Price > bestPrice)
                break;

            // R-VI-01a: test before either quantity is changed; only the unfilled tail rests.
            if (TryTriggerVi(bestPrice, submittedReference, order.LogicalTimeMs, events))
                break;

            var head = queue.First!; // R-MATCH-02: FIFO at this price.
            var resting = head.Value;
            var quantity = Math.Min(remaining, resting.Remaining);
            // R-MATCH-03: use the resting order's price.
            events.Add(order.Side == Side.Buy
                ? new Trade(order.OrderId, resting.Id, bestPrice, quantity, order.Sequence)
                : new Trade(resting.Id, order.OrderId, bestPrice, quantity, order.Sequence));
            DynamicReferencePrice = bestPrice;
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
        // Check ID before looking up the cancellation target.
        if (string.IsNullOrWhiteSpace(command.OrderId))
            return [new OrderRejected(command.OrderId, RejectReason.InvalidOrderId, null)];
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
