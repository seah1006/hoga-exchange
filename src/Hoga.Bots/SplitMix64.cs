using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hoga.Bots;

// Reference algorithm: https://prng.di.unimi.it/splitmix64.c
internal sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public long NextBelow(long bound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bound);
        var range = (ulong)bound;
        var threshold = unchecked(0UL - range) % range;
        ulong value;
        do { value = Next(); } while (value < threshold);
        return (long)(value % range);
    }

    public static SplitMix64 ForBot(long seed, string name) => Derive(seed, "bot/" + name);
    public static SplitMix64 ForFairValue(long seed) => Derive(seed, "market/fair-value");

    // Stable derivation: seed as signed 64-bit LE, UTF-8 stream label, then first 64 digest bits as LE.
    private static SplitMix64 Derive(long seed, string label)
    {
        var input = new byte[sizeof(long) + Encoding.UTF8.GetByteCount(label)];
        BinaryPrimitives.WriteInt64LittleEndian(input, seed);
        Encoding.UTF8.GetBytes(label, input.AsSpan(sizeof(long)));
        return new(BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(input)));
    }
}
