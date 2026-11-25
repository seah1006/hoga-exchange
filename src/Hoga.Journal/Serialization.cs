using System.Text.Json;
using Hoga.Engine;

namespace Hoga.Journal;

public sealed class UnknownCommandTypeException(string message) : Exception(message);

public interface ICommandConverter
{
    string TypeId { get; }
    Type CommandType { get; }
    JsonElement Write(OrderCommand command);
    OrderCommand Read(JsonElement payload);
}

public sealed class JsonCommandConverter<T>(string typeId) : ICommandConverter where T : OrderCommand
{
    public string TypeId { get; } = typeId;
    public Type CommandType => typeof(T);
    public JsonElement Write(OrderCommand command) => JsonSerializer.SerializeToElement((T)command);
    public OrderCommand Read(JsonElement payload) => payload.Deserialize<T>()
        ?? throw new JsonException("Command payload cannot be null.");
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandConverter> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, ICommandConverter> _types = new();

    public static CommandRegistry CreateDefault() => new CommandRegistry()
        .Register(new JsonCommandConverter<PlaceLimitOrder>("place-limit/v1"))
        .Register(new JsonCommandConverter<CancelOrder>("cancel/v1"));

    public CommandRegistry Register(ICommandConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentException.ThrowIfNullOrWhiteSpace(converter.TypeId);
        if (_ids.ContainsKey(converter.TypeId) || _types.ContainsKey(converter.CommandType))
            throw new ArgumentException("Command type or discriminator already registered.", nameof(converter));
        _ids.Add(converter.TypeId, converter);
        _types.Add(converter.CommandType, converter);
        return this;
    }

    public (string Type, JsonElement Payload) Encode(OrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_types.TryGetValue(command.GetType(), out var converter))
            throw new UnknownCommandTypeException($"Unregistered command CLR type: {command.GetType().FullName}");
        return (converter.TypeId, converter.Write(command));
    }

    public OrderCommand Decode(string type, JsonElement payload)
    {
        if (!_ids.TryGetValue(type, out var converter))
            throw new UnknownCommandTypeException($"Unknown command discriminator: {type}");
        var command = converter.Read(payload);
        if (command.GetType() != converter.CommandType)
            throw new JsonException("Converter returned a different command type.");
        return command;
    }
}

public sealed record JournalHeader(int FormatVersion, long BasePrice, string TickSizeRuleId)
{
    public const int CurrentVersion = 1;
    public static JournalHeader Create(long basePrice, ITickSizeRule rule) =>
        new(CurrentVersion, basePrice, rule.RuleId);

    public MatchingEngine CreateEngine(Func<string, ITickSizeRule>? resolveRule = null)
    {
        if (FormatVersion != CurrentVersion)
            throw new InvalidDataException($"Unsupported journal format version: {FormatVersion}");
        var rule = resolveRule is null ? new TickSizeTable() : resolveRule(TickSizeRuleId);
        if (rule is null || !StringComparer.Ordinal.Equals(rule.RuleId, TickSizeRuleId))
            throw new InvalidDataException($"Unsupported or mismatched tick rule: {TickSizeRuleId}");
        return new MatchingEngine(BasePrice, rule);
    }
}
