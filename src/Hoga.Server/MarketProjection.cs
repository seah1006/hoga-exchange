using System.Globalization;
using Hoga.Engine;

namespace Hoga.Server;

internal sealed class MarketProjection
{
    private readonly Dictionary<string, OrderView> _orders = new(StringComparer.Ordinal);
    internal const int TerminalCapacity = 4096; // 임의 지정
    private const int TradeCapacity = 50; // 계약 v0.3
    private const long DayMs = 86_400_000; // 임의 지정: logical day until sessions are introduced.
    private readonly Queue<string> _terminal = new();
    private readonly SortedDictionary<long, Candle> _candles = new();
    private readonly Queue<object> _trades = new();
    private long _day;
    private long _lastEvictedSequence;
    internal IEnumerable<object> Candles => _candles.Values;
    internal IEnumerable<object> Trades => _trades;
    internal bool MayHaveExpired(string id) => id.StartsWith("o-", StringComparison.Ordinal) &&
        long.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var seq) &&
        seq > 0 && seq <= _lastEvictedSequence && id == "o-" + seq.ToString(CultureInfo.InvariantCulture);

    private void Complete(string id)
    {
        _terminal.Enqueue(id);
        if (_terminal.Count <= TerminalCapacity) return;
        var expired = _terminal.Dequeue();
        var sequence = _orders[expired].Source.Sequence;
        _lastEvictedSequence = Math.Max(_lastEvictedSequence, sequence);
        _orders.Remove(expired);
    }
    public const string Symbol = "HOGA";
    public ViTriggered? Vi { get; private set; }
    public long LogicalTimeMs { get; private set; }
    public bool KnowsOrder(string id) => _orders.ContainsKey(id);

    public IReadOnlyList<(string Name, object Payload)> Apply(OrderCommand command, IReadOnlyList<EngineEvent> events,
        bool publish)
    {
        LogicalTimeMs = command.LogicalTimeMs;
        var day = LogicalTimeMs / DayMs;
        if (day != _day) { _candles.Clear(); _day = day; }
        if (command is PlaceLimitOrder place) _orders.Add(place.OrderId, new(place));
        var messages = new List<(string, object)>();
        var end = events.OfType<AuctionCompleted>().SingleOrDefault();
        foreach (var e in events)
        {
            switch (e)
            {
                case OrderRejected failure when command is PlaceLimitOrder:
                    _orders[failure.OrderId].Reject(failure);
                    Complete(failure.OrderId);
                    break;
                case OrderCancelled cancel:
                    _orders[cancel.OrderId].Cancelled = true;
                    Complete(cancel.OrderId);
                    break;
                case OrderFilled filled:
                    Complete(filled.OrderId);
                    break;
                case Trade trade:
                    _orders[trade.BuyOrderId].Remaining -= trade.Quantity;
                    _orders[trade.SellOrderId].Remaining -= trade.Quantity;
                    var at = trade.HasAggressor ? command.LogicalTimeMs : end!.AtMs;
                    var side = trade.HasAggressor ? ((PlaceLimitOrder)command).Side.ToString() : null;
                    var payload = new { symbol = Symbol, seq = trade.Sequence, logicalTimeMs = at,
                        trade.Price, trade.Quantity, aggressorSide = side };
                    if (_trades.Count == TradeCapacity) _trades.Dequeue();
                    _trades.Enqueue(payload);
                    Emit("trade", payload);
                    if (at / DayMs == _day)
                    {
                        var start = at / 60_000 * 60_000;
                        var candle = _candles.TryGetValue(start, out var prior)
                            ? prior with { High = Math.Max(prior.High, trade.Price), Low = Math.Min(prior.Low, trade.Price),
                                Close = trade.Price, Volume = prior.Volume + trade.Quantity }
                            : new(Symbol, start, trade.Price, trade.Price, trade.Price, trade.Price, trade.Quantity);
                        _candles[start] = candle;
                        Emit("candle", candle);
                    }
                    break;
                case ViTriggered active:
                    Vi = active;
                    Emit("viEvent", new { type = "Triggered", kind = active.Kind.ToString(), active.ReferencePrice,
                        triggerPrice = active.TriggerPriceForDisplay, atMs = active.AtMs, active.RuleId });
                    break;
                case ViReleased released:
                    var previous = Vi!;
                    Vi = null;
                    Emit("viEvent", new { type = "Released", kind = previous.Kind.ToString(), previous.ReferencePrice,
                        triggerPrice = previous.TriggerPriceForDisplay, released.AtMs, previous.RuleId, auctionPrice = end?.Price });
                    break;
            }
        }
        return messages;

        void Emit(string name, object payload)
        {
            if (publish) messages.Add((name, payload));
        }
    }

    public object Snapshot(MatchingEngine engine, long seq)
    {
        var book = engine.GetBook(10);
        var expected = engine.GetIndicativeAuction();
        var vi = Vi is { } active ? new { kind = active.Kind.ToString(), active.ReferencePrice,
            triggerPrice = active.TriggerPriceForDisplay, startMs = active.AtMs, active.ScheduledEndMs, active.RuleId } : null;
        var core = new { symbol = Symbol, seq, logicalTimeMs = LogicalTimeMs, phase = engine.Phase.ToString(),
            engine.BasePrice, engine.UpperLimit, engine.LowerLimit, lastPrice = engine.DynamicReferencePrice,
            book.Asks, book.Bids, vi };
        return engine.Phase != TradingPhase.CallAuction ? core : new { core.symbol, core.seq, core.logicalTimeMs,
            core.phase, core.BasePrice, core.UpperLimit, core.LowerLimit, core.lastPrice, core.Asks, core.Bids, core.vi,
            expectedPrice = expected?.Price, expectedQuantity = expected?.Quantity };
    }

    public object? Explain(MatchingEngine engine, string id)
    {
        if (!_orders.TryGetValue(id, out var order)) return null;
        var position = engine.GetQueuePosition(id);
        var queueAhead = position is null ? null : new { orders = position.OrdersAhead, quantity = position.QuantityAhead };
        var (status, message) = order switch
        {
            { Failure: { } failure } => ("Rejected", RejectionMessage(failure)),
            { Cancelled: true } => ("Cancelled", "남은 주문이 취소되었습니다."),
            { Remaining: 0 } => ("Filled", "주문이 모두 체결되었습니다."),
            _ when Vi is { } vi => ("WaitingAuction", "단일가 체결 대기 중입니다. 예정 종료는 " +
                TimeSpan.FromMilliseconds(9 * 3_600_000 + vi.ScheduledEndMs).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                + "이며 최대 30초 연장될 수 있습니다."),
            _ when position is not null => (order.Remaining == order.Source.Quantity ? "Resting" : "PartiallyFilled",
                $"같은 가격 대기열 {position.OrdersAhead + 1}번째, 앞에 {position.QuantityAhead.ToString("N0", CultureInfo.InvariantCulture)}주가 있습니다."),
            _ => throw new InvalidOperationException("Active order is absent from the engine book.")
        };
        return new { orderId = id, status, side = order.Source.Side.ToString(), order.Source.Price,
            remainingQuantity = order.Cancelled ? 0 : order.Remaining, queueAhead,
            reason = order.Failure?.Reason.ToString(), ruleId = order.Failure?.RuleId, message };
    }

    private static string RejectionMessage(OrderRejected failure)
    {
        var reason = failure.Reason switch
        {
            RejectReason.InvalidQuantity => "수량이 1주 이상이어야 합니다.",
            RejectReason.InvalidPrice => "가격이 1원 이상이어야 합니다.",
            RejectReason.InvalidOrderId => "주문 ID가 비어 있습니다.",
            RejectReason.OffTick => "가격이 호가단위에 맞지 않습니다.",
            RejectReason.AboveUpperLimit => "가격이 상한가를 넘었습니다.",
            RejectReason.BelowLowerLimit => "가격이 하한가보다 낮습니다.",
            RejectReason.DuplicateOrderId => "이미 사용한 주문 ID입니다.",
            _ => "주문을 찾을 수 없습니다."
        };
        return failure.RuleId is null ? reason : $"{reason} ({failure.RuleId})";
    }

    private sealed class OrderView(PlaceLimitOrder source)
    {
        public PlaceLimitOrder Source { get; } = source;
        public long Remaining { get; set; } = source.Quantity;
        public bool Cancelled { get; set; }
        public OrderRejected? Failure { get; private set; }
        public void Reject(OrderRejected failure) { Failure = failure; Remaining = 0; }
    }

    // Integral decimal preserves the JSON number while sums can exceed a single long trade quantity.
    private sealed record Candle(string Symbol, long StartMs, long Open, long High, long Low, long Close, decimal Volume);
}
