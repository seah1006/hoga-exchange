using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoga.Engine;

namespace Hoga.Bots;

public enum Scenario { Calm, FatFinger, PanicSell }

public sealed record PricePoint(long TimeMs, long Price);

public sealed record ExperimentViEvent(string Type, string Kind, long AtMs, long ReferencePrice,
    long TriggerPrice, string RuleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AuctionPrice = null);

public sealed record ExperimentRun(bool ViEnabled, IReadOnlyList<PricePoint> Series,
    IReadOnlyList<ExperimentViEvent> ViEvents, long MinPrice, long MaxPrice);

public sealed record Experiment(string Id, long Seed, string Scenario, IReadOnlyList<ExperimentRun> Runs)
{
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

// Diagnostics are not part of the experiment wire object. Rejections are keyed by engine reason.
public sealed record RunDiagnostics(bool ViEnabled, IReadOnlyDictionary<RejectReason, long> Rejections);
public sealed record ExperimentResult(Experiment Experiment, IReadOnlyList<RunDiagnostics> Diagnostics);

public static class ExperimentRunner
{
    public static ExperimentResult RunExperiment(long seed, Scenario scenario)
    {
        if (!Enum.IsDefined(scenario))
            throw new ArgumentOutOfRangeException(nameof(scenario));
        var (enabled, enabledDiagnostics) = new MarketSimulation(seed, scenario, viEnabled: true).Run();
        var (disabled, disabledDiagnostics) = new MarketSimulation(seed, scenario, viEnabled: false).Run();
        var id = string.Create(CultureInfo.InvariantCulture, $"exp-{scenario}-{seed}");
        return new(new(id, seed, scenario.ToString(), Array.AsReadOnly(new[] { enabled, disabled })),
            Array.AsReadOnly(new[] { enabledDiagnostics, disabledDiagnostics }));
    }
}
