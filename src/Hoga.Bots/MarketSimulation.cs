using System.Collections.ObjectModel;
using System.Globalization;
using Hoga.Engine;

namespace Hoga.Bots;

internal sealed class MarketSimulation
{
    // Toy-market price, horizon and cadence (임의 지정). 240s includes the first FatFinger
    // auction's maximum end at 210s, but not necessarily every later auction.
    internal const long BasePrice = 10_000;
    internal const long TickMs = 100;
    internal const long EndMs = 240_000;
    private readonly TickSizeTable _ticks = new();
    private readonly MatchingEngine _engine;
    private readonly bool _viEnabled;
    private readonly Bot[] _bots;
    private readonly SplitMix64 _fairValueStream;
    private readonly Dictionary<string, Bot> _owners = new(StringComparer.Ordinal); // Lookup only.
    private readonly SortedDictionary<RejectReason, long> _rejections = new();
    private readonly List<ExperimentViEvent> _viEvents = [];
    private readonly List<PricePoint> _series = [new(0, BasePrice)];
    private long _timeMs;
    private long _sequence;
    private long _fairValue = BasePrice;
    private long _lastPrice = BasePrice;
    private long _previousClose = BasePrice;
    private long _minPrice = BasePrice;
    private long _maxPrice = BasePrice;
    // Keep the opening reference as a price candidate, but prefer a real trade at that price.
    private long? _minAtMs;
    private long? _maxAtMs;
    private long _downStreak;
    private ViTriggered? _activeVi;

    public MarketSimulation(long seed, Scenario scenario, bool viEnabled)
    {
        _viEnabled = viEnabled;
        _engine = new(BasePrice, _ticks, new(ViEnabled: viEnabled, RandomSeed: seed));
        _fairValueStream = SplitMix64.ForFairValue(seed);
        _bots = scenario switch
        {
            Scenario.Calm => [new MarketMaker(seed), new Noise(seed), new Momentum(seed)],
            Scenario.FatFinger => [new MarketMaker(seed), new Noise(seed), new Momentum(seed), new FatFinger(seed)],
            Scenario.PanicSell => [new MarketMaker(seed), new Noise(seed), new Momentum(seed), new PanicSeller(seed)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    public (ExperimentRun Run, RunDiagnostics Diagnostics) Run(Action<Trade, long>? observeTrade = null)
    {
        for (_timeMs = TickMs; _timeMs <= EndMs; _timeMs += TickMs)
        {
            Process(new AdvanceTime(++_sequence, _timeMs), observeTrade);
            _fairValue = Math.Clamp(_fairValue + 10 * (_fairValueStream.NextBelow(3) - 1),
                _engine.LowerLimit, _engine.UpperLimit);
            foreach (var bot in _bots)
            {
                var view = new MarketView(_timeMs, _fairValue, _lastPrice, _previousClose, _downStreak,
                    _engine.LowerLimit);
                if (bot.Requote(view) is { } quotes)
                    Requote(bot, quotes, observeTrade);
            }
            if (_timeMs % 1_000 == 0)
            {
                _series.Add(new(_timeMs, _lastPrice));
                _downStreak = _lastPrice < _previousClose ? _downStreak + 1 : 0;
                _previousClose = _lastPrice;
            }
        }
        return (new(_viEnabled, _series.AsReadOnly(), _viEvents.AsReadOnly(), _minPrice, _maxPrice, _minAtMs ?? 0, _maxAtMs ?? 0),
            new(_viEnabled, new ReadOnlyDictionary<RejectReason, long>(_rejections)));
    }

    private void Requote(Bot bot, IReadOnlyList<Quote> quotes, Action<Trade, long>? observeTrade)
    {
        foreach (var id in bot.LiveOrders.ToArray())
            Process(new CancelOrder(id, ++_sequence, _timeMs), observeTrade);
        foreach (var quote in quotes)
        {
            var sequence = ++_sequence;
            var id = sequence.ToString(CultureInfo.InvariantCulture);
            var price = _ticks.FloorToTick(Math.Clamp(quote.Price, _engine.LowerLimit, _engine.UpperLimit));
            // Track incoming ownership before Process: immediate fills can refer to this ID.
            _owners.Add(id, bot);
            bot.LiveOrders.Add(id);
            Process(new PlaceLimitOrder(id, quote.Side, price, quote.Quantity, bot.Name, sequence, _timeMs), observeTrade);
        }
    }

    private void Process(OrderCommand command, Action<Trade, long>? observeTrade)
    {
        long? auctionPrice = null;
        long? auctionAtMs = null;
        var events = _engine.Process(command);
        foreach (var engineEvent in events)
        {
            switch (engineEvent)
            {
                case Trade trade:
                    _owners[trade.BuyOrderId].Inventory += trade.Quantity;
                    _owners[trade.SellOrderId].Inventory -= trade.Quantity;
                    _lastPrice = trade.Price;
                    // AuctionCompleted follows the auction trades and carries the exact end,
                    // which may precede the simulation tick that closes the auction.
                    var atMs = trade.HasAggressor ? command.LogicalTimeMs
                        : (auctionAtMs ??= events.OfType<AuctionCompleted>().Single().AtMs);
                    if (trade.Price < _minPrice || (trade.Price == _minPrice && _minAtMs is null))
                        (_minPrice, _minAtMs) = (trade.Price, atMs);
                    if (trade.Price > _maxPrice || (trade.Price == _maxPrice && _maxAtMs is null))
                        (_maxPrice, _maxAtMs) = (trade.Price, atMs);
                    observeTrade?.Invoke(trade, atMs);
                    break;
                case OrderFilled filled:
                    ForgetOrder(filled.OrderId);
                    break;
                case OrderCancelled cancelled:
                    ForgetOrder(cancelled.OrderId);
                    break;
                case OrderRejected rejected:
                    _rejections[rejected.Reason] = _rejections.GetValueOrDefault(rejected.Reason) + 1;
                    ForgetOrder(rejected.OrderId);
                    break;
                case ViTriggered triggered:
                    _activeVi = triggered;
                    _viEvents.Add(ToViEvent("Triggered", triggered, triggered.AtMs));
                    break;
                case AuctionCompleted completed:
                    auctionPrice = completed.Price;
                    break;
                case ViReleased released:
                    var prior = _activeVi ?? throw new InvalidOperationException("VI release without a trigger.");
                    _viEvents.Add(ToViEvent("Released", prior, released.AtMs, auctionPrice));
                    _activeVi = null;
                    break;
            }
        }
    }

    private void ForgetOrder(string id)
    {
        if (_owners.Remove(id, out var bot))
            bot.LiveOrders.Remove(id);
    }

    private static ExperimentViEvent ToViEvent(string type, ViTriggered vi, long atMs, long? auctionPrice = null) =>
        new(type, vi.Kind.ToString(), atMs, vi.ReferencePrice, vi.TriggerPriceForDisplay, vi.RuleId, auctionPrice);
}
