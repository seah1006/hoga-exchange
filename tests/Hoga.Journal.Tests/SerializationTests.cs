using Hoga.Engine;
using Hoga.Journal;

namespace Hoga.Journal.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void RegisteredCommandsRoundTrip()
    {
        var registry = CommandRegistry.CreateDefault();
        OrderCommand[] commands = [new PlaceLimitOrder("a", Side.Buy, 1000, 1, "account", 1, 5), new CancelOrder("a", 2, 6)];
        foreach (var command in commands)
        {
            var encoded = registry.Encode(command);
            Assert.Equal(command, registry.Decode(encoded.Type, encoded.Payload));
        }
    }

    [Fact]
    public void UnknownDiscriminatorIsExplicit()
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.Throws<UnknownCommandTypeException>(() => registry.Decode("no-such-type/v1", default));
    }

    [Fact]
    public void HeaderRejectsIncompatibleVersionAndRule()
    {
        Assert.Throws<InvalidDataException>(() => new JournalHeader(2, 1000, new TickSizeTable().RuleId).CreateEngine());
        Assert.Throws<InvalidDataException>(() => new JournalHeader(1, 1000, "unknown").CreateEngine());
        Assert.Equal(1000, JournalHeader.Create(1000, new TickSizeTable()).CreateEngine().BasePrice);
    }
}
