using System.Globalization;
using Hoga.Bots;

namespace Hoga.Server;

internal sealed class ExperimentCache(TimeSpan timeout, Func<long, Scenario, Experiment> calculate)
{
    internal const int Capacity = 8; // 임의 지정
    private readonly object _sync = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Dictionary<(Scenario, int), Experiment> _cache = [];
    private readonly Queue<(Scenario, int)> _insertionOrder = new();
    private readonly Dictionary<(Scenario, int), Task<Experiment?>> _inFlight = [];

    public async Task<Experiment?> Get(string id, CancellationToken cancellationToken)
    {
        if (!Parse(id, out var key)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        Task<Experiment?> pending;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (!_inFlight.TryGetValue(key, out pending!))
            {
                // The shared computation must not inherit any one HTTP request's cancellation.
                pending = Task.Run(() => Calculate(key));
                _inFlight.Add(key, pending);
            }
        }
        return await pending.WaitAsync(timeout, cancellationToken) ?? throw new TimeoutException();
    }

    private async Task<Experiment?> Calculate((Scenario, int) key)
    {
        var acquired = false;
        try
        {
            acquired = await _slot.WaitAsync(timeout);
            if (!acquired) return null;
            var result = calculate(key.Item2, key.Item1);
            lock (_sync)
            {
                if (_cache.Count == Capacity) _cache.Remove(_insertionOrder.Dequeue());
                _cache.Add(key, result);
                _insertionOrder.Enqueue(key);
            }
            return result;
        }
        finally
        {
            lock (_sync) _inFlight.Remove(key);
            if (acquired) _slot.Release();
        }
    }

    internal static Scenario? ScenarioName(string? name) => name switch
    {
        "Calm" => Scenario.Calm, "FatFinger" => Scenario.FatFinger, "PanicSell" => Scenario.PanicSell, _ => null
    };

    private static bool Parse(string id, out (Scenario, int) key)
    {
        // SUMMARY.md: closest delta to the FatFinger median, smallest seed on ties.
        if (id == "demo") { key = (Scenario.FatFinger, 16); return true; }
        key = default;
        if (!id.StartsWith("exp-", StringComparison.Ordinal)) return false;
        var parts = id.Split('-');
        if (parts.Length != 3 || ScenarioName(parts[1]) is not { } scenario ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ||
            seed is < 1 or > 1000 || parts[2] != seed.ToString(CultureInfo.InvariantCulture)) return false;
        key = (scenario, seed);
        return true;
    }
}
