using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hoga.Engine;

namespace Hoga.Journal;

internal sealed record JournalLine(long Seq, string Type, JsonElement Payload, string Checksum);
internal static class JournalFormat
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    internal static string Checksum(long seq, string type, JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{type}\n{payload.GetRawText()}")))[..16];
    internal static string Encode(long seq, string type, JsonElement payload) =>
        JsonSerializer.Serialize(new JournalLine(seq, type, payload, Checksum(seq, type, payload)), Json);
}

/// <summary>Single-owner append-only writer. Construction never overwrites existing files.</summary>
public sealed class JournalWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly CommandRegistry _registry;
    private readonly int _flushEvery;
    private int _pending;
    private bool _faulted;
    public long LastSequence { get; private set; }
    public JournalWriter(string path, JournalHeader header, CommandRegistry? registry = null, int flushEvery = 1,
        Func<string, ITickSizeRule>? resolveRule = null)
        : this(CreateStream(path, header, flushEvery, resolveRule), header, registry, flushEvery, resolveRule) { }
    private static FileStream CreateStream(string path, JournalHeader header, int flushEvery,
        Func<string, ITickSizeRule>? resolveRule)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(flushEvery, 1);
        _ = header.CreateEngine(resolveRule);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    internal JournalWriter(Stream stream, JournalHeader header, CommandRegistry? registry = null, int flushEvery = 1,
        Func<string, ITickSizeRule>? resolveRule = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(flushEvery, 1);
        _ = header.CreateEngine(resolveRule);
        _registry = registry ?? CommandRegistry.CreateDefault();
        _flushEvery = flushEvery;
        _stream = stream;
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        try
        {
            _writer.WriteLine(JournalFormat.Encode(0, "header", JsonSerializer.SerializeToElement(header, JournalFormat.Json)));
            Flush();
        }
        catch { _stream.Dispose(); throw; }
    }
    private JournalWriter(FileStream stream, CommandRegistry? registry, long lastSequence)
    {
        _stream = stream;
        _registry = registry ?? CommandRegistry.CreateDefault();
        _flushEvery = 1;
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        LastSequence = lastSequence;
    }

    /// <summary>Validates an existing journal and resumes after the last complete record.</summary>
    public static JournalWriter OpenExisting(string path, JournalHeader expectedHeader, CommandRegistry? registry = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var journal = JournalReader.Read(path, registry, expectedHeader);
            long seq = 0;
            foreach (var command in journal.Commands) seq = command.Sequence;
            if (journal.Warnings.Count != 0)
            {
                // Only a non-LF-terminated tail is discardable. Search backward in bounded blocks.
                Span<byte> block = stackalloc byte[4096];
                for (long end = stream.Length; end > 0;)
                {
                    var count = (int)Math.Min(end, block.Length);
                    end -= count;
                    stream.Position = end;
                    stream.ReadExactly(block[..count]);
                    var lf = block[..count].LastIndexOf((byte)'\n');
                    if (lf < 0) continue;
                    stream.SetLength(end + lf + 1);
                    stream.Flush(flushToDisk: true);
                    break;
                }
            }
            stream.Seek(0, SeekOrigin.End);
            return new(stream, registry, seq);
        }
        catch { stream.Dispose(); throw; }
    }

    public void Append(OrderCommand command)
    {
        CommandStructure.Validate(command);
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
        if (_faulted) throw new InvalidOperationException("Writer faulted; recovery required.");
        try
        {
            _writer.Flush();
            if (_stream is FileStream file) file.Flush(flushToDisk: true);
            else _stream.Flush();
            _pending = 0;
        }
        catch { _faulted = true; throw; }
    }
    public void Dispose()
    {
        try { if (!_faulted) Flush(); }
        finally
        {
            // Never let StreamWriter.Dispose flush buffered failed commands.
            _stream.Dispose();
        }
    }
}

public sealed record DiscardedLine(int LineNumber, string Reason);
public sealed record JournalContents(JournalHeader Header, IEnumerable<OrderCommand> Commands,
    IReadOnlyList<DiscardedLine> Warnings);
public static class JournalReader
{
    // Buffer only one line; do not decode an unterminated UTF-8 tail.
    private static IEnumerable<(int Number, string? Text)> Lines(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var line = new MemoryStream();
        var utf8 = new UTF8Encoding(false, true);
        var number = 1;
        int value;
        while ((value = input.ReadByte()) != -1)
        {
            if (value != '\n') { line.WriteByte((byte)value); continue; }
            string text;
            try { text = utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r'); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException($"Invalid UTF-8 at journal line {number}.", ex); }
            yield return (number++, text);
            line.SetLength(0);
        }
        if (line.Length > 0) yield return (number, null);
    }
    private static JournalLine Parse(int number, string text)
    {
        try
        {
            var line = JsonSerializer.Deserialize<JournalLine>(text, JournalFormat.Json)
                ?? throw new InvalidDataException("Null record.");
            if (line.Seq != number - 1L) throw new InvalidDataException($"Sequence gap: expected {number - 1L}, got {line.Seq}.");
            if (string.IsNullOrWhiteSpace(line.Type) || line.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new InvalidDataException("Missing type or payload.");
            if (!StringComparer.Ordinal.Equals(line.Checksum, JournalFormat.Checksum(line.Seq, line.Type, line.Payload)))
                throw new InvalidDataException("Checksum mismatch.");
            return line;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException($"Invalid journal line {number}: {ex.Message}", ex); }
    }
    public static JournalContents Read(string path, CommandRegistry? registry = null, JournalHeader? expectedHeader = null)
    {
        registry ??= CommandRegistry.CreateDefault();
        var first = Lines(path).FirstOrDefault();
        if (first.Text is null) throw new InvalidDataException("Journal has no complete header.");
        var line = Parse(1, first.Text);
        if (line.Type != "header") throw new InvalidDataException("Journal line 1 must be a header.");
        JournalHeader header;
        try { header = line.Payload.Deserialize<JournalHeader>(JournalFormat.Json) ?? throw new JsonException("Null header."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid journal line 1 header.", ex); }
        if (expectedHeader is not null && header != expectedHeader)
            throw new InvalidDataException("Journal header does not match expected engine configuration.");
        var warnings = new List<DiscardedLine>();
        IEnumerable<OrderCommand> Commands()
        {
            warnings.Clear();
            foreach (var (number, text) in Lines(path).Skip(1))
            {
                if (text is null) { warnings.Add(new(number, "Unterminated final record discarded (possible interrupted write).")); yield break; }
                var record = Parse(number, text);
                OrderCommand command;
                try
                {
                    command = registry.Decode(record.Type, record.Payload);
                    if (command.Sequence != record.Seq) throw new InvalidDataException("Envelope and command sequence differ.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or UnknownCommandTypeException)
                { throw new InvalidDataException($"Invalid journal line {number}, seq {record.Seq}: {ex.Message}", ex); }
                yield return command;
            }
        }
        return new(header, Commands(), warnings.AsReadOnly());
    }
}

public sealed record RecoveryResult(MatchingEngine Engine, long LastSequence, string StateHash,
    IReadOnlyList<DiscardedLine> Warnings);
public static class Recovery
{
    internal static IReadOnlyList<EngineEvent> Apply(MatchingEngine engine, OrderCommand command)
    {
        try { return engine.Process(command); }
        catch (Exception ex)
        { throw new InvalidDataException($"Engine replay failed at journal line {command.Sequence + 1}, seq {command.Sequence}: {ex.Message}", ex); }
    }
    public static RecoveryResult Restore(string journalPath, CommandRegistry? registry = null,
        JournalHeader? expectedHeader = null, Func<string, ITickSizeRule>? resolveRule = null,
        Action<OrderCommand, IReadOnlyList<EngineEvent>>? observe = null)
    {
        var journal = JournalReader.Read(journalPath, registry, expectedHeader);
        var engine = journal.Header.CreateEngine(resolveRule);
        long seq = 0;
        foreach (var command in journal.Commands)
        {
            var events = Apply(engine, command);
            observe?.Invoke(command, events);
            seq = command.Sequence;
        }
        return new(engine, seq, engine.ComputeStateHash(), journal.Warnings);
    }
}
public sealed record ReplayVerification(bool IsMatch, string StateHash, int EventCount);
public static class ReplayVerifier
{
    public static ReplayVerification Verify(string journalPath, CommandRegistry? registry = null,
        Func<string, ITickSizeRule>? resolveRule = null)
    {
        var first = JournalReader.Read(journalPath, registry);
        var second = JournalReader.Read(journalPath, registry);
        var engine1 = first.Header.CreateEngine(resolveRule);
        var engine2 = second.Header.CreateEngine(resolveRule);
        using var commands1 = first.Commands.GetEnumerator();
        using var commands2 = second.Commands.GetEnumerator();
        var match = first.Header == second.Header;
        var count = 0;
        while (commands1.MoveNext())
        {
            if (!commands2.MoveNext()) return new(false, engine1.ComputeStateHash(), count);
            var events1 = Recovery.Apply(engine1, commands1.Current);
            var events2 = Recovery.Apply(engine2, commands2.Current);
            match &= commands1.Current.Sequence == commands2.Current.Sequence && events1.SequenceEqual(events2);
            count += events1.Count;
        }
        match &= !commands2.MoveNext();
        var hash = engine1.ComputeStateHash();
        return new(match && hash == engine2.ComputeStateHash(), hash, count);
    }
}

/// <summary>Owns a fresh engine and guarantees journal-before-engine ordering.</summary>
public sealed class JournaledEngine : IDisposable
{
    private readonly JournalWriter _writer;
    private bool _faulted;
    public MatchingEngine Engine { get; }
    public JournaledEngine(string path, JournalHeader header, CommandRegistry? registry = null, int flushEvery = 1,
        Func<string, ITickSizeRule>? resolveRule = null)
    {
        Engine = header.CreateEngine(resolveRule);
        _writer = new JournalWriter(path, header, registry, flushEvery, resolveRule);
    }
    internal JournaledEngine(JournalWriter writer, JournalHeader header)
    {
        Engine = header.CreateEngine();
        _writer = writer;
    }
    public IReadOnlyList<EngineEvent> Process(OrderCommand command)
    {
        if (_faulted) throw new InvalidOperationException("Session faulted; recovery is required.");
        try { _writer.Append(command); }
        catch (IOException) { _faulted = true; throw; }
        try { return Engine.Process(command); }
        catch { _faulted = true; throw; }
    }
    public void Dispose() => _writer.Dispose();
}
