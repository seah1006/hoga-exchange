using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hoga.Engine;

namespace Hoga.Journal;

internal sealed record JournalLine(long Seq, string Type, JsonElement Payload, string Checksum);
internal static class JournalFormat
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string Checksum(long seq, string type, JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{type}\n{payload.GetRawText()}")))[..16];
    internal static string Encode(long seq, string type, JsonElement payload) =>
        JsonSerializer.Serialize(new JournalLine(seq, type, payload, Checksum(seq, type, payload)), Json);
}

/// <summary>Single-owner append-only writer. Existing files are never overwritten.</summary>
public sealed class JournalWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly CommandRegistry _registry;
    private readonly int _flushEvery;
    private int _pending;
    private bool _faulted;
    public long LastSequence { get; private set; }
    public JournalWriter(string path, JournalHeader header, CommandRegistry? registry = null, int flushEvery = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(flushEvery, 1);
        _ = header.CreateEngine();
        _registry = registry ?? CommandRegistry.CreateDefault();
        _flushEvery = flushEvery;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { NewLine = "\n" };
        try
        {
            _writer.WriteLine(JournalFormat.Encode(0, "header", JsonSerializer.SerializeToElement(header, JournalFormat.Json)));
            Flush();
        }
        catch { _writer.Dispose(); throw; }
    }
    public void Append(OrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_faulted) throw new InvalidOperationException("Writer faulted; recovery required.");
        if (LastSequence == long.MaxValue || command.Sequence != LastSequence + 1)
            throw new ArgumentOutOfRangeException(nameof(command), "Journal sequence must increase by one from 1.");
        var (type, payload) = _registry.Encode(command);
        var line = JournalFormat.Encode(command.Sequence, type, payload);
        try
        {
            _writer.WriteLine(line);
            if (++_pending >= _flushEvery) Flush();
            LastSequence = command.Sequence;
        }
        catch { _faulted = true; throw; }
    }
    public void Flush()
    {
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
        _pending = 0;
    }
    public void Dispose()
    {
        try { if (!_faulted) Flush(); }
        finally { _writer.Dispose(); }
    }
}

public sealed record DiscardedLine(int LineNumber, string Reason);
public sealed record JournalContents(JournalHeader Header, IReadOnlyList<OrderCommand> Commands,
    IReadOnlyList<DiscardedLine> Warnings);
public static class JournalReader
{
    public static JournalContents Read(string path, CommandRegistry? registry = null, JournalHeader? expectedHeader = null)
    {
        registry ??= CommandRegistry.CreateDefault();
        var bytes = File.ReadAllBytes(path);
        var completeEnd = Array.LastIndexOf(bytes, (byte)'\n');
        if (completeEnd < 0) throw new InvalidDataException("Journal has no complete header.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes, 0, completeEnd + 1); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Journal contains invalid UTF-8.", ex); }
        var lines = text.Split('\n');
        JournalHeader? header = null;
        var commands = new List<OrderCommand>();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            try
            {
                var line = JsonSerializer.Deserialize<JournalLine>(lines[i], JournalFormat.Json)
                    ?? throw new InvalidDataException("Null record.");
                if (line.Seq != i) throw new InvalidDataException($"Sequence gap: expected {i}, got {line.Seq}.");
                if (string.IsNullOrWhiteSpace(line.Type) || line.Payload.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidDataException("Missing type or payload.");
                if (!StringComparer.Ordinal.Equals(line.Checksum, JournalFormat.Checksum(line.Seq, line.Type, line.Payload)))
                    throw new InvalidDataException("Checksum mismatch.");
                if (i == 0)
                {
                    if (line.Type != "header") throw new InvalidDataException("First record must be a header.");
                    header = line.Payload.Deserialize<JournalHeader>(JournalFormat.Json)
                        ?? throw new InvalidDataException("Missing header.");
                    if (expectedHeader is not null && header != expectedHeader)
                        throw new InvalidDataException("Journal header does not match expected engine configuration.");
                }
                else
                {
                    var command = registry.Decode(line.Type, line.Payload);
                    if (command.Sequence != line.Seq) throw new InvalidDataException("Envelope and command sequence differ.");
                    commands.Add(command);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            { throw new InvalidDataException($"Invalid journal line {i + 1}: {ex.Message}", ex); }
        }
        IReadOnlyList<DiscardedLine> warnings = completeEnd == bytes.Length - 1 ? [] :
            [new(lines.Length, "Unterminated final record discarded (possible interrupted write).")];
        return new(header!, commands.AsReadOnly(), warnings);
    }
}

public sealed record RecoveryResult(MatchingEngine Engine, long LastSequence, string StateHash,
    IReadOnlyList<DiscardedLine> Warnings, IReadOnlyList<EngineEvent> Events);
public static class Recovery
{
    public static RecoveryResult Restore(string journalPath, CommandRegistry? registry = null,
        JournalHeader? expectedHeader = null, Func<string, ITickSizeRule>? resolveRule = null)
    {
        var journal = JournalReader.Read(journalPath, registry, expectedHeader);
        var engine = journal.Header.CreateEngine(resolveRule);
        var events = new List<EngineEvent>();
        long seq = 0;
        foreach (var command in journal.Commands)
        {
            events.AddRange(engine.Process(command));
            seq = command.Sequence;
        }
        return new(engine, seq, engine.ComputeStateHash(), journal.Warnings, events.AsReadOnly());
    }
}
public sealed record ReplayVerification(bool IsMatch, string StateHash, int EventCount);
public static class ReplayVerifier
{
    public static ReplayVerification Verify(string journalPath, CommandRegistry? registry = null,
        Func<string, ITickSizeRule>? resolveRule = null)
    {
        var first = Recovery.Restore(journalPath, registry, resolveRule: resolveRule);
        var second = Recovery.Restore(journalPath, registry, resolveRule: resolveRule);
        return new(first.StateHash == second.StateHash && first.LastSequence == second.LastSequence &&
            first.Events.SequenceEqual(second.Events), first.StateHash, first.Events.Count);
    }
}

/// <summary>Owns a fresh engine and guarantees journal-before-engine ordering.</summary>
public sealed class JournaledEngine : IDisposable
{
    private readonly JournalWriter _writer;
    private bool _faulted;
    public MatchingEngine Engine { get; }
    public JournaledEngine(string path, JournalHeader header, CommandRegistry? registry = null, int flushEvery = 1)
    {
        Engine = header.CreateEngine();
        _writer = new JournalWriter(path, header, registry, flushEvery);
    }
    public IReadOnlyList<EngineEvent> Process(OrderCommand command)
    {
        if (_faulted) throw new InvalidOperationException("Session faulted; recovery is required.");
        _writer.Append(command);
        try { return Engine.Process(command); }
        catch { _faulted = true; throw; }
    }
    public void Dispose() => _writer.Dispose();
}
