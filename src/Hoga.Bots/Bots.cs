using Hoga.Engine;

namespace Hoga.Bots;

internal readonly record struct MarketView(long TimeMs, long FairValue, long LastPrice,
    long PreviousClose, long DownStreak, long LowerLimit);

internal readonly record struct Quote(Side Side, long Price, long Quantity);

internal abstract class Bot(string name, long seed)
{
    public string Name { get; } = name;
    public long Inventory { get; set; }
    public List<string> LiveOrders { get; } = [];
    internal SplitMix64 Stream { get; } = SplitMix64.ForBot(seed, name);

    // Null: retain existing orders. Empty: cancel without replacement.
    public abstract IReadOnlyList<Quote>? Requote(MarketView market);
}

// Timings, depths and quantities are toy-market parameters (임의 지정).
internal sealed class MarketMaker(long seed) : Bot("MarketMaker", seed)
{
    private static readonly long[] Offsets = [30, 130, 340, 650, 1_050];

    public override IReadOnlyList<Quote>? Requote(MarketView market)
    {
        if (market.TimeMs % 500 != 0)
            return null;
        var center = market.FairValue - Math.Clamp(Inventory / 5, -180, 180);
        var quotes = new Quote[Offsets.Length * 2];
        for (var i = 0; i < Offsets.Length; i++)
        {
            quotes[2 * i] = new(Side.Buy, center - Offsets[i], 240 + Stream.NextBelow(81));
            quotes[2 * i + 1] = new(Side.Sell, center + Offsets[i], 240 + Stream.NextBelow(81));
        }
        return quotes;
    }
}

internal sealed class Noise(long seed) : Bot("Noise", seed)
{
    public override IReadOnlyList<Quote>? Requote(MarketView market)
    {
        if (market.TimeMs % 200 != 0)
            return null;
        var side = Stream.NextBelow(2) == 0 ? Side.Buy : Side.Sell;
        return [new(side, market.LastPrice + 10 * (Stream.NextBelow(15) - 7), 10 + Stream.NextBelow(41))];
    }
}

internal sealed class Momentum(long seed) : Bot("Momentum", seed)
{
    public override IReadOnlyList<Quote>? Requote(MarketView market)
    {
        if (market.TimeMs % 1_000 != 0)
            return null;
        var quantity = 20 + Stream.NextBelow(41);
        return market.LastPrice.CompareTo(market.PreviousClose) switch
        {
            > 0 => [new(Side.Buy, market.LastPrice + 60, quantity)],
            < 0 => [new(Side.Sell, market.LastPrice - 60, quantity)],
            _ => [],
        };
    }
}

internal sealed class PanicSeller(long seed) : Bot("PanicSeller", seed)
{
    private long? _previousOffer;

    public override IReadOnlyList<Quote>? Requote(MarketView market)
    {
        if (market.TimeMs % 1_000 != 0)
            return null;
        var jitter = Stream.NextBelow(51);
        if (market.DownStreak < 2)
        {
            _previousOffer = null;
            return [];
        }
        var offer = market.LastPrice - 100 * market.DownStreak;
        if (_previousOffer is long previous)
            offer = Math.Min(offer, previous - 50);
        offer = Math.Max(market.LowerLimit, offer);
        _previousOffer = offer;
        return [new(Side.Sell, offer, 100 * market.DownStreak + jitter)];
    }
}

internal sealed class FatFinger(long seed) : Bot("FatFinger", seed)
{
    // Retain any unfilled remainder: there is no separate cancellation or second order.
    public override IReadOnlyList<Quote>? Requote(MarketView market) => market.TimeMs == 60_000
        ? [new(Side.Sell, market.LowerLimit + 20, 1_600)] : null;
}
