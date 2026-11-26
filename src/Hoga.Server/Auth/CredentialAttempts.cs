using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;

namespace Hoga.Server;

internal sealed class CredentialAttempts : IDisposable
{
    private readonly PartitionedRateLimiter<(bool Withdraw, string Key)> _identities =
        PartitionedRateLimiter.Create<(bool Withdraw, string Key), (bool Withdraw, string Key)>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new()
            { PermitLimit = key.Withdraw ? 5 : 8, Window = TimeSpan.FromMinutes(key.Withdraw ? 5 : 1), QueueLimit = 0 }));
    private readonly ConcurrencyLimiter _hashing = new(new() { PermitLimit = 8, QueueLimit = 0 }); // 임의 지정
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    internal string DummyHash { get; }

    internal CredentialAttempts(IPasswordHasher<Member> hasher) =>
        DummyHash = hasher.HashPassword(new(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal RateLimitLease Acquire(string key, bool withdraw = false) => _identities.AttemptAcquire((withdraw, Key(key)));
    internal RateLimitLease AcquireHash() => _hashing.AttemptAcquire();
    internal SemaphoreSlim Stripe(string key) => _stripes[Convert.ToByte(Key(key)[..2], 16) % _stripes.Length];
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose()
    {
        _identities.Dispose();
        _hashing.Dispose();
        foreach (var stripe in _stripes) stripe.Dispose();
    }
}
