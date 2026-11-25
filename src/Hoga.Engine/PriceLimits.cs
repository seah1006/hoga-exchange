namespace Hoga.Engine;

public static class PriceLimits
{
    public const long MinBasePrice = 1;
    public const long MaxBasePrice = 10_000_000; // 지시서 예시에 따른 임의 상한.

    public static (long Upper, long Lower, long Band) Compute(long basePrice, ITickSizeRule rule)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(basePrice, MinBasePrice);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basePrice, MaxBasePrice);
        ArgumentNullException.ThrowIfNull(rule);

        var raw = checked(basePrice * 3) / 10;
        var band = raw - raw % rule.GetTickSize(basePrice); // R-LIMIT-01
        band = Math.Max(1, band); // R-LIMIT-03
        return (rule.FloorToTick(basePrice + band), rule.FloorToTick(basePrice - band), band); // R-LIMIT-02
    }
}
