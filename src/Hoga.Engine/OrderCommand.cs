namespace Hoga.Engine;

public enum Side { Buy, Sell }

public abstract record OrderCommand(long Sequence, long LogicalTimeMs, string OrderId);

public sealed record PlaceLimitOrder(
    string OrderId, Side Side, long Price, long Quantity, string AccountId, long Sequence, long LogicalTimeMs)
    : OrderCommand(Sequence, LogicalTimeMs, OrderId);

public sealed record CancelOrder(string OrderId, long Sequence, long LogicalTimeMs)
    : OrderCommand(Sequence, LogicalTimeMs, OrderId);
