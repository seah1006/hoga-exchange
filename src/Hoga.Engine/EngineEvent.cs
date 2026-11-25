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
public sealed record Trade(string BuyOrderId, string SellOrderId, long Price, long Quantity, long Sequence) : EngineEvent
{
    public bool HasAggressor { get; init; } = true;
}
public sealed record OrderCancelled(string OrderId, long RemainingQuantity) : EngineEvent;
public sealed record OrderFilled(string OrderId) : EngineEvent;

public enum ViKind { Dynamic, Static }

public sealed record ViTriggered(ViKind Kind, long ReferencePrice, long TriggerPriceForDisplay,
    long AtMs, long ScheduledEndMs, string RuleId) : EngineEvent;
public sealed record AuctionCompleted(long Price, long Quantity, long AtMs) : EngineEvent;
public sealed record AuctionNoMatch(long AtMs) : EngineEvent;
public sealed record ViReleased(long AtMs) : EngineEvent;
