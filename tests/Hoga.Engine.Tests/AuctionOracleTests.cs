using FsCheck.Xunit;
using Xunit.Abstractions;

namespace Hoga.Engine.Tests;

public sealed class AuctionOracleTests(ITestOutputHelper output)
{
    private const int Runs = 300;
    private static readonly (long First, long Last)[] Bands =
    [
        (1_990, 2_010), (4_990, 5_010), (19_990, 20_050), (49_950, 50_100),
        (199_800, 200_500), (499_000, 501_000), (9_800, 10_200), (7_800, 7_870),
    ];

    private enum Path { NoMatch, Occupied, Article6, Grid }
    private sealed record Quote(string Id, bool Buy, long Price, long Quantity);
    private sealed record Result(Path Path, long Price = 0, long Quantity = 0);

    [Property(MaxTest = Runs)]
    [Trait("Rule", "R-AUCT-01")]
    [Trait("Rule", "R-AUCT-02")]
    [Trait("Rule", "R-AUCT-02a")]
    [Trait("Rule", "R-AUCT-03")]
    [Trait("Rule", "R-AUCT-05")]
    [Trait("Rule", "R-AUCT-07")]
    [Trait("Rule", "R-AUCT-08")]
    [Trait("Rule", "R-VI-04")]
    public void Indicative_final_price_quantity_and_order_fills_agree_with_independent_oracle(int seed)
    {
        foreach (var band in Bands)
            Compare(seed, band);
    }

    [Fact]
    public void Seeded_oracle_sample_reports_cancellations_no_match_and_grid_usage()
    {
        var seeds = new Random(20261125);
        var paths = new int[Enum.GetValues<Path>().Length];
        var cancellations = 0;
        var fallback = 0L;
        for (var i = 0; i < Runs; i++)
        {
            var seed = seeds.Next();
            foreach (var band in Bands)
            {
                var sample = Compare(seed, band);
                paths[(int)sample.Path]++;
                cancellations += sample.Cancellations;
                fallback += sample.GridFallback;
            }
        }
        Assert.True(cancellations > 0);
        Assert.True(paths[(int)Path.NoMatch] > 0);
        Assert.True(paths[(int)Path.Occupied] > 0);
        Assert.True(paths[(int)Path.Article6] > 0);
        output.WriteLine($"Oracle sample: comparisons={paths.Sum()}; mismatches=0; cancellations={cancellations}; " +
            $"no-match={paths[(int)Path.NoMatch]}; occupied={paths[(int)Path.Occupied]}; " +
            $"article-6={paths[(int)Path.Article6]}; grid-fallback={fallback}");
    }

    private static (Path Path, int Cancellations, long GridFallback) Compare(int seed, (long First, long Last) band)
    {
        var random = new Random(unchecked(seed * 397 + (int)band.First));
        var prices = AllTicks(band.First, band.Last).ToArray();
        var previous = prices[random.Next(prices.Length)];
        var session = new VolatilityTests.Session(previous, new(RandomSeed: seed));
        var trigger = SnapDown(previous * 9 / 10);
        Assert.IsType<OrderAccepted>(Assert.Single(session.Place("trigger-bid", Side.Buy, trigger)));
        Assert.Single(session.Place("trigger-ask", Side.Sell, trigger).OfType<ViTriggered>());
        Assert.IsType<OrderCancelled>(Assert.Single(session.Cancel("trigger-bid")));
        Assert.IsType<OrderCancelled>(Assert.Single(session.Cancel("trigger-ask")));

        var live = new List<Quote>();
        var sequence = 0;
        var mode = random.Next(8);
        if (mode == 0)
        {
            // Quantities and prices force 6항: neither occupied valid endpoint fills both at-price sides.
            Add(true, prices[^1], 1);
            Add(true, prices[0], 1);
            Add(false, prices[0], 1);
            Add(false, prices[1], 19);
        }
        else
        {
            var count = random.Next(4, 31);
            for (var i = 0; i < count; i++)
            {
                Add(i == 0 || (i != 1 && random.Next(2) == 0), prices[random.Next(prices.Length)], random.Next(1, 21));
            }
        }

        var cancelled = 0;
        foreach (var quote in live.ToArray())
        {
            if ((mode == 1 && !quote.Buy) || (mode >= 2 && random.Next(5) == 0))
            {
                Assert.Equal(new OrderCancelled(quote.Id, quote.Quantity), Assert.Single(session.Cancel(quote.Id, 2)));
                live.Remove(quote);
                cancelled++;
            }
        }
        Assert.Equal(TradingPhase.CallAuction, session.Engine.Phase);
        Assert.Empty(session.Advance(119_999));
        var expected = BruteForce(live, previous);
        var indication = session.Engine.GetIndicativeAuction();
        var events = session.Advance(150_001);
        var trades = events.OfType<Trade>().ToArray();
        Assert.Single(events.OfType<ViReleased>());
        if (expected.Path == Path.NoMatch)
        {
            Assert.Null(indication);
            Assert.Empty(trades);
            Assert.Empty(events.OfType<AuctionCompleted>());
            Assert.Single(events.OfType<AuctionNoMatch>());
        }
        else
        {
            Assert.Equal(new IndicativeAuction(expected.Price, expected.Quantity), indication);
            Assert.Empty(events.OfType<AuctionNoMatch>());
            var completion = Assert.Single(events.OfType<AuctionCompleted>());
            Assert.Equal((expected.Price, expected.Quantity), (completion.Price, completion.Quantity));
            Assert.Equal(expected.Quantity, trades.Sum(t => t.Quantity));
            Assert.All(trades, trade =>
            {
                Assert.False(trade.HasAggressor);
                Assert.Equal(expected.Price, trade.Price);
                Assert.True(trade.Quantity > 0);
            });
        }
        CheckAllocation(live.Where(q => q.Buy).OrderByDescending(q => q.Price).ToArray(),
            trades.ToLookup(t => t.BuyOrderId), expected.Quantity);
        CheckAllocation(live.Where(q => !q.Buy).OrderBy(q => q.Price).ToArray(),
            trades.ToLookup(t => t.SellOrderId), expected.Quantity);
        Assert.Equal(TradingPhase.Continuous, session.Engine.Phase);
        Assert.Equal(expected.Path == Path.Grid ? 1L : 0L, session.Engine.AuctionGridFallbackCount);
        return (expected.Path, cancelled, session.Engine.AuctionGridFallbackCount);

        void Add(bool buy, long price, long quantity)
        {
            var quote = new Quote($"quote-{sequence++}", buy, price, quantity);
            // During a VI auction even a crossing order is accepted without a trade or another VI.
            Assert.Equal(new OrderAccepted(quote.Id),
                Assert.Single(session.Place(quote.Id, buy ? Side.Buy : Side.Sell, price, quantity, 1)));
            live.Add(quote);
        }
    }

    // No engine calls, no aggregated levels or moving cursors: recount every raw quote at each price.
    private static Result BruteForce(List<Quote> quotes, long previous)
    {
        if (!quotes.Any(q => q.Buy) || !quotes.Any(q => !q.Buy))
            return new(Path.NoMatch);
        var bestBuy = quotes.Where(q => q.Buy).Max(q => q.Price);
        var bestSell = quotes.Where(q => !q.Buy).Min(q => q.Price);
        if (bestBuy < bestSell)
            return new(Path.NoMatch);

        var result = Pick(quotes.Select(q => q.Price).Distinct(), Path.Occupied, Path.Article6);
        return result ?? Pick(AllTicks(bestSell, bestBuy), Path.Grid, Path.Grid)
            ?? throw new InvalidOperationException("Independent oracle found no price for a crossed book.");

        Result? Pick(IEnumerable<long> prices, Path full, Path relaxed)
        {
            var h = new List<(long Price, long Quantity, bool Minimum)>();
            foreach (var price in prices)
            {
                var buys = quotes.Where(q => q.Buy && q.Price >= price).Sum(q => q.Quantity);
                var sells = quotes.Where(q => !q.Buy && q.Price <= price).Sum(q => q.Quantity);
                var quantity = Math.Min(buys, sells);
                var atBuy = quotes.Where(q => q.Buy && q.Price == price).Sum(q => q.Quantity);
                var atSell = quotes.Where(q => !q.Buy && q.Price == price).Sum(q => q.Quantity);
                var betterBuy = buys - atBuy;
                var betterSell = sells - atSell;
                if (quantity == 0 || Math.Max(betterBuy, betterSell) > quantity)
                    continue;
                if (quantity == buys || quantity == sells)
                {
                    var minimumBuy = betterBuy + Math.Min(1, atBuy);
                    var minimumSell = betterSell + Math.Min(1, atSell);
                    h.Add((price, quantity, quantity >= Math.Max(minimumBuy, minimumSell)));
                }
            }
            var valid = h.Where(c => c.Minimum).ToArray();
            if (valid.Length > 0)
            {
                var max = valid.Max(c => c.Quantity);
                return Select(valid.Where(c => c.Quantity == max), full);
            }
            return h.Count >= 2 ? Select(h, relaxed) : null;
        }

        Result Select(IEnumerable<(long Price, long Quantity, bool Minimum)> candidates, Path path)
        {
            var ordered = candidates.OrderBy(c => c.Price).ToArray();
            var chosen = previous >= ordered[^1].Price ? ordered[^1] : ordered.First(c => c.Price >= previous);
            return new(path, chosen.Price, chosen.Quantity);
        }
    }

    private static void CheckAllocation(Quote[] ordered, ILookup<string, Trade> actual, long volume)
    {
        Assert.All(actual, group => Assert.Contains(ordered, quote => quote.Id == group.Key));
        // OrderBy is stable, so quotes tied on price retain original submission order.
        var cumulative = 0L;
        foreach (var quote in ordered)
        {
            var fill = Math.Clamp(volume - cumulative, 0, quote.Quantity);
            Assert.Equal(fill, actual[quote.Id].Sum(t => t.Quantity));
            cumulative += quote.Quantity;
        }
    }

    private static IEnumerable<long> AllTicks(long first, long last)
    {
        for (var price = first; price <= last; price++)
            if (price % Tick(price) == 0)
                yield return price;
    }

    private static long SnapDown(long price) => price / Tick(price) * Tick(price);

    private static long Tick(long price) => price switch
    {
        < 2_000 => 1, < 5_000 => 5, < 20_000 => 10, < 50_000 => 50,
        < 200_000 => 100, < 500_000 => 500, _ => 1_000,
    };
}
