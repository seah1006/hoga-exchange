namespace Hoga.Engine;

public enum RejectReason
{
    InvalidQuantity,
    OffTick,
    AboveUpperLimit,
    BelowLowerLimit,
    DuplicateOrderId,
    UnknownOrder,
    InvalidPrice,
    InvalidOrderId,
}

public abstract record EngineEvent;

public sealed record OrderAccepted(string OrderId) : EngineEvent;
public sealed record OrderRejected(string OrderId, RejectReason Reason, string? RuleId) : EngineEvent;
public sealed record Trade(string BuyOrderId, string SellOrderId, long Price, long Quantity, long Sequence) : EngineEvent;
public sealed record OrderCancelled(string OrderId, long RemainingQuantity) : EngineEvent;
public sealed record OrderFilled(string OrderId) : EngineEvent;
