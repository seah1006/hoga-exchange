using System.Globalization;
using System.Text;
using System.Text.Json;
using Hoga.Bots;
using Hoga.Engine;

namespace Hoga.Experiments;

internal static class BatchReport
{
    private const int SeedCount = 100; // 임의 지정: 1~100 전수.
    private const string GeneratedBy = "dotnet run --project src/Hoga.Experiments -- --batch --out docs/experiments";
    private const string DemoRule = "FatFinger Δ가 중앙값에 가장 가까운 시드, 동률이면 작은 시드";
    private const string Caveat = "임의 지정한 봇·호가 깊이·충격 크기·관측 구간에 한정된 결과다.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        WriteIndented = true,
    };

    public static void Write(string directory)
    {
        var report = new List<string>
        {
            "# TASK-005 비교 실험 요약", "",
            $"- `{GeneratedBy}`로 재생성한다.",
            "- Calm·FatFinger·PanicSell 각각 시드 1~100(임의 지정)을 빠짐없이 실행했다. 총 300쌍, 600 run이다.",
            "- Δ(원) = VI 켬 최저가 − VI 끔 최저가. 음수는 **이 지표에서만** VI 켬이 더 나빴다는 뜻이다.",
            "- 최저·최고가는 시작 기준가와 100ms 틱 사이의 모든 체결을 포함한다. 그래프는 매초 틱 처리 후의 마지막 체결가를 유지해 기록하므로 극값을 놓칠 수 있다.",
            "- 두 run의 기준가, 봇·적정가치 스트림과 논리 시간은 같고 VI 옵션만 바꾼다. 실제 주문은 달라진 체결가·재고에 봇이 반응하므로 VI 이후 갈라질 수 있다.",
            "- 종료 시각을 양쪽 모두 240초(임의 지정)로 고정했다. 종료 시 진행 중인 VI는 표시하되 이후 체결은 포함하지 않는다.", "",
            "## 최저가 차이 분포", "",
            "25·75 분위는 최근접 순위법, 중앙값은 가운데 두 값의 평균이다. 0.5원 단위 보존을 위해 두 배 정수로 계산한다.", "",
            "| 시나리오 | 최소 | 25 분위 | 중앙값 | 75 분위 | 최대 | VI 켬 더 나쁨 | 동률 | VI 켬 더 높음 |",
            "|---|---:|---:|---:|---:|---:|---:|---:|---:|",
        };
        var histogram = new List<string>();
        var reliability = new List<string>();
        var scenarios = new List<ScenarioSummary>();
        Sample? demo = null;
        foreach (var scenario in new[] { Scenario.Calm, Scenario.FatFinger, Scenario.PanicSell })
        {
            var samples = new List<Sample>(SeedCount);
            for (var seed = 1; seed <= SeedCount; seed++)
            {
                var result = ExperimentRunner.RunExperiment(seed, scenario);
                var (viOn, viOff) = (result.Experiment.Runs[0], result.Experiment.Runs[1]);
                samples.Add(new(result, new(seed, viOn.MinPrice, viOff.MinPrice, viOn.MinPrice - viOff.MinPrice,
                    Triggers(viOn))));
            }
            var sorted = samples.Select(sample => sample.Seed.Delta).Order().ToArray();
            var medianTwice = sorted[SeedCount / 2 - 1] + sorted[SeedCount / 2];
            var quantiles = new DeltaQuantiles(sorted[0], sorted[24], medianTwice / 2m, sorted[74], sorted[^1]);
            var counts = new DeltaCounts(sorted.Count(d => d < 0), sorted.Count(d => d == 0), sorted.Count(d => d > 0));
            var on = GetRunTotals(samples, viEnabled: true);
            var off = GetRunTotals(samples, viEnabled: false);
            scenarios.Add(new(scenario.ToString(), quantiles, counts, new(on.EndingInVi, off.EndingInVi),
                samples.Select(sample => sample.Seed).ToArray()));

            report.Add(Invariant($"| {scenario} | {quantiles.Min} | {quantiles.P25} | {quantiles.Median:0.#} | {quantiles.P75} | {quantiles.Max} | {counts.ViWorse} | {counts.Tie} | {counts.ViBetter} |"));
            histogram.Add(Invariant($"| {scenario} | {sorted.Count(d => d < -500)} | {sorted.Count(d => d is >= -500 and <= -101)} | {sorted.Count(d => d is >= -100 and <= -1)} | {counts.Tie} | {sorted.Count(d => d is >= 1 and <= 100)} | {sorted.Count(d => d is >= 101 and <= 500)} | {sorted.Count(d => d > 500)} |"));
            reliability.Add(FormatReliability(scenario, "켬", on));
            reliability.Add(FormatReliability(scenario, "끔", off));
            if (scenario == Scenario.FatFinger)
                demo = samples.OrderBy(sample => Math.Abs(2 * sample.Seed.Delta - medianTwice))
                    .ThenBy(sample => sample.Seed.Seed).First();
        }
        report.AddRange(["", "### 구간별 빈도 (경계는 임의 지정)", "",
            "| 시나리오 | ≤−501 | −500~−101 | −100~−1 | 0 | 1~100 | 101~500 | ≥501 |",
            "|---|---:|---:|---:|---:|---:|---:|---:|"]);
        report.AddRange(histogram);
        report.AddRange(["", "## 엔진 거부 및 VI 집계", "",
            "| 시나리오 | VI 옵션 | VI 발동 합계 | 240초에 VI 중인 run 수 | 거부 합계 | 거부 사유별 |",
            "|---|---|---:|---:|---:|---|"]);
        report.AddRange(reliability);
        var selected = demo ?? throw new InvalidOperationException("FatFinger sample missing.");
        var experiment = selected.Result.Experiment;
        var file = Invariant($"demo-fatfinger-{experiment.Seed}.json");
        var demoOn = experiment.Runs[0];
        var demoOff = experiment.Runs[1];
        report.AddRange(["", "## 시연 시드", "",
            "실행 전에 고정한 규칙(임의 지정): FatFinger의 Δ가 100개 표본 중앙값에 가장 가까운 시드를 쓴다. 동률이면 가장 작은 시드를 쓴다.",
            Invariant($"[{file}]({file}): 시드 **{experiment.Seed}**, Δ **{selected.Seed.Delta}원**, 최저가 켬 {selected.Seed.MinOn}원·끔 {selected.Seed.MinOff}원, VI 발동 {selected.Seed.ViTriggers}회·{Triggers(demoOff)}회."), "",
            "이 결과는 임의 지정한 봇·호가 깊이·충격 크기·관측 구간에 한정된다. VI의 실제 시장 효과나 다른 입력에서의 우위를 입증하지 않는다. 최저가 차이에는 VI 중 체결을 기다리는 시간이 포함되므로 거래량·체결 지연과도 구분해서 읽어야 한다."]);
        var summary = new Summary(GeneratedBy, new(1, SeedCount), demoOn.Series[^1].TimeMs,
            new(experiment.Scenario, experiment.Seed, DemoRule), scenarios, Caveat);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, file), experiment.ToJson());
        File.WriteAllText(Path.Combine(directory, "SUMMARY.md"), string.Join('\n', report) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions) + "\n");
        Console.WriteLine(Invariant($"300쌍 실행 완료. 시연 파일: {file}"));
    }

    private static RunTotals GetRunTotals(List<Sample> samples, bool viEnabled)
    {
        var byReason = new SortedDictionary<RejectReason, long>();
        foreach (var (reason, count) in samples.SelectMany(sample => sample.Result.Diagnostics
                     .Single(d => d.ViEnabled == viEnabled).Rejections))
            byReason[reason] = byReason.GetValueOrDefault(reason) + count;
        var open = samples.Count(sample => sample.Result.Experiment.Runs.Single(r => r.ViEnabled == viEnabled)
            .ViEvents.LastOrDefault()?.Type == "Triggered");
        var triggers = viEnabled ? samples.Sum(sample => sample.Seed.ViTriggers)
            : samples.Sum(sample => Triggers(sample.Result.Experiment.Runs.Single(run => !run.ViEnabled)));
        return new(triggers, open, byReason);
    }

    private static string FormatReliability(Scenario scenario, string option, RunTotals totals)
    {
        var byReason = totals.Rejections;
        var reasons = byReason.Count == 0 ? "없음" : string.Join(", ", byReason.Select(p => Invariant($"{p.Key}: {p.Value}")));
        return Invariant($"| {scenario} | {option} | {totals.Triggers} | {totals.EndingInVi} | {byReason.Values.Sum()} | {reasons} |");
    }

    private static int Triggers(ExperimentRun run) => run.ViEvents.Count(e => e.Type == "Triggered");
    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record Sample(ExperimentResult Result, SeedSummary Seed);
    private sealed record RunTotals(int Triggers, int EndingInVi, IReadOnlyDictionary<RejectReason, long> Rejections);
    private sealed record Summary(string GeneratedBy, SeedRange Seeds, long DurationMs, Demo Demo,
        IReadOnlyList<ScenarioSummary> Scenarios, string Caveat);
    private sealed record SeedRange(int From, int To);
    private sealed record Demo(string Scenario, long Seed, string Rule);
    private sealed record ScenarioSummary(string Scenario, DeltaQuantiles DeltaQuantiles, DeltaCounts Counts,
        RunsEndingInVi RunsEndingInVi, IReadOnlyList<SeedSummary> Seeds);
    private sealed record DeltaQuantiles(long Min, long P25, decimal Median, long P75, long Max);
    private sealed record DeltaCounts(int ViWorse, int Tie, int ViBetter);
    private sealed record RunsEndingInVi(int ViOn, int ViOff);
    private sealed record SeedSummary(int Seed, long MinOn, long MinOff, long Delta, int ViTriggers);
}
