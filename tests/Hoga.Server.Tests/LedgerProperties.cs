using FsCheck.Xunit;
using Hoga.Engine;

namespace Hoga.Server.Tests;

public sealed class LedgerProperties
{
    [Property(MaxTest = 125)]
    public void CashAndSharesIncludingHoldsAreConservedWithExternalTradeFlows(byte[] operations)
    {
        const long price = 10_000, initialCash = 1_000_000, initialShares = 100;
        string[] members = ["u-one", "u-two"];
        var ledger = new AccountLedger(initialCash, initialShares, price);
        var engine = new MatchingEngine(price, new TickSizeTable());
        var inputs = new List<OrderCommand>();
        var owners = new Dictionary<string, string>();
        long cash = initialCash * 2, shares = initialShares * 2, seq = 0;
        foreach (var item in operations)
        {
            OrderCommand input;
            if ((item & 3) == 0 && owners.Count > 0)
                input = new CancelOrder(owners.Keys.Order(StringComparer.Ordinal).ElementAt(item % owners.Count), seq + 1, seq + 1);
            else
            {
                var id = "o-" + (seq + 1);
                var owner = (item & 8) != 0 ? "bot:test" : members[(item >> 2) & 1];
                var place = new PlaceLimitOrder(id, (item & 16) == 0 ? Side.Buy : Side.Sell,
                    price + ((item >> 5) % 3) * 50, item % 7 + 1, owner, seq + 1, seq + 1);
                if (ledger.Check(place) is not null) continue;
                owners.Add(id, owner);
                input = place;
            }
            seq++;
            inputs.Add(input);
            var events = engine.Process(input);
            ledger.Apply(input, events);
            foreach (var trade in events.OfType<Trade>())
            {
                if (members.Contains(owners[trade.BuyOrderId])) { cash -= trade.Price * trade.Quantity; shares += trade.Quantity; }
                if (members.Contains(owners[trade.SellOrderId])) { cash += trade.Price * trade.Quantity; shares -= trade.Quantity; }
            }
            var balances = members.Select(id => ledger.Snapshot(id, "", price)).ToArray();
            Assert.Equal(cash, balances.Sum(a => a.Cash + a.CashOnHold));
            Assert.Equal(shares, balances.Sum(a => a.Shares + a.SharesOnHold));
            Assert.All(balances, a => Assert.True(a.Cash >= 0 && a.Shares >= 0 && a.CashOnHold >= 0 && a.SharesOnHold >= 0));
        }
        var replay = new AccountLedger(initialCash, initialShares, price);
        var replayEngine = new MatchingEngine(price, new TickSizeTable());
        foreach (var input in inputs) replay.Apply(input, replayEngine.Process(input));
        Assert.Equal(engine.ComputeStateHash(), replayEngine.ComputeStateHash());
        Assert.Equal(ledger.Hash(), replay.Hash());
    }
}
