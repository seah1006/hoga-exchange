using System.Security.Cryptography;
using System.Text.Json;
using Hoga.Engine;

namespace Hoga.Bots.Tests;

public sealed class ExperimentTests
{
    [Fact]
    public void SplitMix64_matches_published_reference_vector()
    {
        // rand_xoshiro reference test, generated using Vigna's reference splitmix64.c:
        // https://docs.rs/rand_xoshiro/latest/src/rand_xoshiro/splitmix64.rs.html#94-106
        var stream = new SplitMix64(1477776061723855037UL);
        ulong[] expected = [1985237415132408290UL, 2979275885539914483UL,
            13511426838097143398UL, 8488337342461049707UL, 15141737807933549159UL];
        Assert.Equal(expected, expected.Select(_ => stream.Next()));
    }

    [Fact]
    public void Adding_a_bot_and_consuming_its_stream_does_not_shift_existing_streams()
    {
        const long seed = 42;
        Bot[] before = [new MarketMaker(seed), new Noise(seed), new Momentum(seed)];
        Bot[] after = [new MarketMaker(seed), new PanicSeller(seed), new Noise(seed), new Momentum(seed)];
        var pairs = before.Select(bot => (Original: bot, Added: after.Single(b => b.Name == bot.Name))).ToArray();
        Assert.NotEqual(SplitMix64.ForBot(seed, "MarketMaker").Next(), SplitMix64.ForBot(seed, "Noise").Next());
        Assert.NotEqual(SplitMix64.ForBot(seed, "Noise").Next(), SplitMix64.ForBot(seed + 1, "Noise").Next());
        for (var tick = 0; tick < 64; tick++)
        {
            _ = after[1].Stream.Next();
            foreach (var (original, added) in pairs)
                Assert.Equal(original.Stream.Next(), added.Stream.Next());
        }
    }

    [Theory]
    [InlineData(Scenario.Calm)]
    [InlineData(Scenario.FatFinger)]
    [InlineData(Scenario.PanicSell)]
    [Trait("Rule", "R-TICK-02")]
    [Trait("Rule", "R-LIMIT-04")]
    public void Paired_experiments_repeat_without_order_rejections(Scenario scenario)
    {
        var first = ExperimentRunner.RunExperiment(42, scenario);
        var second = ExperimentRunner.RunExperiment(42, scenario);
        Assert.Equal(SHA256.HashData(first.Experiment.ToJson()), SHA256.HashData(second.Experiment.ToJson()));
        Assert.Collection(first.Diagnostics, d => { Assert.True(d.ViEnabled); Assert.Empty(d.Rejections); },
            d => { Assert.False(d.ViEnabled); Assert.Empty(d.Rejections); });
        var (upper, lower, _) = PriceLimits.Compute(MarketSimulation.BasePrice, new TickSizeTable());
        var ticks = new TickSizeTable();
        foreach (var run in first.Experiment.Runs)
        {
            Assert.Equal(241, run.Series.Count);
            Assert.Equal(new(0, MarketSimulation.BasePrice), run.Series[0]);
            Assert.Equal(MarketSimulation.EndMs, run.Series[^1].TimeMs);
            for (var i = 0; i < run.Series.Count; i++)
            {
                var point = run.Series[i];
                Assert.Equal(i * 1_000L, point.TimeMs);
                Assert.InRange(point.Price, run.MinPrice, run.MaxPrice);
                Assert.True(ticks.IsOnTick(point.Price));
            }
            Assert.InRange(run.MinPrice, lower, upper);
            Assert.InRange(run.MaxPrice, lower, upper);
            if (!run.ViEnabled)
                Assert.Empty(run.ViEvents);
        }
    }

    [Fact]
    public void Json_uses_v04_field_names_and_string_event_kinds()
    {
        var experiment = ExperimentRunner.RunExperiment(42, Scenario.FatFinger).Experiment;
        using var json = JsonDocument.Parse(experiment.ToJson());
        var root = json.RootElement;
        Assert.Equal(["id", "seed", "scenario", "runs"], Keys(root));
        Assert.Equal(experiment.Id, root.GetProperty("id").GetString());
        Assert.Equal(42L, root.GetProperty("seed").GetInt64());
        Assert.Equal("FatFinger", root.GetProperty("scenario").GetString());
        var runs = root.GetProperty("runs");
        Assert.Equal(2, runs.GetArrayLength());
        Assert.True(runs[0].GetProperty("viEnabled").GetBoolean());
        Assert.False(runs[1].GetProperty("viEnabled").GetBoolean());
        Assert.Equal(["viEnabled", "series", "viEvents", "minPrice", "maxPrice", "minAtMs", "maxAtMs"], Keys(runs[0]));
        Assert.Equal(["timeMs", "price"], Keys(runs[0].GetProperty("series")[0]));
        var events = runs[0].GetProperty("viEvents").EnumerateArray().ToArray();
        var trigger = events.First();
        Assert.Equal(["type", "kind", "atMs", "referencePrice", "triggerPrice", "ruleId"], Keys(trigger));
        Assert.Equal("Triggered", trigger.GetProperty("type").GetString());
        Assert.Contains(trigger.GetProperty("kind").GetString(), new[] { "Dynamic", "Static" });
        var release = events.First(e => e.GetProperty("type").GetString() == "Released");
        Assert.Equal(trigger.GetProperty("kind").GetString(), release.GetProperty("kind").GetString());
        Assert.Equal(trigger.GetProperty("ruleId").GetString(), release.GetProperty("ruleId").GetString());
        Assert.True(release.GetProperty("auctionPrice").GetInt64() > 0);
        Assert.InRange(release.GetProperty("atMs").GetInt64() - trigger.GetProperty("atMs").GetInt64(),
            120_000L, 150_000L);
    }

    [Fact]
    public void FatFinger_only_places_a_single_sell_at_the_scheduled_tick()
    {
        var fat = new FatFinger(42);
        var view = new MarketView(60_000, 10_000, 10_000, 10_000, 0, 7_000);
        Assert.Null(fat.Requote(view with { TimeMs = 59_900 }));
        Assert.Equal(new Quote(Side.Sell, 7_020, 1_600), Assert.Single(fat.Requote(view)!));
        Assert.Null(fat.Requote(view with { TimeMs = 60_100 }));
    }

    [Fact]
    public void Panic_offers_get_lower_until_the_streak_breaks()
    {
        var panic = new PanicSeller(42);
        var view = new MarketView(60_000, 10_000, 9_900, 9_920, 2, 7_000);
        var first = Assert.Single(panic.Requote(view)!);
        var next = Assert.Single(panic.Requote(view with { TimeMs = 61_000, DownStreak = 3 })!);
        Assert.Equal(Side.Sell, first.Side);
        Assert.True(next.Price < first.Price);
        Assert.True(next.Quantity > first.Quantity);
        Assert.Empty(panic.Requote(view with { TimeMs = 62_000, DownStreak = 0 })!);
    }

    private static string[] Keys(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();
}
