using System.Security.Cryptography;

namespace Hoga.Engine;

public sealed record BookLevel(long Price, long Quantity, int OrderCount);
public sealed record BookSnapshot(IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks);
public sealed record QueuePosition(int OrdersAhead, long QuantityAhead);
public enum OrderStatus { Resting, WaitingAuction }

public sealed partial class MatchingEngine
{
    public BookSnapshot GetBook(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        return new(GetLevels(_bids, depth), GetLevels(_asks, depth));
    }

    public QueuePosition? GetQueuePosition(string orderId)
    {
        if (!_active.TryGetValue(orderId, out var node))
            return null;
        var orders = 0;
        var quantity = 0L;
        for (var cursor = node.List!.First; cursor != node; cursor = cursor!.Next)
        {
            orders++;
            quantity = checked(quantity + cursor!.Value.Remaining);
        }
        return new(orders, quantity);
    }

    // The engine reports active limit orders only; terminal lifecycle events are not retained here.
    public OrderStatus? GetOrderStatus(string orderId) => !_active.ContainsKey(orderId) ? null :
        Phase == TradingPhase.CallAuction ? OrderStatus.WaitingAuction : OrderStatus.Resting;

    public IndicativeAuction? GetIndicativeAuction() =>
        Phase == TradingPhase.CallAuction ? CalculateAuction() : null;

    public string ComputeStateHash()
    {
        // Canonical bytes (little-endian): Int64 last sequence, base price, upper limit, lower limit;
        // tick rule ID; Int32 accepted ID count, then IDs in StringComparer.Ordinal order;
        // bids descending then asks ascending, each prefixed with an Int32 level count.
        // Per level: Int64 price, Int32 order count; per FIFO order: Int64 sequence, ID, Int64 remaining.
        // Strings: Int32 length in UTF-16 code units, followed by each code unit as UInt16.
        // The single book above is the active continuous or auction book, depending on phase.
        // TASK-002 suffix: two Boolean flags (VI enabled, KOSPI200), Int32 phase,
        // Int64 dynamic/static references, UInt64 PRNG state and Boolean active VI.
        // With active VI: Int32 kind, Int64 reference/display/start/scheduled end/actual end, rule ID.
        // Booleans occupy one byte. Prior command times and accounts are not stored state.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(_lastSequence);
        writer.Write(BasePrice);
        writer.Write(UpperLimit);
        writer.Write(LowerLimit);
        WriteString(writer, _tickSizeRule.RuleId);
        writer.Write(_acceptedIds.Count);
        foreach (var id in _acceptedIds.Order(StringComparer.Ordinal))
            WriteString(writer, id);
        WriteSide(writer, _bids);
        WriteSide(writer, _asks);
        writer.Write(_options.ViEnabled);
        writer.Write(_options.IsKospi200);
        writer.Write((int)Phase);
        writer.Write(DynamicReferencePrice);
        writer.Write(StaticReferencePrice);
        writer.Write(_rng.State);
        writer.Write(_ongoingVi is not null);
        if (_ongoingVi is { } vi)
        {
            writer.Write((int)vi.Kind);
            writer.Write(vi.ReferencePrice);
            writer.Write(vi.TriggerPriceForDisplay);
            writer.Write(vi.AtMs);
            writer.Write(vi.ScheduledEndMs);
            writer.Write(_actualEndMs);
            WriteString(writer, vi.RuleId);
        }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static IReadOnlyList<BookLevel> GetLevels(
        SortedDictionary<long, LinkedList<RestingOrder>> side, int depth) =>
        side.Take(depth).Select(level => new BookLevel(level.Key, level.Value.Sum(order => order.Remaining), level.Value.Count))
            .ToList().AsReadOnly();

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        foreach (var unit in value)
            writer.Write((ushort)unit);
    }

    private static void WriteSide(BinaryWriter writer, SortedDictionary<long, LinkedList<RestingOrder>> side)
    {
        writer.Write(side.Count);
        foreach (var (price, queue) in side)
        {
            writer.Write(price);
            writer.Write(queue.Count);
            foreach (var order in queue)
            {
                writer.Write(order.Sequence);
                WriteString(writer, order.Id);
                writer.Write(order.Remaining);
            }
        }
    }
}
