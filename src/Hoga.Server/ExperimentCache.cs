using System.Globalization;
using Hoga.Bots;

namespace Hoga.Server;

internal sealed class ExperimentCache
{
    internal const int Capacity = 8; // 임의 지정
    private readonly Dictionary<(Scenario, int), Experiment> _cache = [];
    private readonly Queue<(Scenario, int)> _insertionOrder = new();

    public Experiment? Get(string id)
    {
        if (!Parse(id, out var key)) return null;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var result = ExperimentRunner.RunExperiment(key.Item2, key.Item1).Experiment;
        if (_cache.Count == Capacity) _cache.Remove(_insertionOrder.Dequeue());
        _cache.Add(key, result);
        _insertionOrder.Enqueue(key);
        return result;
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
