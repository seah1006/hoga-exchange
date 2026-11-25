namespace Hoga.Engine;

public interface ITickSizeRule
{
    // Immutable, deterministic rules with different behavior must expose distinct stable IDs,
    // including a new version whenever the behavior changes.
    string RuleId { get; }
    long GetTickSize(long price);
    bool IsOnTick(long price);
    long FloorToTick(long price);
}

public sealed class TickSizeTable : ITickSizeRule
{
    public string RuleId => "KRX-2023-01-25/v1";

    // R-TICK-01: 2023-01-25 이후 공통 표. 각 가격대 상한은 미포함.
    public long GetTickSize(long price) => price switch
    {
        < 2_000 => 1,
        < 5_000 => 5,
        < 20_000 => 10,
        < 50_000 => 50,
        < 200_000 => 100,
        < 500_000 => 500,
        _ => 1_000,
    };

    // R-TICK-02
    public bool IsOnTick(long price) => price % GetTickSize(price) == 0;

    public long FloorToTick(long price) => price - price % GetTickSize(price);
}
