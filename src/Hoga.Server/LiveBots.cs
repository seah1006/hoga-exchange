using Hoga.Bots;
using Hoga.Engine;

namespace Hoga.Server;

// Runs exclusively on the market queue. Strategies produce proposals, never engine commands.
internal sealed class LiveBots(long seed, long basePrice, double intensity = 1.0)
{
    internal const long TickMs = 100; // Same logical grid as TASK-005 (toy setting).
    internal const long CooldownMs = 180_000; // 임의 지정: includes a possible 150s VI auction.
    private readonly TickSizeTable _ticks = new();
    private readonly SplitMix64 _fairStream = SplitMix64.ForFairValue(seed);
    private readonly Bot[] _regular = [new MarketMaker(seed), new Noise(seed), new Momentum(seed)];
    private readonly Bot _fatFinger = new FatFinger(seed);
    private readonly Bot _panic = new PanicSeller(seed);
    private readonly Dictionary<string, Bot> _owners = new(StringComparer.Ordinal);
    private long _fair = basePrice, _previousClose = basePrice, _downStreak;
    private long _expires;
    private bool _fired;
    internal Scenario CurrentScenario { get; private set; }

    public async ValueTask Select(Scenario scenario, long now, Func<string, Task> cancel)
    {
        if (CurrentScenario == Scenario.PanicSell)
            foreach (var id in _panic.LiveOrders.ToArray()) await cancel(id);
        CurrentScenario = scenario;
        _expires = checked(now + CooldownMs);
        _fired = false;
    }

    public async ValueTask Step(long tick, MatchingEngine engine,
        Func<string, Task> cancel, Func<string, Side, long, long, Task> place)
    {
        if (CurrentScenario != Scenario.Calm && tick >= _expires) await Select(Scenario.Calm, tick, cancel);
        // Only the regular strategy clock slows down; the market and scenario clock do not.
        var strategyStep = (long)Math.Floor(tick / TickMs * intensity);
        var regularDue = strategyStep > (long)Math.Floor((tick / TickMs - 1) * intensity);
        if (regularDue)
        {
            _fair = Math.Clamp(_fair + 10 * (_fairStream.NextBelow(3) - 1), engine.LowerLimit, engine.UpperLimit);
            foreach (var bot in _regular)
                if (bot.Requote(View(strategyStep * TickMs, engine)) is { } quotes) await Requote(bot, quotes);
        }
        if (!_fired && CurrentScenario == Scenario.FatFinger)
        {
            _fired = true;
            // Prime an empty/early book without waiting for the first slow maker refresh (demo policy).
            if (intensity < 1 && _regular[0].LiveOrders.Count == 0)
                await Requote(_regular[0], _regular[0].Requote(View(500, engine))!);
            await Requote(_fatFinger, _fatFinger.Requote(View(60_000, engine))!);
        }
        if (!_fired && CurrentScenario == Scenario.PanicSell && tick % 1_000 == 0)
        {
            _fired = true;
            // Selecting the scenario simulates two down ticks (임의 지정), so the demo fires once even in a rising market.
            await Requote(_panic, _panic.Requote(View(tick, engine) with { DownStreak = Math.Max(2, _downStreak) })!);
        }
        if (regularDue && strategyStep % 10 == 0)
        {
            _downStreak = engine.DynamicReferencePrice < _previousClose ? _downStreak + 1 : 0;
            _previousClose = engine.DynamicReferencePrice;
        }

        async Task Requote(Bot bot, IReadOnlyList<Quote> quotes)
        {
            foreach (var id in bot.LiveOrders.ToArray()) await cancel(id);
            foreach (var quote in quotes)
            {
                var price = _ticks.FloorToTick(Math.Clamp(quote.Price, engine.LowerLimit, engine.UpperLimit));
                await place(bot.Name, quote.Side, price, quote.Quantity);
            }
        }
    }

    private MarketView View(long tick, MatchingEngine engine) =>
        new(tick, _fair, engine.DynamicReferencePrice, _previousClose, _downStreak, engine.LowerLimit);

    public void Observe(OrderCommand command, IReadOnlyList<EngineEvent> events)
    {
        if (command is PlaceLimitOrder p)
        {
            var account = p.AccountId.StartsWith("bot:", StringComparison.Ordinal) ? p.AccountId[4..] : p.AccountId;
            var bot = account switch
            {
                "MarketMaker" => _regular[0], "Noise" => _regular[1], "Momentum" => _regular[2],
                "FatFinger" => _fatFinger, "PanicSeller" => _panic, _ => null
            };
            if (bot is not null)
            {
                _owners.Add(p.OrderId, bot);
                bot.LiveOrders.Add(p.OrderId);
            }
        }
        foreach (var e in events)
        {
            switch (e)
            {
                case Trade t:
                    if (_owners.TryGetValue(t.BuyOrderId, out var buyer)) buyer.Inventory += t.Quantity;
                    if (_owners.TryGetValue(t.SellOrderId, out var seller)) seller.Inventory -= t.Quantity;
                    break;
                case OrderFilled filled: Forget(filled.OrderId); break;
                case OrderCancelled cancelled: Forget(cancelled.OrderId); break;
                case OrderRejected rejected when command is PlaceLimitOrder: Forget(rejected.OrderId); break;
            }
        }
    }

    private void Forget(string id)
    {
        if (_owners.Remove(id, out var bot)) bot.LiveOrders.Remove(id);
    }
}
