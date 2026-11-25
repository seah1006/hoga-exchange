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

    [Fact]
    public void GoldenJournalHasExactBytesAndFixedReplayHash()
    {
        WriteThree();
        var golden = Path.Combine(AppContext.BaseDirectory, "Golden", "basic.jsonl");
        Assert.Equal(File.ReadAllBytes(golden), File.ReadAllBytes(FilePath));

        // 저널 재생 해시는 같은 명령을 엔진에 직접 넣은 해시와 같아야 한다(교차 검증).
        var direct = new MatchingEngine(1000, new TickSizeTable());
        direct.Process(new PlaceLimitOrder("a", Side.Buy, 990, 5, "account", 1, 0));
        direct.Process(new PlaceLimitOrder("b", Side.Sell, 1010, 3, "account", 2, 1));
        direct.Process(new CancelOrder("a", 3, 2));
        var replayed = Recovery.Restore(golden).StateHash;
        Assert.Equal(direct.ComputeStateHash(), replayed);

        // 고정값: TASK-002가 엔진 상태 해시에 VI 설정·단계·참조가격·난수 상태를 추가해 형식이 바뀜
        // (병합 시 메인 통합 수정, 2026-11-25). 엔진 해시 형식이 다시 바뀌면 이 값도 이유와 함께 갱신한다.
        Assert.Equal(GoldenReplayHash, replayed);
    }

    private const string GoldenReplayHash = "c295211d29702b6bd2cb699d72c724dc18f1852492c6937756111cf118656b15";

    [Fact]
    public void CrLfJournalReplays()
    {
        WriteThree();
        var hash = Recovery.Restore(FilePath).StateHash;
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(hash, Recovery.Restore(FilePath).StateHash);
    }

    [Fact]
    public void KoreanAndSpecialOrderIdRoundTrips()
    {
        const string id = "주문-한글+<>\"\\\u2028";
        string hash;
        using (var session = new JournaledEngine(FilePath, Header))
        {
            session.Process(new PlaceLimitOrder(id, Side.Buy, 1000, 1, "계좌", 1, 0));
            hash = session.Engine.ComputeStateHash();
        }
        Assert.Equal(hash, Recovery.Restore(FilePath).StateHash);
        Assert.Equal(id, Assert.Single(JournalReader.Read(FilePath).Commands).OrderId);
    }

    [Fact]
    public void EmptyJournalCannotRecover()
    {
        File.WriteAllBytes(FilePath, []);
        Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath));
    }

    [Fact]
    public void HeaderOnlyRestoresEmptyEngine()
    {
        using (var writer = new JournalWriter(FilePath, Header)) { }
        var restored = Recovery.Restore(FilePath);
        Assert.Equal(0, restored.LastSequence);
        Assert.Empty(restored.Warnings);
        Assert.Equal(Header.CreateEngine().ComputeStateHash(), restored.StateHash);
        Assert.Equal(0, ReplayVerifier.Verify(FilePath).EventCount);
    }

    [Fact]
    public void StructuralErrorsAreNotRecordedAndDoNotPoisonSession()
    {
        using var session = new JournaledEngine(FilePath, Header);
        var before = ReadSharedBytes();
        OrderCommand[] invalid = [new PlaceLimitOrder("a", (Side)7, 1000, 1, "a", 1, 0),
            new CancelOrder(null!, 1, 0), new PlaceLimitOrder("a", Side.Buy, 1000, 1, null!, 1, 0),
            new CancelOrder("a", 2, 0)];
        foreach (var command in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => session.Process(command));
            Assert.Equal(before, ReadSharedBytes());
        }
        Assert.Throws<ArgumentNullException>(() => session.Process(null!));
        Assert.IsType<OrderRejected>(Assert.Single(session.Process(new PlaceLimitOrder("a", Side.Buy, 1000, 0, "a", 1, 0))));
        Assert.Equal(1, Recovery.Restore(FilePath).LastSequence);
    }

    [Theory]
    [InlineData("side", true)]
    [InlineData("accountId", false)]
    [InlineData("sequence", false)]
    [InlineData("price", false)]
    public void MissingOrInvalidDtoFieldsFailWithLineNumber(string field, bool invalidEnum)
    {
        WriteThree();
        Rewrite(1, node => { var payload = node["payload"]!.AsObject(); if (invalidEnum) payload[field] = "Unknown"; else payload.Remove(field); });
        Assert.Contains("line 2", Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath)).Message);
    }

    [Fact]
    public void WriteIoFailureDoesNotApplyCommandOrRetryOnDispose()
    {
        var stream = new FailingStream();
        var writer = new JournalWriter(stream, Header);
        using var session = new JournaledEngine(writer, Header);
        var before = session.Engine.ComputeStateHash();
        stream.Fail = true;
        Assert.Throws<IOException>(() => session.Process(new CancelOrder("a", 1, 0)));
        Assert.Equal(before, session.Engine.ComputeStateHash());
        Assert.Equal(0, writer.LastSequence);
        Assert.Throws<InvalidOperationException>(() => session.Process(new CancelOrder("a", 1, 0)));
        Assert.Throws<InvalidOperationException>(() => writer.Flush());
        var writes = stream.WriteAttempts;
        session.Dispose();
        Assert.Equal(writes, stream.WriteAttempts);
    }
    private sealed class FailingStream : MemoryStream
    {
        public bool Fail { get; set; }
        public int WriteAttempts { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            if (Fail) throw new IOException("Injected disk failure.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAttempts++;
            if (Fail) throw new IOException("Injected disk failure.");
            base.Write(buffer);
        }
    }

    [Fact]
    public void CustomTickRuleIsUsedForWritingAndRecovery()
    {
        var rule = new CustomRule();
        var header = JournalHeader.Create(1000, rule);
        using (var session = new JournaledEngine(FilePath, header, resolveRule: _ => rule))
            session.Process(new CancelOrder("a", 1, 0));
        Assert.Equal(1, Recovery.Restore(FilePath, resolveRule: _ => rule).LastSequence);
        Assert.True(ReplayVerifier.Verify(FilePath, resolveRule: _ => rule).IsMatch);
    }
    private sealed class CustomRule : ITickSizeRule
    {
        public string RuleId => "test-custom/v1"; // 임의 지정 테스트 규칙 ID.
        public long GetTickSize(long price) => 1;
        public bool IsOnTick(long price) => true;
        public long FloorToTick(long price) => price;
    }

    private byte[] ReadSharedBytes()
    {
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void EngineExceptionDuringRecoveryIncludesLineAndSequence()
    {
        WriteThree();
        var ex = Assert.Throws<InvalidDataException>(() => Recovery.Restore(FilePath, resolveRule: _ => new ThrowingRule()));
        Assert.Contains("line 2", ex.Message);
        Assert.Contains("seq 1", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
    private sealed class ThrowingRule : ITickSizeRule
    {
        public string RuleId => new TickSizeTable().RuleId;
        public long GetTickSize(long price) => 1;
        public bool IsOnTick(long price) => throw new InvalidOperationException("Injected rule failure.");
        public long FloorToTick(long price) => price;
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
