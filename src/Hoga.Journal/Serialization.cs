using System.Text.Json;
using System.Text.Json.Serialization;
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

// Wire DTOs are independent of engine records. All fields are mandatory, including zero-valued fields.
internal sealed record PlaceDto(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("price")] long Price,
    [property: JsonPropertyName("quantity")] long Quantity,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("logicalTimeMs")] long LogicalTimeMs);
internal sealed record CancelDto(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("logicalTimeMs")] long LogicalTimeMs);

internal sealed class PlaceConverter : ICommandConverter
{
    public string TypeId => "place-limit/v1";
    public Type CommandType => typeof(PlaceLimitOrder);
    public JsonElement Write(OrderCommand command)
    {
        var c = (PlaceLimitOrder)command;
        return JsonSerializer.SerializeToElement(new PlaceDto(c.OrderId, c.Side.ToString(), c.Price,
            c.Quantity, c.AccountId, c.Sequence, c.LogicalTimeMs), JournalFormat.Json);
    }
    public OrderCommand Read(JsonElement payload)
    {
        var c = payload.Deserialize<PlaceDto>(JournalFormat.Json) ?? throw new JsonException("Null command.");
        var side = c.Side switch { "Buy" => Side.Buy, "Sell" => Side.Sell, _ => throw new JsonException("Unknown side.") };
        var command = new PlaceLimitOrder(c.OrderId, side, c.Price, c.Quantity, c.AccountId, c.Sequence, c.LogicalTimeMs);
        CommandStructure.Validate(command);
        return command;
    }
}
internal sealed class CancelConverter : ICommandConverter
{
    public string TypeId => "cancel/v1";
    public Type CommandType => typeof(CancelOrder);
    public JsonElement Write(OrderCommand command)
    {
        var c = (CancelOrder)command;
        return JsonSerializer.SerializeToElement(new CancelDto(c.OrderId, c.Sequence, c.LogicalTimeMs), JournalFormat.Json);
    }
    public OrderCommand Read(JsonElement payload)
    {
        var c = payload.Deserialize<CancelDto>(JournalFormat.Json) ?? throw new JsonException("Null command.");
        var command = new CancelOrder(c.OrderId, c.Sequence, c.LogicalTimeMs);
        CommandStructure.Validate(command);
        return command;
    }
}
internal static class CommandStructure
{
    internal static void Validate(OrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.OrderId);
        if (command is PlaceLimitOrder place)
        {
            ArgumentNullException.ThrowIfNull(place.AccountId);
            if (place.Side is not (Side.Buy or Side.Sell))
                throw new ArgumentOutOfRangeException(nameof(command), "Unknown side.");
        }
        else if (command is not CancelOrder)
            throw new ArgumentException("Unsupported engine command type.", nameof(command));
    }
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandConverter> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, ICommandConverter> _types = new();

    public static CommandRegistry CreateDefault() => new CommandRegistry()
        .Register(new PlaceConverter())
        .Register(new CancelConverter());

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
