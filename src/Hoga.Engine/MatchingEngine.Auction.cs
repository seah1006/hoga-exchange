namespace Hoga.Engine;

public sealed record IndicativeAuction(long Price, long Quantity);

public sealed partial class MatchingEngine
{
    internal IndicativeAuction? CalculateAuction() => FindAuction(
        GetLevels(_bids, _bids.Count), GetLevels(_asks, _asks.Count), DynamicReferencePrice);

    internal static IndicativeAuction? FindAuction(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice)
    {
        if (bids.Count == 0 || asks.Count == 0 || bids[0].Price < asks[0].Price)
            return null; // R-AUCT-07

        // Visit actual limit prices in ascending order. B+ and S- are strictly better prices,
        // B0 and S0 are the quantity at P. R-AUCT-01: V = min(B+ + B0, S- + S0).
        // R-AUCT-02: V >= B+ and V >= S-; of V-B+ and V-S-, one fills its whole
        // level and the other fills >= 1. An absent level has zero shares to fill.
        // R-AUCT-02a: retain exactly two positive, balanced prices even when the
        // at-price one-share condition does not hold. Compare volume after eligibility.
        // Wide running sums are only arithmetic scratch space, not stored order quantities.
        Int128 buy = 0;
        foreach (var bid in bids)
            buy += bid.Quantity;
        Int128 sell = 0;
        var best = new List<long>();
        var balanced = new List<(long Price, Int128 Volume)>();
        Int128 max = 0;
        var bi = bids.Count - 1;
        var ai = 0;
        while (bi >= 0 || ai < asks.Count)
        {
            var price = Math.Min(bi >= 0 ? bids[bi].Price : long.MaxValue,
                ai < asks.Count ? asks[ai].Price : long.MaxValue);
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
                if ((buyFill == atBuy && sellFill >= 1) || (sellFill == atSell && buyFill >= 1))
                    AddCandidate(price, volume);
                if (buy == sell)
                    balanced.Add((price, volume));
            }
            buy = betterBuy;
        }
        if (balanced.Count == 2)
            foreach (var (price, volume) in balanced)
                if (!best.Contains(price))
                    AddCandidate(price, volume);
        return best.Count == 0 ? null : new(ChooseAuctionPrice(best, previousPrice), checked((long)max));

        void AddCandidate(long price, Int128 volume)
        {
            if (volume > max)
            {
                max = volume;
                best.Clear();
            }
            if (volume == max)
                best.Add(price);
        }
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
