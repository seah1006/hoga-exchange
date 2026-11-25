namespace Hoga.Engine;

public sealed record IndicativeAuction(long Price, long Quantity);

public sealed partial class MatchingEngine
{
    // Diagnostics only: count final auctions, not indicative queries. No matching or hash dependency.
    internal long AuctionGridFallbackCount { get; private set; }

    private IndicativeAuction? CalculateAuction(out bool usedGridFallback) => FindAuction(
        GetLevels(_bids, _bids.Count), GetLevels(_asks, _asks.Count), DynamicReferencePrice, _tickSizeRule,
        out usedGridFallback);

    internal static IndicativeAuction? FindAuction(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice, ITickSizeRule tickRule) =>
        FindAuction(bids, asks, previousPrice, tickRule, out _);

    private static IndicativeAuction? FindAuction(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice, ITickSizeRule tickRule,
        out bool usedGridFallback)
    {
        usedGridFallback = false;
        if (bids.Count == 0 || asks.Count == 0 || bids[0].Price < asks[0].Price)
            return null; // R-AUCT-07

        // R-AUCT-08: try occupied prices first so §23(5)(2)(b) can apply between them.
        var prices = bids.Concat(asks).Select(level => level.Price)
            .Where(price => price >= asks[0].Price && price <= bids[0].Price).Distinct().Order();
        var match = ScanAuctionPrices(bids, asks, previousPrice, prices);
        if (match is not null)
            return match;

        // R-AUCT-08: arbitrary simulator safeguard, not in §23. Only here consider empty ticks.
        usedGridFallback = true;
        return FindAuctionOnGrid(bids, asks, previousPrice, tickRule);
    }

    internal static IndicativeAuction FindAuctionOnGrid(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice, ITickSizeRule tickRule) =>
        ScanAuctionPrices(bids, asks, previousPrice, GridPrices(asks[0].Price, bids[0].Price, tickRule)) ??
        throw new InvalidOperationException("Crossed auction book has no eligible clearing price.");

    private static IEnumerable<long> GridPrices(long first, long last, ITickSizeRule tickRule)
    {
        // Standard KRX bands align. A custom rule may not have aligned bands.
        for (var price = first; price <= last; price += tickRule is TickSizeTable ? tickRule.GetTickSize(price) : 1)
            if (tickRule.IsOnTick(price))
                yield return price;
    }

    private static IndicativeAuction? ScanAuctionPrices(
        IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, long previousPrice, IEnumerable<long> prices)
    {
        // B+ and S- are strictly better quantities, B0 and S0 are at P.
        // R-AUCT-01: V = min(B+ + B0, S- + S0).
        Int128 buy = 0;
        foreach (var bid in bids)
            if (bid.Price >= asks[0].Price)
                buy += bid.Quantity;
        Int128 sell = 0;
        var best = new List<long>();
        var withoutMinimum = new Dictionary<long, Int128>();
        Int128 max = 0;
        var bi = bids.Count - 1;
        while (bids[bi].Price < asks[0].Price)
            bi--;
        var ai = 0;
        foreach (var price in prices)
        {
            var atBuy = bi >= 0 && bids[bi].Price == price ? bids[bi--].Quantity : 0;
            var atSell = ai < asks.Count && asks[ai].Price == price ? asks[ai++].Quantity : 0;
            var betterSell = sell;
            sell += atSell;
            var volume = Int128.Min(buy, sell);
            var betterBuy = buy - atBuy;
            // §23(4)(1): all better prices fill. §23(4)(2)(a): one side at P fills.
            if (volume > 0 && volume >= betterBuy && volume >= betterSell)
            {
                var buyFill = volume - betterBuy;
                var sellFill = volume - betterSell;
                if (buyFill == atBuy || sellFill == atSell)
                    withoutMinimum.Add(price, volume);
                // §23(4)(2)(b): if the other side has orders at P, it fills at least one.
                if ((buyFill == atBuy && (atSell == 0 || sellFill >= 1)) ||
                    (sellFill == atSell && (atBuy == 0 || buyFill >= 1)))
                    AddCandidate(best, ref max, price, volume);
            }
            buy = betterBuy;
        }
        if (best.Count != 0)
            return new(ChooseAuctionPrice(best, previousPrice), checked((long)max));
        // R-AUCT-02a (§23(6)): two prices can match despite (4)(2)(b).
        // Extending this to more than two is a simulator policy. Use all of H.
        if (withoutMinimum.Count >= 2)
        {
            var price = ChooseAuctionPrice(withoutMinimum.Keys.Order().ToArray(), previousPrice);
            return new(price, checked((long)withoutMinimum[price]));
        }
        return null;
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
