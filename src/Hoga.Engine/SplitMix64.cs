namespace Hoga.Engine;

internal struct SplitMix64(long seed)
{
    public ulong State { get; private set; } = unchecked((ulong)seed);

    public ulong Next()
    {
        unchecked
        {
            State += 0x9E3779B97F4A7C15UL;
            var value = State;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    // R-AUCT-04: reject the short prefix so every residue has exactly the same probability.
    public long NextAuctionExtensionMs()
    {
        const ulong range = 30_001;
        const ulong threshold = unchecked(0UL - range) % range;
        ulong value;
        do { value = Next(); } while (value < threshold);
        return (long)(value % range);
    }
}
