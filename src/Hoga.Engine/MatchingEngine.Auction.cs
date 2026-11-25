namespace Hoga.Engine;

public sealed record IndicativeAuction(long Price, long Quantity);

public sealed partial class MatchingEngine
{
    private IndicativeAuction? CalculateAuction() => FindAuction(
        GetLevels(_bids, _bids.Count), GetLevels(_asks, _asks.Count), DynamicReferencePrice, _tickSizeRule);

    internal static IndicativeAuction? FindAuction(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice, ITickSizeRule tickRule)
    {
        if (bids.Count == 0 || asks.Count == 0 || bids[0].Price < asks[0].Price)
            return null; // R-AUCT-07

        // R-AUCT-08: include empty tick levels; only [best ask, best bid] can match.
        // B+ and S- are strictly better quantities, B0 and S0 are quantities at P.
        // R-AUCT-01: V = min(B+ + B0, S- + S0).
        // R-AUCT-02: V >= B+ and V >= S-; one side fills its P level entirely,
        // and the other fills >= 1 if it has orders at P (R-AUCT-08).
        Int128 buy = 0;
        foreach (var bid in bids)
            if (bid.Price >= asks[0].Price)
                buy += bid.Quantity;
        Int128 sell = 0;
        var best = new List<long>();
        var withoutMinimum = new List<long>();
        Int128 max = 0;
        Int128 withoutMinimumMax = 0;
        var bi = bids.Count - 1;
        while (bids[bi].Price < asks[0].Price)
            bi--;
        var ai = 0;
        for (var price = asks[0].Price; price <= bids[0].Price;
             price += tickRule is TickSizeTable ? tickRule.GetTickSize(price) : 1)
        {
            // Standard KRX bands align. For a custom rule, do not skip prices based on the prior tick size.
            if (!tickRule.IsOnTick(price))
                continue;
            var atBuy = bi >= 0 && bids[bi].Price == price ? bids[bi--].Quantity : 0;
            var atSell = ai < asks.Count && asks[ai].Price == price ? asks[ai++].Quantity : 0;
            var betterSell = sell;
            sell += atSell;
            var volume = Int128.Min(buy, sell);
            var betterBuy = buy - atBuy;
            if (volume > 0 && volume >= betterBuy && volume >= betterSell)
            {
                var buyFill = volume - betterBuy;
                var sellFill = volume - betterSell;
                if (buyFill == atBuy || sellFill == atSell)
                    AddCandidate(withoutMinimum, ref withoutMinimumMax, price, volume);
                if ((buyFill == atBuy && (atSell == 0 || sellFill >= 1)) ||
                    (sellFill == atSell && (atBuy == 0 || buyFill >= 1)))
                    AddCandidate(best, ref max, price, volume);
            }
            buy = betterBuy;
        }
        if (best.Count != 0)
            return new(ChooseAuctionPrice(best, previousPrice), checked((long)max));
        // R-AUCT-02a (§23(6)): two prices can match when only the one-share
        // condition fails. Extending this to more than two is a simulator policy.
        if (withoutMinimum.Count >= 2)
            return new(ChooseAuctionPrice(withoutMinimum, previousPrice), checked((long)withoutMinimumMax));
        throw new InvalidOperationException("Crossed auction book has no eligible clearing price.");
    }

    private static void AddCandidate(List<long> candidates, ref Int128 max, long price, Int128 volume)
    {
        if (volume > max)
        {
            max = volume;
            candidates.Clear();
        }
        if (volume == max)
            candidates.Add(price);
    }

    // R-AUCT-05: exact match, otherwise smallest price above previous; if
    // previous is above every candidate, choose the highest candidate instead.
    internal static long ChooseAuctionPrice(IReadOnlyList<long> candidates, long previousPrice)
    {
        var highest = long.MinValue;
        var upper = long.MaxValue;
        foreach (var price in candidates)
        {
            highest = Math.Max(highest, price);
            if (price >= previousPrice)
                upper = Math.Min(upper, price);
        }
        return upper == long.MaxValue ? highest : upper;
    }

    private void ExecuteAuction(IndicativeAuction match, long sequence, List<EngineEvent> events)
    {
        // R-AUCT-03: use the same price/FIFO queues as continuous trading, but
        // both sides are resting and every pair trades at the chosen price.
        var remaining = match.Quantity;
        while (remaining > 0)
        {
            var bid = _bids.First().Value.First!;
            var ask = _asks.First().Value.First!;
            var quantity = Math.Min(remaining, Math.Min(bid.Value.Remaining, ask.Value.Remaining));
            events.Add(new Trade(bid.Value.Id, ask.Value.Id, match.Price, quantity, sequence)
                { HasAggressor = false });
            remaining -= quantity;
            FillResting(bid, quantity, events);
            FillResting(ask, quantity, events);
        }
    }

    private void FillResting(LinkedListNode<RestingOrder> node, long quantity, List<EngineEvent> events)
    {
        node.Value.Remaining -= quantity;
        if (node.Value.Remaining == 0)
        {
            Remove(node);
            events.Add(new OrderFilled(node.Value.Id));
        }
    }
}
