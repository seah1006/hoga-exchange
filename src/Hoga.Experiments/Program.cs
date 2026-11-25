using System.Globalization;
using Hoga.Bots;
using Hoga.Experiments;

try
{
    if (args is ["--batch", "--out", var directory] && !string.IsNullOrWhiteSpace(directory))
    {
        BatchReport.Write(directory);
        return 0;
    }
    if (args.Length == 6)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            options.TryAdd(args[i], args[i + 1]);
        if (options.TryGetValue("--seed", out var seedText) &&
            long.TryParse(seedText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seed) &&
            options.TryGetValue("--scenario", out var scenarioText) &&
            Enum.GetNames<Scenario>().Contains(scenarioText, StringComparer.Ordinal) &&
            options.TryGetValue("--out", out var filePath) && !string.IsNullOrWhiteSpace(filePath))
        {
            var result = ExperimentRunner.RunExperiment(seed, Enum.Parse<Scenario>(scenarioText));
            File.WriteAllBytes(filePath, result.Experiment.ToJson());
            for (var i = 0; i < result.Experiment.Runs.Count; i++)
            {
                var run = result.Experiment.Runs[i];
                var diagnostics = result.Diagnostics[i];
                var rejections = string.Join(", ", diagnostics.Rejections.Select(entry => $"{entry.Key}={entry.Value}"));
                Console.WriteLine(FormattableString.Invariant(
                    $"VI={run.ViEnabled}: min={run.MinPrice}, max={run.MaxPrice}, triggers={run.ViEvents.Count(e => e.Type == "Triggered")}, rejects={diagnostics.Rejections.Values.Sum()} [{rejections}]"));
            }
            return 0;
        }
    }
    Console.Error.WriteLine("사용법: --seed <정수> --scenario <Calm|FatFinger|PanicSell> --out <JSON 경로>");
    Console.Error.WriteLine("일괄 보고: --batch --out <출력 디렉터리>");
    return 2;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
