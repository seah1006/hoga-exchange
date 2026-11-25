using System.Text;
using Hoga.Engine;
using Hoga.Journal;

namespace Hoga.Journal.Tests;

public sealed class ResumeTests
{
    [Fact]
    public void ResumesAfterDiscardingOnlyUnterminatedTail()
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"resume-{Guid.NewGuid():N}.jsonl");
        var header = JournalHeader.Create(10_000, new TickSizeTable());
        try
        {
            using (var writer = new JournalWriter(file, header))
                writer.Append(new PlaceLimitOrder("o-1", Side.Buy, 10_000, 5, "test", 1, 25));
            File.AppendAllText(file, new string('x', 9000), new UTF8Encoding(false));
            using (var writer = JournalWriter.OpenExisting(file, header))
            {
                Assert.Equal(1, writer.LastSequence);
                Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append(new AdvanceTime(3, 40)));
                writer.Append(new CancelOrder("o-1", 2, 30));
            }
            var contents = JournalReader.Read(file);
            Assert.Collection(contents.Commands, x => Assert.IsType<PlaceLimitOrder>(x), x => Assert.IsType<CancelOrder>(x));
            Assert.Empty(contents.Warnings);
            using (var resumed = JournalWriter.OpenExisting(file, header)) Assert.Equal(2, resumed.LastSequence);
            File.AppendAllText(file, "damaged\n", new UTF8Encoding(false));
            var bytes = File.ReadAllBytes(file);
            Assert.Throws<InvalidDataException>(() => JournalWriter.OpenExisting(file, header));
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void AdvanceTimeRoundTripsAndEndsAuctionOnRecovery()
    {
        var file = Path.Combine(AppContext.BaseDirectory, $"advance-{Guid.NewGuid():N}.jsonl");
        var header = JournalHeader.Create(10_000, new TickSizeTable());
        OrderCommand[] commands = [new PlaceLimitOrder("ask", Side.Sell, 10_600, 2, "test", 1, 0),
            new PlaceLimitOrder("bid", Side.Buy, 10_600, 2, "test", 2, 1), new AdvanceTime(3, 150_001)];
        try
        {
            using (var writer = new JournalWriter(file, header))
                foreach (var command in commands) writer.Append(command);
            Assert.Contains("\"type\":\"advance-time/v1\",\"payload\":{\"sequence\":3,\"logicalTimeMs\":150001}", File.ReadAllText(file));
            Assert.Equal(commands, JournalReader.Read(file).Commands);
            var released = false;
            var recovery = Recovery.Restore(file, observe: (_, events) => released |= events.OfType<ViReleased>().Any());
            Assert.True(released);
            Assert.Equal(TradingPhase.Continuous, recovery.Engine.Phase);
            Assert.Equal(10_600, recovery.Engine.DynamicReferencePrice);
            Assert.True(ReplayVerifier.Verify(file).IsMatch);
        }
        finally { File.Delete(file); }
    }
}
