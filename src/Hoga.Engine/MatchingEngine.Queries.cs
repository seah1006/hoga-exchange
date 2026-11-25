using System.Security.Cryptography;

namespace Hoga.Engine;

public sealed record BookLevel(long Price, long Quantity, int OrderCount);
public sealed record BookSnapshot(IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks);
public sealed record QueuePosition(int OrdersAhead, long QuantityAhead);

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

    public string ComputeStateHash()
    {
        // Canonical bytes: bids descending then asks ascending; each side starts with an Int32 level count.
        // Per level: Int64 price, Int32 order count. Per FIFO order: Int64 sequence, Int32 ID length in
        // UTF-16 code units, each code unit as UInt16, Int64 remaining. All values are little-endian.
        // Only the book is included, not the last input sequence, order-ID history or logical time.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteSide(writer, _bids);
        WriteSide(writer, _asks);
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static IReadOnlyList<BookLevel> GetLevels(
        SortedDictionary<long, LinkedList<RestingOrder>> side, int depth) =>
        side.Take(depth).Select(level => new BookLevel(level.Key, level.Value.Sum(order => order.Remaining), level.Value.Count))
            .ToList().AsReadOnly();

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
                writer.Write(order.Id.Length);
                foreach (var unit in order.Id)
                    writer.Write((ushort)unit);
                writer.Write(order.Remaining);
            }
        }
    }
}
