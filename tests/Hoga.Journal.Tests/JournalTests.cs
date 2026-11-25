using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FsCheck.Xunit;
using Hoga.Engine;
using Hoga.Journal;

namespace Hoga.Journal.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hoga-journal-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "journal.jsonl");
    private static JournalHeader Header => JournalHeader.Create(1000, new TickSizeTable());
    public JournalTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteThree(int flushEvery = 1)
    {
        using var session = new JournaledEngine(FilePath, Header, flushEvery: flushEvery);
        session.Process(new PlaceLimitOrder("a", Side.Buy, 990, 5, "account", 1, 0));
        session.Process(new PlaceLimitOrder("b", Side.Sell, 1010, 3, "account", 2, 1));
        var beforeLast = session.Engine.ComputeStateHash();
        session.Process(new CancelOrder("a", 3, 2));
        return beforeLast;
    }

    [Fact]
    public void TenThousandSeededCommandsReplayWithIdenticalEventsAndHash()
    {
        var random = new Random(31003); // 임의 지정 재현 시드, 명령 수 10,000은 지시서 기준.
        var events = new List<EngineEvent>();
        string originalHash;
        using (var session = new JournaledEngine(FilePath, Header, flushEvery: 100))
        {
            for (var i = 1; i <= 10_000; i++)
            {
                OrderCommand command = random.Next(4) == 0
                    ? new CancelOrder("order-" + random.Next(1, i + 1), i, i * 10L)
                    : new PlaceLimitOrder("order-" + i, (Side)random.Next(2), random.Next(680, 1320),
                        random.Next(-1, 50), "account", i, i * 10L);
                events.AddRange(session.Process(command));
            }
            originalHash = session.Engine.ComputeStateHash();
        }
        var restored = Recovery.Restore(FilePath);
        Assert.Equal(originalHash, restored.StateHash);
        Assert.Equal(10_000, restored.LastSequence);
        // Restore deliberately retains no historical events. Verifier compares events incrementally.
        Assert.Empty(restored.Warnings);
        var verification = ReplayVerifier.Verify(FilePath);
        Assert.True(verification.IsMatch);
        Assert.Equal(events.Count, verification.EventCount);
        Assert.Equal(originalHash, verification.StateHash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(100)]
    public void TruncatedLastLineDiscardsOnlyThatCommand(int removedBytes)
    {
        var beforeLast = WriteThree();
        var bytes = File.ReadAllBytes(FilePath);
        File.WriteAllBytes(FilePath, bytes[..^removedBytes]);
        var restored = Recovery.Restore(FilePath);
        Assert.Equal(2, restored.LastSequence);
        Assert.Equal(beforeLast, restored.StateHash);
        Assert.Equal(4, Assert.Single(restored.Warnings).LineNumber);
    }

    [Fact]
    public void MiddleChecksumDamageThrows()
    {
        WriteThree();
        var lines = File.ReadAllLines(FilePath);
        lines[1] = lines[1].Replace("990", "991", StringComparison.Ordinal);
        File.WriteAllText(FilePath, string.Join('\n', lines) + "\n");
        var exception = Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
        Assert.Contains("Checksum", exception.Message);
        Assert.Contains("line 2", exception.Message);
    }

    [Fact]
    public void MissingSequenceThrowsEvenWithValidChecksums()
    {
        WriteThree();
        var lines = File.ReadAllLines(FilePath);
        File.WriteAllText(FilePath, lines[0] + "\n" + lines[1] + "\n" + lines[3] + "\n");
        Assert.Contains("Sequence gap", Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath)).Message);
    }

    [Fact]
    public void HeaderMismatchThrows()
    {
        WriteThree();
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath, expectedHeader: Header with { BasePrice = 2000 }));
    }

    [Fact]
    public void CompletedLastLineCorruptionIsNotSilentlyDiscarded()
    {
        WriteThree();
        var lines = File.ReadAllLines(FilePath);
        lines[3] = "{broken-json}";
        File.WriteAllText(FilePath, string.Join('\n', lines) + "\n");
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
    }

    [Fact]
    public void PartialHeaderCannotBeRecovered()
    {
        File.WriteAllText(FilePath, "{\"seq\":0");
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
    }

    [Fact]
    public void UnknownCommandInJournalIsExplicit()
    {
        WriteThree();
        Rewrite(1, node => node["type"] = "future/v1");
        var ex = Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
        Assert.Contains("line 2", ex.Message);
        Assert.IsType<UnknownCommandTypeException>(ex.InnerException);
    }

    [Fact]
    public void PayloadSequenceMismatchThrowsAfterValidChecksum()
    {
        WriteThree();
        Rewrite(1, node => node["payload"]!["sequence"] = 50);
        Assert.Contains("Envelope", Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath)).Message);
    }

    [Fact]
    public void UnsupportedHeaderVersionThrowsAfterValidChecksum()
    {
        WriteThree();
        Rewrite(0, node => node["payload"]!["formatVersion"] = 999);
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
    }

    [Fact]
    public void HeaderChecksumDamageThrows()
    {
        WriteThree();
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("basePrice\":1000", "basePrice\":2000", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
    }

    [Fact]
    public void DefaultFlushMakesRecordReadableBeforeDisposeAndNeverOverwrites()
    {
        using var writer = new JournalWriter(FilePath, Header);
        writer.Append(new CancelOrder("missing", 1, 0));
        Assert.Equal(1, Recovery.Restore(FilePath).LastSequence);
        Assert.Throws<IOException>(() => new JournalWriter(FilePath, Header));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append(new CancelOrder("missing", 3, 0)));
        writer.Append(new CancelOrder("missing", 2, 0));
        Assert.Equal(2, Recovery.Restore(FilePath).LastSequence);
    }

    [Fact]
    public void BatchedFlushDrainsOnDispose()
    {
        WriteThree(flushEvery: 100);
        Assert.Equal(3, Recovery.Restore(FilePath).LastSequence);
    }

    [Property(MaxTest = 25)]
    public bool SeededCommandStreamsReplay(int seed)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        var random = new Random(seed);
        string hash;
        using (var session = new JournaledEngine(path, Header, flushEvery: 50))
        {
            for (var i = 1; i <= 100; i++)
                session.Process(new PlaceLimitOrder(i.ToString(), (Side)random.Next(2), random.Next(700, 1301), random.Next(1, 10), "a", i, i));
            hash = session.Engine.ComputeStateHash();
        }
        return hash == Recovery.Restore(path).StateHash;
    }

    private void Rewrite(int index, Action<JsonNode> modify)
    {
        var lines = File.ReadAllLines(FilePath);
        var node = JsonNode.Parse(lines[index])!;
        modify(node);
        var payload = node["payload"]!.ToJsonString();
        var canonical = node["seq"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + node["type"]!.GetValue<string>() + "\n" + payload;
        node["checksum"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
        lines[index] = node.ToJsonString();
        File.WriteAllText(FilePath, string.Join('\n', lines) + "\n");
    }
}
