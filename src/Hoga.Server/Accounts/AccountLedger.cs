using System.Security.Cryptography;
using System.Text.Json;
using Hoga.Engine;

namespace Hoga.Server;

internal sealed record AccountSnapshot(string Round, long Cash, long CashOnHold, long Shares, long SharesOnHold,
    long InitialValuation, long LastPrice, long Valuation, long Pnl, long ReturnBp);
internal sealed record AccountOrder(string OrderId, string Side, long Price, long Quantity, long FilledQuantity,
    long RemainingQuantity, string Status, long CreatedAtMs);
internal sealed record AccountTrade(string OrderId, string Side, long Price, long Quantity, long LogicalTimeMs);
internal sealed record RankCandidate(string Id, long FirstSequence, long ReturnBp, long Valuation);

// Owned by the market loop. Apply is also the sole entry point during replay.
internal sealed class AccountLedger(long initialCash, long initialShares, long basePrice)
{
    private const int HistoryLimit = 200;
    private readonly Dictionary<string, Balance> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _open = new(StringComparer.Ordinal);
    private readonly long _initialValue = checked(initialCash + initialShares * basePrice);

    // Legacy public orders and bots are external liquidity, not funded member accounts.
    private static bool Member(string id) => id.StartsWith("u-", StringComparison.Ordinal);
    private Balance Get(string id)
    {
        if (!_accounts.TryGetValue(id, out var value)) _accounts.Add(id, value = new(initialCash, initialShares));
        return value;
    }

    public string? Check(PlaceLimitOrder order)
    {
        if (!Member(order.AccountId) || order.Quantity <= 0 || order.Price <= 0) return null;
        var balance = Get(order.AccountId);
        return order.Side == Side.Buy
            ? (Int128)order.Price * order.Quantity > balance.Cash ? "InsufficientCash" : null
            : order.Quantity > balance.Shares ? "InsufficientShares" : null;
    }

    public IEnumerable<string> OpenOrders(string id) => _open.Values.Where(o => o.Source.AccountId == id)
        .OrderBy(o => o.Source.Sequence).Select(o => o.Source.OrderId);

    public HashSet<string> Apply(OrderCommand command, IReadOnlyList<EngineEvent> events)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (command is PlaceLimitOrder order && Member(order.AccountId))
        {
            var balance = Get(order.AccountId);
            if (balance.First == 0) balance.First = order.Sequence;
            var entry = new Entry(order);
            Push(balance.Orders, entry);
            if (events.Any(e => e is OrderRejected)) { entry.Rejected = true; entry.Remaining = 0; }
            else
            {
                if (Check(order) is not null) throw new InvalidDataException("Negative ledger balance while reserving order hold.");
                _open.Add(order.OrderId, entry);
                if (order.Side == Side.Buy)
                {
                    var amount = checked(order.Price * order.Quantity);
                    balance.Cash -= amount;
                    balance.CashHold += amount;
                }
                else { balance.Shares -= order.Quantity; balance.ShareHold += order.Quantity; }
                changed.Add(order.AccountId);
            }
        }
        var auction = events.OfType<AuctionCompleted>().SingleOrDefault();
        foreach (var e in events)
        {
            if (e is Trade trade)
            {
                var at = trade.HasAggressor ? command.LogicalTimeMs : auction!.AtMs;
                Settle(trade.BuyOrderId, trade, at);
                Settle(trade.SellOrderId, trade, at);
            }
            else if (e is OrderCancelled cancel && _open.Remove(cancel.OrderId, out var entry))
            {
                var orderSource = entry.Source;
                var balance = Get(orderSource.AccountId);
                if (orderSource.Side == Side.Buy)
                {
                    var amount = checked(orderSource.Price * entry.Remaining);
                    balance.CashHold -= amount;
                    balance.Cash += amount;
                }
                else { balance.ShareHold -= entry.Remaining; balance.Shares += entry.Remaining; }
                entry.Remaining = 0;
                entry.Cancelled = true;
                changed.Add(orderSource.AccountId);
            }
        }
        foreach (var id in changed)
        {
            var b = Get(id);
            if (b.Cash < 0 || b.Shares < 0 || b.CashHold < 0 || b.ShareHold < 0)
                throw new InvalidDataException("Negative ledger balance or hold.");
        }
        return changed;

        void Settle(string id, Trade t, long at)
        {
            if (!_open.TryGetValue(id, out var entry)) return;
            if (t.Quantity <= 0 || t.Quantity > entry.Remaining) throw new InvalidDataException("Trade exceeds ledger order hold.");
            var source = entry.Source;
            var balance = Get(source.AccountId);
            checked
            {
                if (source.Side == Side.Buy)
                {
                    balance.CashHold -= source.Price * t.Quantity;
                    balance.Cash += (source.Price - t.Price) * t.Quantity;
                    balance.Shares += t.Quantity;
                }
                else { balance.ShareHold -= t.Quantity; balance.Cash += t.Price * t.Quantity; }
                entry.Remaining -= t.Quantity;
                entry.Filled += t.Quantity;
            }
            if (entry.Remaining == 0) _open.Remove(id);
            Push(balance.Trades, new(id, source.Side.ToString(), t.Price, t.Quantity, at));
            changed.Add(source.AccountId);
        }
    }

    internal void VerifyOpenHolds()
    {
        var holds = new Dictionary<string, (Int128 Cash, Int128 Shares)>(StringComparer.Ordinal);
        foreach (var entry in _open.Values)
        {
            if (entry.Remaining <= 0 || entry.Remaining + entry.Filled != entry.Source.Quantity)
                throw new InvalidDataException("Invalid open ledger order.");
            var id = entry.Source.AccountId;
            var (cash, shares) = holds.GetValueOrDefault(id);
            holds[id] = entry.Source.Side == Side.Buy
                ? (cash + (Int128)entry.Source.Price * entry.Remaining, shares) : (cash, shares + entry.Remaining);
        }
        foreach (var (id, balance) in _accounts)
        {
            var (cash, shares) = holds.GetValueOrDefault(id);
            if (cash != balance.CashHold || shares != balance.ShareHold)
                throw new InvalidDataException("Open ledger orders do not match holds.");
        }
    }

    public AccountSnapshot Snapshot(string id, string round, long price) => Snapshot(Get(id), round, price);
    private AccountSnapshot Snapshot(Balance b, string round, long price)
    {
        var valuation = checked((long)((Int128)b.Cash + b.CashHold + ((Int128)b.Shares + b.ShareHold) * price));
        var pnl = checked(valuation - _initialValue);
        return new(round, b.Cash, b.CashHold, b.Shares, b.ShareHold, _initialValue, price, valuation, pnl,
            checked((long)((Int128)pnl * 10_000 / _initialValue)));
    }
    public AccountOrder[] Orders(string id, bool inAuction) => Get(id).Orders.Reverse().Select(o => o.View(inAuction)).ToArray();
    public AccountTrade[] Trades(string id) => Get(id).Trades.Reverse().ToArray();
    public RankCandidate[] Standings(long price) => _accounts.Where(a => a.Value.First > 0).Select(a =>
    {
        var snapshot = Snapshot(a.Value, "", price);
        return new RankCandidate(a.Key, a.Value.First, snapshot.ReturnBp, snapshot.Valuation);
    }).OrderByDescending(a => a.ReturnBp).ThenBy(a => a.FirstSequence).ToArray();

    // Read-created untouched balances cannot appear in the command journal: omit them from the hash.
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        initialCash, initialShares, basePrice,
        accounts = _accounts.Where(a => a.Value.First != 0).OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new { a.Key, a.Value.First, a.Value.Cash, a.Value.CashHold, a.Value.Shares, a.Value.ShareHold,
                orders = a.Value.Orders.Select(o => o.View(false)), a.Value.Trades }),
        open = _open.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new { a.Value.Source, a.Value.Remaining })
    })));

    private static void Push<T>(Queue<T> queue, T value)
    {
        if (queue.Count == HistoryLimit) queue.Dequeue();
        queue.Enqueue(value);
    }
    private sealed class Balance(long cash, long shares)
    {
        public long Cash = cash, CashHold, Shares = shares, ShareHold, First;
        public Queue<Entry> Orders { get; } = new();
        public Queue<AccountTrade> Trades { get; } = new();
    }
    private sealed class Entry(PlaceLimitOrder source)
    {
        public PlaceLimitOrder Source { get; } = source;
        public long Remaining = source.Quantity, Filled;
        public bool Cancelled, Rejected;
        public AccountOrder View(bool auction) => new(Source.OrderId, Source.Side.ToString(), Source.Price, Source.Quantity,
            Filled, Remaining, Rejected ? "Rejected" : Cancelled ? "Cancelled" : Remaining == 0 ? "Filled" :
                auction ? "WaitingAuction" : Filled > 0 ? "PartiallyFilled" : "Resting", Source.LogicalTimeMs);
    }
}
