using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hoga.Bots.Tests;

public sealed class ExperimentRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Demo_extrema_use_the_first_trade_at_each_price_not_the_sampled_close(bool viEnabled)
    {
        var trades = new List<PricePoint> { new(0, MarketSimulation.BasePrice) };
        var (run, _) = new MarketSimulation(16, Scenario.FatFinger, viEnabled)
            .Run((trade, atMs) => trades.Add(new(atMs, trade.Price)));

        Assert.Equal(trades.Min(p => p.Price), run.MinPrice);
        Assert.Equal(trades.Max(p => p.Price), run.MaxPrice);
        Assert.Equal(trades.First(p => p.Price == run.MinPrice).TimeMs, run.MinAtMs);
        Assert.Equal(trades.First(p => p.Price == run.MaxPrice).TimeMs, run.MaxAtMs);
        Assert.True(run.MinPrice < run.Series.Min(p => p.Price));
        if (viEnabled)
        {
            var release = Assert.Single(run.ViEvents, e => e.Type == "Released");
            Assert.Equal(run.MinPrice, release.AuctionPrice);
            Assert.Equal(release.AtMs, run.MinAtMs);
            Assert.NotEqual(0, run.MinAtMs % MarketSimulation.TickMs);
        }
    }

    [Theory]
    [InlineData(17L)]
    [InlineData(36L)]
    public void Opening_reference_keeps_legacy_price_but_prefers_the_first_actual_trade(long seed)
    {
        var tradesAtBase = new List<long>();
        var (run, _) = new MarketSimulation(seed, Scenario.Calm, viEnabled: false).Run((trade, atMs) =>
        {
            if (trade.Price == MarketSimulation.BasePrice) tradesAtBase.Add(atMs);
        });
        var firstAt = tradesAtBase.FirstOrDefault();
        if (seed == 17)
        {
            Assert.Equal(MarketSimulation.BasePrice, run.MaxPrice);
            Assert.Equal(firstAt, run.MaxAtMs);
        }
        else
        {
            Assert.Equal(MarketSimulation.BasePrice, run.MinPrice);
            Assert.Equal(firstAt, run.MinAtMs);
        }
    }

    [Fact]
    public void Legacy_result_hash_is_unchanged_for_all_300_experiments()
    {
        // SHA-256 from main 0aed3ee, concatenating original JSON in scenario and seed order.
        const string expected = "2E2A0841F1EC08D3351C9C4CD11F851130946F30615943F6F1C8D9D123A5E691";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var scenario in new[] { Scenario.Calm, Scenario.FatFinger, Scenario.PanicSell })
            for (var seed = 1; seed <= 100; seed++)
            {
                var experiment = ExperimentRunner.RunExperiment(seed, scenario).Experiment;
                hash.AppendData(LegacyJson(experiment));
            }
        Assert.Equal(expected, Convert.ToHexString(hash.GetHashAndReset()));
    }

    [Fact]
    public void Generated_summary_matches_the_markdown_tables()
    {
        var docs = Path.Combine(AppContext.BaseDirectory, "ExperimentDocs");
        var lines = File.ReadAllLines(Path.Combine(docs, "SUMMARY.md"));
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(docs, "summary.json")));
        var root = json.RootElement;
        Assert.Equal(240_000, root.GetProperty("durationMs").GetInt32());
        Assert.Equal("FatFinger", root.GetProperty("demo").GetProperty("scenario").GetString());
        Assert.Equal(16, root.GetProperty("demo").GetProperty("seed").GetInt32());
        var range = root.GetProperty("seeds");
        Assert.Equal(1, range.GetProperty("from").GetInt32());
        Assert.Equal(100, range.GetProperty("to").GetInt32());
        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Calm", "FatFinger", "PanicSell" }, scenarios.Select(s => s.GetProperty("scenario").GetString()));
        foreach (var scenario in scenarios)
        {
            var name = scenario.GetProperty("scenario").GetString();
            var quantiles = scenario.GetProperty("deltaQuantiles");
            var counts = scenario.GetProperty("counts");
            var end = scenario.GetProperty("runsEndingInVi");
            Assert.Contains(FormattableString.Invariant(
                $"| {name} | {Number(quantiles, "min")} | {Number(quantiles, "p25")} | {Number(quantiles, "median"):0.#} | {Number(quantiles, "p75")} | {Number(quantiles, "max")} | {Number(counts, "viWorse")} | {Number(counts, "tie")} | {Number(counts, "viBetter")} |"), lines);
            var seeds = scenario.GetProperty("seeds").EnumerateArray().ToArray();
            Assert.Equal(Enumerable.Range(1, 100), seeds.Select(s => s.GetProperty("seed").GetInt32()));
            Assert.All(seeds, s => Assert.Equal(Number(s, "minOn") - Number(s, "minOff"), Number(s, "delta")));
            var triggersOn = seeds.Sum(s => s.GetProperty("viTriggers").GetInt32());
            Assert.Equal(triggersOn.ToString(CultureInfo.InvariantCulture), Reliability("켬")[2]);
            Assert.Equal(Number(end, "viOn").ToString(CultureInfo.InvariantCulture), Reliability("켬")[3]);
            Assert.Equal(Number(end, "viOff").ToString(CultureInfo.InvariantCulture), Reliability("끔")[3]);

            string[] Reliability(string option) => Assert.Single(lines, l => l.StartsWith($"| {name} | {option} |", StringComparison.Ordinal))
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static decimal Number(JsonElement obj, string name) => obj.GetProperty(name).GetDecimal();

    private static byte[] LegacyJson(Experiment experiment)
    {
        using var document = JsonDocument.Parse(experiment.ToJson());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "runs")
                {
                    property.WriteTo(writer);
                    continue;
                }
                writer.WriteStartArray(property.Name);
                foreach (var run in property.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (var runProperty in run.EnumerateObject())
                        if (runProperty.Name is not ("minAtMs" or "maxAtMs"))
                            runProperty.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
